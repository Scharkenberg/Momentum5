using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Client;

public sealed record CommandOptions
{
	public TimeSpan Timeout { get; init; } = BindingDefaults.Timeout;

	/// <summary>Extra time (<see cref="CommandPipeline.GraceExtension"/>) when the last write was less than this ago.</summary>
	public TimeSpan BusyGrace { get; init; }

	/// <summary>DevTools GET-style probe of an Unknown command (never a write).</summary>
	public bool IsProbe { get; init; }

	public ConsentToken? Consent { get; init; }

	public OverrideToken? Override { get; init; }
}

/// <summary>
/// Single-flight request/response over a <see cref="GaiaLink"/>. Every frame passes the safety gate first.
/// Unsolicited frames (pushes) go to <see cref="Unsolicited"/>; responses nobody waits for go to <see cref="LateFrame"/>.
/// </summary>
public sealed class CommandPipeline : IAsyncDisposable
{
	public static readonly TimeSpan GraceExtension = TimeSpan.FromSeconds(2);

	private readonly GaiaLink _link;
	private readonly ISafetyGate _gate;
	private readonly SessionContext _session;
	private readonly TimeProvider _time;
	private readonly Action<string>? _log;
	private readonly SemaphoreSlim _single = new(1, 1);
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _pump;
	private volatile Pending? _pending;
	private DateTimeOffset? _lastWrite;

	public CommandPipeline(GaiaLink link, ISafetyGate gate, SessionContext session, TimeProvider? time = null, Action<string>? log = null)
	{
		ArgumentNullException.ThrowIfNull(link);
		ArgumentNullException.ThrowIfNull(gate);
		ArgumentNullException.ThrowIfNull(session);
		_link = link;
		_gate = gate;
		_session = session;
		_time = time ?? TimeProvider.System;
		_log = log;
		_pump = Task.Run(() => PumpAsync(_cts.Token));
	}

	public event EventHandler<GaiaFrame>? Unsolicited;

	public event EventHandler<GaiaFrame>? LateFrame;

	public SessionContext Session => _session;

	public async ValueTask<GaiaFrame> ExecuteAsync(
		GaiaVendor vendor,
		ushort command,
		ReadOnlyMemory<byte> payload,
		CommandOptions? options = null,
		CancellationToken ct = default)
	{
		options ??= new CommandOptions();
		var decision = _gate.Check(vendor, command, !options.IsProbe, _session.Trust, _session.DevMode, options.Consent, options.Override);
		if (!decision.IsAllowed)
		{
			throw new SafetyDeniedException(decision);
		}

		var safety = GaiaCatalog.SafetyOf(vendor, command);
		var attempts = safety == SafetyClass.Read ? 2 : 1;
		await _single.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					return await SendOnceAsync(vendor, command, payload, options, safety, ct).ConfigureAwait(false);
				}
				catch (TimeoutException) when (attempt < attempts)
				{
					_log?.Invoke($"Timeout on {vendor}:{GaiaWord.AsCommand(command):X4}; retrying once.");
				}
			}
		}
		finally
		{
			_single.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _cts.CancelAsync().ConfigureAwait(false);
		try
		{
			await _pump.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}

		_single.Dispose();
		_cts.Dispose();
	}

	private async ValueTask<GaiaFrame> SendOnceAsync(
		GaiaVendor vendor,
		ushort command,
		ReadOnlyMemory<byte> payload,
		CommandOptions options,
		SafetyClass safety,
		CancellationToken ct)
	{
		var baseCommand = GaiaWord.AsCommand(command);
		var tcs = new TaskCompletionSource<GaiaFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending = new Pending(vendor, baseCommand, tcs);
		GaiaFrame reply;
		try
		{
			var timeout = options.Timeout;
			var now = _time.GetUtcNow();
			if (options.BusyGrace > TimeSpan.Zero && _lastWrite is { } last && now - last < options.BusyGrace)
			{
				timeout += GraceExtension;
			}

			if (safety != SafetyClass.Read)
			{
				_lastWrite = now;
			}

			await _link.SendAsync(new GaiaFrame(vendor, command, payload), ct).ConfigureAwait(false);

			using var timeoutCts = new CancellationTokenSource(timeout, _time);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
			try
			{
				reply = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
			{
				throw new TimeoutException($"No response to {vendor}:{baseCommand:X4} within {timeout.TotalSeconds:0.#} s.");
			}
		}
		finally
		{
			_pending = null;
		}

		if (GaiaProtocolException.FromFrame(reply) is { } error)
		{
			throw error;
		}

		return reply;
	}

	private async Task PumpAsync(CancellationToken ct)
	{
		try
		{
			await foreach (var frame in _link.Frames.ReadAllAsync(ct).ConfigureAwait(false))
			{
				Route(frame);
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_pending?.Tcs.TrySetException(
				new LinkException(BtError.ConnectionAborted, unchecked((int)0x80072745), "The link closed while a command was pending."));
		}
	}

	private void Route(GaiaFrame frame)
	{
		var (baseCommand, kind) = frame.Classify();
		if (kind is GaiaMessageKind.Response or GaiaMessageKind.Error)
		{
			var pending = _pending;
			if (pending is not null && pending.Vendor == frame.Vendor && pending.Base == baseCommand && pending.Tcs.TrySetResult(frame))
			{
				return;
			}

			_log?.Invoke($"Late or unexpected reply discarded: {GaiaFormatter.Describe(frame, FrameDirection.Rx)}");
			LateFrame?.Invoke(this, frame);
			return;
		}

		Unsolicited?.Invoke(this, frame);
	}

	private sealed record Pending(GaiaVendor Vendor, ushort Base, TaskCompletionSource<GaiaFrame> Tcs);
}

public static class PipelineExtensions
{
	public static CommandOptions OptionsFor(ISettingBinding binding)
	{
		ArgumentNullException.ThrowIfNull(binding);
		return new CommandOptions { Timeout = binding.Timeout, BusyGrace = binding.BusyGrace };
	}

	public static ValueTask<GaiaFrame> GetFrameAsync(this CommandPipeline pipeline, ISettingBinding binding, int index, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		ArgumentNullException.ThrowIfNull(binding);
		var get = binding.GetCmd ?? throw new InvalidOperationException($"{binding.Name} has no GET command.");
		return pipeline.ExecuteAsync(binding.Vendor, get, binding.BuildGetPayload(index), OptionsFor(binding), ct);
	}

	/// <summary>Reads and decodes one key (no state update).</summary>
	public static async ValueTask<T> GetAsync<T>(this CommandPipeline pipeline, SettingKey<T> key, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		ArgumentNullException.ThrowIfNull(key);
		var binding = GaiaCatalog.Registry.Find(key) ?? throw new ArgumentException($"No binding for {key.Name}.", nameof(key));
		var frame = await pipeline.GetFrameAsync(binding, key.Index, ct).ConfigureAwait(false);
		return binding.Format.TryDecode(frame.Payload.Span, key.Index, out var value)
			? value
			: throw new GaiaDecodeException(key.Name, GaiaFormatter.Hex(frame.Payload.Span));
	}
}
