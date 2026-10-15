using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Simulation;

/// <summary>Recorded request/response bytes replayed verbatim (capture-replay mode).</summary>
public sealed record ReplayExchange(byte[] Tx, byte[] Rx);

/// <summary>Independent quirk and failure knobs for <see cref="FakeM5"/>. Defaults mimic a healthy M5 fw 6.21.5.</summary>
public sealed record FakeM5Options
{
	public string Model { get; init; } = "M5AEBT Black";

	public FirmwareVersion Firmware { get; init; } = new(6, 21, 5);

	public string Serial { get; init; } = "0000000000";

	/// <summary>Delay before every answer (fake or real time).</summary>
	public TimeSpan CommandLatency { get; init; }

	/// <summary>Per base command word; overrides <see cref="CommandLatency"/> (e.g. 0x0603 → 9 s battery).</summary>
	public IReadOnlyDictionary<ushort, TimeSpan> PerCommandLatency { get; init; } = new Dictionary<ushort, TimeSpan>();

	/// <summary>After connect or power cycle the device drops every command for this long.</summary>
	public TimeSpan BusyWindow { get; init; }

	/// <summary>Read-only firmware: every SET is answered with error 01.</summary>
	public bool RefuseWrites { get; init; }

	/// <summary>Quirk: writing TransparencyStatus also moves AncLevel (100 on / 0 off).</summary>
	public bool CoupleLevelToTransparency { get; init; } = true;

	/// <summary>Quirk: transparency (and the ANC level) fall back to off after <see cref="FakeM5.PowerCycle"/>.</summary>
	public bool TransparencyResetsOnPowerCycle { get; init; } = true;

	/// <summary>Exact request bytes → recorded response bytes; wins over generic handling.</summary>
	public IReadOnlyList<ReplayExchange> Replay { get; init; } = [];

	/// <summary>Synthetic answer to Q:0000 (GetApiVersion).</summary>
	public byte[] ApiVersionPayload { get; init; } = [0x03, 0x00];
}

public sealed partial class FakeM5
{
	private int _corruptNext;

	/// <summary>The next response/push is preceded by 2 garbage bytes (the client deframer must resync).</summary>
	public void CorruptNextResponse() => Interlocked.Exchange(ref _corruptNext, 1);

	/// <summary>Queues raw bytes (default 3 bytes without 0xFF) that are not a frame.</summary>
	public void InjectGarbage(byte[]? bytes = null) => Enqueue(bytes ?? [0x00, 0x01, 0x02]);

	/// <summary>Simulates power-off/on: registrations are lost, the boot busy window restarts, transparency resets.</summary>
	public void PowerCycle()
	{
		lock (_gate)
		{
			_registered.Clear();
			_busyUntil = _time.GetUtcNow() + _options.BusyWindow;
			if (_options.TransparencyResetsOnPowerCycle)
			{
				_state[(Keys.TransparencyStatus.Name, -1)] = false;
				_state[(Keys.AncLevel.Name, -1)] = 0;
			}
		}
	}

	private bool TryReplay(GaiaFrame request)
	{
		if (_replay.Count == 0 || !_replay.TryGetValue(Convert.ToHexString(request.Encode()), out var queue))
		{
			return false;
		}

		Enqueue(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
		return true;
	}

	private void ApplyCouplings(ISettingBinding binding, object value, List<ISettingBinding> changed)
	{
		if (binding.Name == Keys.TransparencyStatus.Name && value is bool on && _options.CoupleLevelToTransparency)
		{
			_state[(Keys.AncLevel.Name, -1)] = on ? 100 : 0;
			if (GaiaCatalog.Registry.ByName(Keys.AncLevel.Name) is { } level)
			{
				changed.Add(level);
			}
		}
		else if (binding.Name == Keys.EqBand(0).Name)
		{
			var gains = new double[8];
			for (var i = 0; i < gains.Length; i++)
			{
				gains[i] = _state.TryGetValue((binding.Name, i), out var g) && g is double d ? d : 0.0;
			}

			_state[(Keys.EqAllGains.Name, -1)] = gains;
		}
	}
}
