using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;

namespace Momentum5.Client;

public enum SessionPhase
{
	Idle,
	Connecting,
	Backoff,
	Ready,
	ReadOnly,
	Unsupported,
	RadioOff,
}

/// <param name="Phase">where the supervisor is</param>
/// <param name="Attempt">0-based reconnect attempt (Connecting/Backoff)</param>
/// <param name="RetryDue">when the next attempt starts (Backoff)</param>
/// <param name="Detail">human-readable reason (ReadOnly, Unsupported, Backoff)</param>
/// <param name="LastTransportError">most recent transport failure, if any</param>
public sealed record SessionState(
	SessionPhase Phase,
	int Attempt = 0,
	DateTimeOffset? RetryDue = null,
	string? Detail = null,
	BtError? LastTransportError = null);

public sealed record SupervisorOptions
{
	public static IReadOnlyList<TimeSpan> DefaultLadder { get; } =
		[TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

	public TimeProvider? Time { get; init; }

	public IFrameTap? Tap { get; init; }

	public ISafetyGate? Gate { get; init; }

	public IRadioMonitor? Radio { get; init; }

	public IDevicePresence? Presence { get; init; }

	public IAdvertisementSource? Advertisements { get; init; }

	public IReadOnlyList<TimeSpan> Ladder { get; init; } = DefaultLadder;

	/// <summary>± fraction applied to every backoff delay.</summary>
	public double JitterFraction { get; init; } = 0.2;

	/// <summary>Returns [0,1); defaults to <see cref="Random.Shared"/>. Inject a constant for deterministic tests.</summary>
	public Func<double>? JitterSource { get; init; }

	public HandshakeOptions Handshake { get; init; } = new();

	/// <summary>Runs after the handshake with the session already published (initial group refresh goes here).</summary>
	public Func<ActiveSession, CancellationToken, Task>? SessionInitializer { get; init; }

	public Action<string>? Log { get; init; }
}

/// <summary>
/// Keeps one session alive: connect (ordered transports) → GaiaLink → handshake → ready → wait for loss → backoff
/// ladder, with immediate retries on wake signals (radio on, presence, advertisement, manual).
/// </summary>
public sealed class ConnectionSupervisor : IActiveSessionProvider, IAsyncDisposable
{
	private readonly IReadOnlyList<ILinkTransport> _transports;
	private readonly HeadsetStateStore _store;
	private readonly SupervisorOptions _options;
	private readonly TimeProvider _time;
	private readonly ISafetyGate _gate;
	private readonly CancellationTokenSource _cts = new();
	private readonly Lock _lock = new();
	private DeviceIdentity? _target;
	private SessionState _state = new(SessionPhase.Idle);
	private ActiveSession? _current;
	private GaiaLink? _currentLink;
	private TaskCompletionSource _wake = NewWake();
	private int _pendingWake;
	private volatile bool _paused;
	private volatile bool _devMode;
	private volatile bool _wasReady;
	private Task? _loop;

	public ConnectionSupervisor(IReadOnlyList<ILinkTransport> transports, HeadsetStateStore store, SupervisorOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(transports);
		ArgumentNullException.ThrowIfNull(store);
		_transports = transports;
		_store = store;
		_options = options ?? new SupervisorOptions();
		_time = _options.Time ?? TimeProvider.System;
		_gate = _options.Gate ?? new SafetyGate();
		if (_options.Radio is { } radio)
		{
			radio.Changed += OnRadioChanged;
		}

		if (_options.Presence is { } presence)
		{
			presence.Changed += OnPresenceChanged;
		}

		if (_options.Advertisements is { } adverts)
		{
			adverts.Sighted += OnSighted;
		}
	}

	public event EventHandler<SessionState>? StateChanged;

	public event EventHandler<ActiveSession>? SessionReady;

	public SessionState State => Volatile.Read(ref _state);

	public ActiveSession? Current => Volatile.Read(ref _current);

	public DeviceIdentity? Target => Volatile.Read(ref _target);

