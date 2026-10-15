using System.Threading.Channels;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Simulation;

/// <summary>
/// In-memory MOMENTUM 5 (M5AEBT). Answers GAIA requests generically from <see cref="GaiaCatalog"/> bindings over a
/// state dictionary seeded with M5 defaults. Quirks and failure knobs live in FakeM5Behaviors.cs.
/// </summary>
public sealed partial class FakeM5
{
	private const byte ReasonFeature = 0x00;
	private const byte ReasonOperation = 0x01;
	private const byte ReasonMalformed = 0x05;
	private const byte ReasonIndex = 0x83;

	// [feature, version] pairs as the M5 reports them (synthetic: only the feature set matters to the client).
	private static readonly byte[] SennheiserFeatures = Convert.FromHexString("00090108061B0103021002040402010A0414010B020C020D020005");
	private static readonly byte[] QualcommFeatures = Convert.FromHexString("000D010C010602040207010005");

	private readonly FakeM5Options _options;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly Dictionary<(string Name, int Index), object> _state = [];
	private readonly HashSet<(GaiaVendor Vendor, byte Feature)> _registered = [];
	private readonly Dictionary<string, Queue<byte[]>> _replay = [];
	private DateTimeOffset _busyUntil;
	private Channel<byte[]>? _outbox;

	public FakeM5(FakeM5Options? options = null, TimeProvider? time = null)
	{
		_options = options ?? new FakeM5Options();
		_time = time ?? TimeProvider.System;
		SeedState();
		foreach (var exchange in _options.Replay)
		{
			var key = Convert.ToHexString(exchange.Tx);
			if (!_replay.TryGetValue(key, out var queue))
			{
				_replay[key] = queue = new Queue<byte[]>();
			}

			queue.Enqueue(exchange.Rx);
		}
	}

	public FakeM5Options Options => _options;

	/// <summary>Serves every connection made through <paramref name="transport"/>.</summary>
	public void Attach(LoopbackTransport transport)
	{
		ArgumentNullException.ThrowIfNull(transport);
		transport.OnConnect = connection => RunAsync(connection);
	}

	/// <summary>Device loop: deframe requests, answer, push. Returns when the link closes or <paramref name="ct"/> fires.</summary>
	public async Task RunAsync(ILinkConnection device, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(device);

		// No awaits before the first ReadAsync: state is armed by the time ConnectAsync returns.
		var box = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
		lock (_gate)
		{
			_registered.Clear();
			_busyUntil = _time.GetUtcNow() + _options.BusyWindow;
			_outbox = box;
		}

		var pump = PumpAsync(box, device, ct);
		var deframer = new GaiaDeframer();
		var buffer = new byte[512];
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var n = await device.ReadAsync(buffer, ct).ConfigureAwait(false);
				if (n == 0)
				{
					break;
				}

				deframer.Append(buffer.AsSpan(0, n));
				while (deframer.TryRead(out var frame))
				{
					await HandleAsync(frame, ct).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (LinkException)
		{
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_outbox, box))
				{
					_outbox = null;
				}
			}