	/// <summary>Sets the headset to keep connected and wakes the loop.</summary>
	public void SetTarget(DeviceIdentity target)
	{
		ArgumentNullException.ThrowIfNull(target);
		Volatile.Write(ref _target, target);
		Connect();
	}

	/// <summary>Starts the supervision loop (idempotent).</summary>
	public void Start()
	{
		lock (_lock)
		{
			_loop ??= Task.Run(() => RunAsync(_cts.Token));
		}
	}

	/// <summary>Manual connect: ends a pause or backoff and tries now. No effect while a session is up.</summary>
	public void Connect()
	{
		_paused = false;
		Wake();
	}

	/// <summary>Drops the current session and stays idle (no backoff retries) until a wake source or <see cref="Connect"/> fires.</summary>
	public void PauseUntilWake()
	{
		_paused = true;
		var link = Volatile.Read(ref _currentLink);
		if (link is not null)
		{
			_ = link.DisposeAsync().AsTask();
		}
	}

	public void SetDevMode(bool enabled)
	{
		_devMode = enabled;
		if (Current is { } session)
		{
			session.Context.DevMode = enabled;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_options.Radio is { } radio)
		{
			radio.Changed -= OnRadioChanged;
		}

		if (_options.Presence is { } presence)
		{
			presence.Changed -= OnPresenceChanged;
		}

		if (_options.Advertisements is { } adverts)
		{
			adverts.Sighted -= OnSighted;
		}

		await _cts.CancelAsync().ConfigureAwait(false);
		Task? loop;
		lock (_lock)
		{
			loop = _loop;
		}

		if (loop is not null)
		{
			try
			{
				await loop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		_cts.Dispose();
	}

	private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	private void Wake()
	{
		if (State.Phase is SessionPhase.Ready or SessionPhase.ReadOnly)
		{
			return;
		}

		Volatile.Write(ref _pendingWake, 1);
		Interlocked.Exchange(ref _wake, NewWake()).TrySetResult();
	}

	private void OnRadioChanged(object? sender, bool on)
	{
		if (on)
		{
			Wake();
		}
	}

	private void OnPresenceChanged(object? sender, DeviceIdentity device)
	{
		if (device.IsConnected && Target is { } target && target.Address == device.Address)
		{
			Wake();
		}
	}

	private void OnSighted(object? sender, AdvertisementSighting sighting)
	{
		if (Target is { } target && target.Address == sighting.Address)
		{
			Wake();
		}
	}

	private void Publish(SessionState state)
	{
		Volatile.Write(ref _state, state);
		StateChanged?.Invoke(this, state);
	}

	private async Task RunAsync(CancellationToken ct)
	{
		var attempt = 0;
		BtError? lastError = null;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				Volatile.Write(ref _pendingWake, 0);
				if (Target is null)
				{
					Publish(new SessionState(SessionPhase.Idle, Detail: "No target device."));
					await WaitWakeAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
					continue;
				}

				if (_options.Radio is { } radio && !await radio.IsOnAsync(ct).ConfigureAwait(false))
				{
					Publish(new SessionState(SessionPhase.RadioOff, Detail: "Bluetooth is off."));
					await WaitWakeAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
					continue;
				}

				Publish(new SessionState(SessionPhase.Connecting, attempt, LastTransportError: lastError));
				_wasReady = false;
				string? failure = null;
				try
				{
					await RunSessionAsync(Target, ct).ConfigureAwait(false);
				}
				catch (UnsupportedDeviceException ex)
				{
					Publish(new SessionState(SessionPhase.Unsupported, Detail: ex.Message));
					await WaitWakeAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
					continue;
				}
				catch (Exception ex) when (ex is LinkException or TimeoutException or GaiaProtocolException or SafetyDeniedException
					or GaiaDecodeException or InvalidOperationException or IOException)
				{
					failure = ex.Message;
					if (ex is LinkException link)
					{
						lastError = link.Error;
					}

					_options.Log?.Invoke($"Session failed: {ex.Message}");
				}

				if (_paused)
				{
					Publish(new SessionState(SessionPhase.Idle, Detail: "Paused until the headset wakes."));
					await WaitWakeAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
					_paused = false;
					attempt = 0;
					continue;
				}

				if (_wasReady)
				{
					attempt = 0;
				}

				var delay = NextDelay(attempt);
				Publish(new SessionState(SessionPhase.Backoff, attempt, _time.GetUtcNow() + delay, failure ?? "Link lost.", lastError));
				await WaitWakeAsync(delay, ct).ConfigureAwait(false);
				attempt++;
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
		}
	}

	private TimeSpan NextDelay(int attempt)
	{
		var ladder = _options.Ladder;
		var baseDelay = ladder[Math.Min(attempt, ladder.Count - 1)];
#pragma warning disable CA5394 // jitter only spreads reconnect attempts
		var r = _options.JitterSource?.Invoke() ?? Random.Shared.NextDouble();
#pragma warning restore CA5394
		var factor = 1 + (_options.JitterFraction * ((2 * r) - 1));
		return TimeSpan.FromTicks((long)(baseDelay.Ticks * factor));
	}

	private async Task WaitWakeAsync(TimeSpan delay, CancellationToken ct)
	{
		var wake = Volatile.Read(ref _wake);
		if (Interlocked.Exchange(ref _pendingWake, 0) != 0)
		{
			return;
		}

		using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var timer = delay == Timeout.InfiniteTimeSpan
			? Task.Delay(Timeout.InfiniteTimeSpan, timerCts.Token)
			: Task.Delay(delay, _time, timerCts.Token);
		await Task.WhenAny(wake.Task, timer).ConfigureAwait(false);
		await timerCts.CancelAsync().ConfigureAwait(false);
		Volatile.Write(ref _pendingWake, 0);
		ct.ThrowIfCancellationRequested();
	}

	private async Task<(ILinkConnection Connection, ILinkTransport Transport)> ConnectAnyAsync(DeviceIdentity target, CancellationToken ct)
	{
		LinkException? last = null;
		foreach (var transport in _transports)
		{
			try
			{
				return (await transport.ConnectAsync(target, ct).ConfigureAwait(false), transport);
			}
			catch (LinkException ex)
			{
				last = ex;
				_options.Log?.Invoke($"{transport.Id}: {ex.Message}");
			}
		}

		if (last is null)
		{
			throw new InvalidOperationException("No transports configured.");
		}

		throw last;
	}

	private async Task RunSessionAsync(DeviceIdentity target, CancellationToken ct)
	{
		var (connection, transport) = await ConnectAnyAsync(target, ct).ConfigureAwait(false);
		var link = new GaiaLink(connection, transport.Framing, _options.Tap);
		Volatile.Write(ref _currentLink, link);
		CommandPipeline? pipeline = null;
		NotificationHub? hub = null;
		IDisposable? subscription = null;
		try
		{
			var context = new SessionContext { DevMode = _devMode };
			pipeline = new CommandPipeline(link, _gate, context, _time, _options.Log);
			hub = new NotificationHub(pipeline, _options.Log);
			subscription = _store.Attach(hub);

			var info = await new Handshake(pipeline, hub, context, _options.Handshake, _options.Log)
				.RunAsync(transport.Id, ct).ConfigureAwait(false);
			_store.SetSession(info);
			var session = new ActiveSession(pipeline, hub, context, info);
			Volatile.Write(ref _current, session);
			if (_options.SessionInitializer is { } initialize)
			{
				await initialize(session, ct).ConfigureAwait(false);
			}

			_wasReady = true;
			Publish(info.Trust == TrustTier.Unknown
				? new SessionState(SessionPhase.ReadOnly, Detail: "Firmware not verified: read-only.")
				: new SessionState(SessionPhase.Ready));
			SessionReady?.Invoke(this, session);

			await link.Closed.WaitAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			Volatile.Write(ref _current, null);
			Volatile.Write(ref _currentLink, null);
			subscription?.Dispose();
			hub?.Detach();
			_store.Reset();
			if (pipeline is not null)
			{
				await pipeline.DisposeAsync().ConfigureAwait(false);
			}

			await link.DisposeAsync().ConfigureAwait(false);
		}
	}
}