			box.Writer.TryComplete();
			await pump.ConfigureAwait(false);
		}
	}

	public T GetState<T>(SettingKey<T> key)
	{
		ArgumentNullException.ThrowIfNull(key);
		lock (_gate)
		{
			return (T)_state[(key.Name, key.Index)];
		}
	}

	public void SetState<T>(SettingKey<T> key, T value)
	{
		ArgumentNullException.ThrowIfNull(key);
		lock (_gate)
		{
			_state[(key.Name, key.Index)] = value!;
		}
	}

	/// <summary>Untyped state write (used to prime the fake from decoded captures).</summary>
	public void Prime(string name, int index, object value)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(value);
		lock (_gate)
		{
			_state[(name, index)] = value;
		}
	}

	public bool IsRegistered(GaiaVendor vendor, byte feature)
	{
		lock (_gate)
		{
			return _registered.Contains((vendor, feature));
		}
	}

	/// <summary>Device-initiated change: updates state and pushes it when its feature is registered. Returns true if a push was queued.</summary>
	public bool Notify<T>(SettingKey<T> key, T value)
	{
		ArgumentNullException.ThrowIfNull(key);
		lock (_gate)
		{
			_state[(key.Name, key.Index)] = value!;
			var binding = GaiaCatalog.Registry.ByName(key.Name);
			if (binding is null)
			{
				return false;
			}

			var frames = Pushes([binding]);
			foreach (var frame in frames)
			{
				EnqueueFrame(frame);
			}

			return frames.Count > 0;
		}
	}

	private static async Task PumpAsync(Channel<byte[]> box, ILinkConnection device, CancellationToken ct)
	{
		try
		{
			await foreach (var bytes in box.Reader.ReadAllAsync(ct).ConfigureAwait(false))
			{
				await device.WriteAsync(bytes, ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (LinkException)
		{
		}
	}

	private static GaiaFrame Ok(GaiaFrame request, byte[] payload) =>
		new(request.Vendor, GaiaWord.ResponseOf(request.Command), payload);

	private static GaiaFrame Error(GaiaFrame request, byte reason) =>
		new(request.Vendor, GaiaWord.ErrorOf(request.Command), new byte[] { reason });

	private void SeedState()
	{
		foreach (var b in GaiaCatalog.Bindings)
		{
			if (!b.Indexed && b.GetCmd is not null && b.SampleValues.Count > 0)
			{
				_state[(b.Name, -1)] = b.SampleValues[0];
			}
		}

		_state[(Keys.Model.Name, -1)] = _options.Model;
		_state[(Keys.Firmware.Name, -1)] = _options.Firmware;
		_state[(Keys.Serial.Name, -1)] = _options.Serial;
		_state[(Keys.BatteryPercent.Name, -1)] = 70;
		_state[(Keys.AncEnabled.Name, -1)] = true;
		_state[(Keys.AncLevel.Name, -1)] = 0;
		_state[(Keys.TransparencyLevel.Name, -1)] = 100;
		_state[(Keys.EqConfig.Name, -1)] = new EqConfig(8, -6.0, 6.0);
		_state[(Keys.EqAllGains.Name, -1)] = new double[8];
		_state[(Keys.SoundMode.Name, -1)] = SoundMode.GraphicEq;

		int[] freqs = [90, 325, 1500, 2500, 4500, 8000, 13000, 16000];
		for (var i = 0; i < freqs.Length; i++)
		{
			_state[(Keys.EqBand(0).Name, i)] = 0.0;
			_state[(Keys.EqBandFreq(0).Name, i)] = freqs[i];
		}
	}

	private async Task HandleAsync(GaiaFrame frame, CancellationToken ct)
	{
		bool busy;
		lock (_gate)
		{
			busy = _time.GetUtcNow() < _busyUntil;
		}

		if (busy)
		{
			return; // still booting: the request is silently lost
		}

		var delay = _options.CommandLatency;
		if (_options.PerCommandLatency.TryGetValue(GaiaWord.AsCommand(frame.Command), out var perCommand))
		{
			delay = perCommand;
		}

		if (delay > TimeSpan.Zero)
		{
			await Task.Delay(delay, _time, ct).ConfigureAwait(false);
		}

		lock (_gate)
		{
			if (TryReplay(frame))
			{
				return;
			}

			foreach (var reply in Process(frame))
			{
				EnqueueFrame(reply);
			}
		}
	}

	private List<GaiaFrame> Process(GaiaFrame frame)
	{
		if (GaiaWord.Kind(frame.Command) != GaiaMessageKind.Command)
		{
			return [];
		}

		var cmd = GaiaWord.AsCommand(frame.Command);
		var payload = frame.Payload.Span;

		if (cmd == Registration.RegisterCommand)
		{
			if (payload.Length != 1)
			{
				return [Error(frame, ReasonMalformed)];
			}

			_registered.Add((frame.Vendor, payload[0]));
			return [Ok(frame, [])];
		}

		if (frame.Vendor == GaiaVendor.Qualcomm && cmd == 0x0000)
		{
			return [Ok(frame, _options.ApiVersionPayload)];
		}

		if (cmd == 0x0001)
		{
			return [Ok(frame, frame.Vendor == GaiaVendor.Qualcomm ? QualcommFeatures : SennheiserFeatures)];
		}

		var candidates = GaiaCatalog.Registry.ByCommand(frame.Vendor, cmd);
		var getters = candidates.Where(b => b.GetCmd is { } g && GaiaWord.AsCommand(g) == cmd).ToList();
		if (getters.Count > 0)
		{
			return [Get(frame, getters)];
		}

		var setters = candidates.Where(b => b.SetCmd is { } s && GaiaWord.AsCommand(s) == cmd).ToList();
		return setters.Count > 0 ? Set(frame, setters) : [Error(frame, ReasonFeature)];
	}

	private GaiaFrame Get(GaiaFrame frame, List<ISettingBinding> getters)
	{
		var body = new List<byte>();
		var matched = false;
		foreach (var b in getters)
		{
			if (!b.TryParseGet(frame.Payload.Span, out var index))
			{
				continue;
			}

			matched = true;
			if (!_state.TryGetValue((b.Name, b.Indexed ? index : -1), out var value))
			{
				return Error(frame, b.Indexed ? ReasonIndex : ReasonOperation);
			}

			if (!b.TryEncodeResponse(value, index, out var encoded))
			{
				return Error(frame, ReasonMalformed);
			}

			body.AddRange(encoded);
		}

		return matched ? Ok(frame, [.. body]) : Error(frame, ReasonMalformed);
	}

	private List<GaiaFrame> Set(GaiaFrame frame, List<ISettingBinding> setters)
	{
		if (_options.RefuseWrites)
		{
			return [Error(frame, ReasonOperation)];
		}

		foreach (var b in setters)
		{
			if (!b.TryDecodeSet(frame.Payload.Span, out var index, out var value) || value is null)
			{
				continue;
			}

			var slot = b.Indexed ? index : -1;
			if (b.Indexed && b.GetCmd is not null && !_state.ContainsKey((b.Name, slot)))
			{
				return [Error(frame, ReasonIndex)];
			}

			_state[(b.Name, slot)] = value;
			var changed = new List<ISettingBinding> { b };
			ApplyCouplings(b, value, changed);

			List<GaiaFrame> frames = [Ok(frame, [])];
			frames.AddRange(Pushes(changed));
			return frames;
		}

		return [Error(frame, ReasonMalformed)];
	}

	private List<GaiaFrame> Pushes(IEnumerable<ISettingBinding> changed)
	{
		var seen = new HashSet<(GaiaVendor, ushort)>();
		var frames = new List<GaiaFrame>();
		foreach (var b in changed)
		{
			if (b.PushCmd is not { } push || b.Indexed || !_registered.Contains((b.Vendor, (byte)(push >> 9))) || !seen.Add((b.Vendor, push)))
			{
				continue;
			}

			var body = new List<byte>();
			foreach (var sibling in GaiaCatalog.Registry.ByCommand(b.Vendor, push))
			{
				if (sibling.PushCmd == push && !sibling.Indexed
					&& _state.TryGetValue((sibling.Name, -1), out var value)
					&& sibling.TryEncodeResponse(value, -1, out var encoded))
				{
					body.AddRange(encoded);
				}
			}

			frames.Add(new GaiaFrame(b.Vendor, push, body.ToArray()));
		}

		return frames;
	}

	private void EnqueueFrame(GaiaFrame frame)
	{
		var bytes = frame.Encode();
		if (Interlocked.Exchange(ref _corruptNext, 0) == 1)
		{
			bytes = [0x00, 0x7E, .. bytes];
		}

		Enqueue(bytes);
	}

	private void Enqueue(byte[] bytes) => _outbox?.Writer.TryWrite(bytes);
}
