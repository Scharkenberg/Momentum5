#pragma warning disable CA5351 // MD5 is mandated by the Qualcomm upgrade protocol / package manifest, not used for security
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>Phase of one firmware upgrade session (docs/PROTOCOL-upgrade.md §4). One <see cref="UpgradeSession"/>
/// instance walks these states at most once per terminal outcome.</summary>
public enum UpgradeSessionState
{
	/// <summary>Constructed, no run started yet.</summary>
	Idle,

	/// <summary>Battery gate, package and profile validation — no frame has left the app.</summary>
	Precheck,

	/// <summary>Acquiring the pipeline and opening the VM-upgrade tunnel (tunnel connect word).</summary>
	Connect,

	/// <summary>VMU synchronization (SYNC_REQ/SYNC_CFM) and device readiness (START_REQ/START_CFM).</summary>
	AwaitReady,

	/// <summary>Start of the data transfer phase (START_DATA_REQ) after resume point 0.</summary>
	Start,

	/// <summary>Device-pulled chunked transfer (DATA_BYTES_REQ windows, UPGRADE_DATA packets, validation loop).</summary>
	Transfer,

	/// <summary>Post-transfer confirmations: TRANSFER_COMPLETE_RES, the reboot, IN_PROGRESS_RES/COMMIT_CFM, COMPLETE_IND.</summary>
	Commit,

	/// <summary>Completion phase: UPGRADE_COMPLETE_IND observed (or timed out) and the tunnel disconnect sent.</summary>
	End,

	/// <summary>Waiting for the headset to reboot and the link to come back (expected after commit confirmations,
	/// or after an unexpected mid-session link loss — the run is then resumable via ResumeAsync).</summary>
	Reconnect,

	/// <summary>Firmware version re-read and comparison against the expected version.</summary>
	Verify,

	/// <summary>Terminal success.</summary>
	Done,

	/// <summary>Terminal failure (see <see cref="UpgradeSessionException.Reason"/>).</summary>
	Failed,

	/// <summary>Terminal: the caller cancelled (best-effort VMU abort sequence was sent).</summary>
	Aborted,
}

/// <summary>Failure classes of <see cref="UpgradeSession"/> — each maps to one recovery hint on the firmware page (X-13).</summary>
public enum UpgradeSessionFailure
{
	/// <summary>A run is already in flight on this session instance (single-flight guard).</summary>
	AlreadyRunning,

	/// <summary>This session instance already reached a terminal state — create a new one (or ResumeAsync, where allowed).</summary>
	SessionEnded,

	/// <summary>No live headset session: the pipeline provider returned null.</summary>
	NoConnection,

	/// <summary>The battery level could not be determined — the session refuses to start (bucket rule: unknown = refuse).</summary>
	BatteryUnknown,

	/// <summary>The battery is at or below the profile floor (default 30 %).</summary>
	BatteryLow,

	/// <summary>The consent token factory returned no (or a mismatched) token for a Dangerous frame — nothing was sent.</summary>
	ConsentRequired,

	/// <summary>The profile does not carry a word the session needed — refusing to send an unobserved command (ref/04 rule 3, R-06 pending).</summary>
	UnobservedCommand,

	/// <summary>The safety gate refused a frame (DevMode off, or the consent token was rejected/expired).</summary>
	GateDenied,

	/// <summary>The prepared package is unusable (not MD5-verified, empty, unreadable, or mismatching saved progress).</summary>
	PackageInvalid,

	/// <summary>The device stayed "not ready" through all START retries.</summary>
	DeviceNotReady,

	/// <summary>The device rejected a command or reported a fatal VMU error/warning code.</summary>
	DeviceRejected,

	/// <summary>The device confirmed an abort (UPGRADE_ABORT_CFM) without the host asking for one.</summary>
	DeviceAborted,

	/// <summary>The link was lost mid-session — resumable: persist <see cref="UpgradeProgress"/> and call ResumeAsync.</summary>
	LinkLost,

	/// <summary>A VMU reply did not arrive in time — resumable via ResumeAsync.</summary>
	ReplyTimeout,

	/// <summary>The headset did not come back within the reconnect window after the commit — resumable via ResumeAsync.</summary>
	ReconnectFailed,

	/// <summary>A malformed or unexpected VMU message (bad lengths, unknown resume point, non-converging resume points).</summary>
	ProtocolError,

	/// <summary>The device restarted the transfer from zero although bytes were already sent, and the profile policy says refuse.</summary>
	RestartRefused,

	/// <summary>The post-upgrade version re-read does not equal the expected version.</summary>
	VersionMismatch,
}

/// <summary>
/// Typed error for the firmware upgrade session (house rule: pages surface exceptions), mirroring
/// <see cref="FirmwarePackageException"/>. <see cref="Reason"/> selects the recovery text; the message is user-presentable.
/// </summary>
public sealed class UpgradeSessionException(UpgradeSessionFailure reason, string message, Exception? innerException = null)
	: Exception(message, innerException)
{
	/// <summary>Failure class; see <see cref="UpgradeSessionFailure"/>.</summary>
	public UpgradeSessionFailure Reason { get; } = reason;
}

/// <summary>
/// Persistable progress snapshot of an upgrade run (the caller owns persistence — X-11 never writes state itself).
/// Feed the last snapshot back into <see cref="UpgradeSession.ResumeAsync"/> after a resumable failure.
/// </summary>
/// <param name="State">Current session phase.</param>
/// <param name="BytesSent">Image bytes handed to the device so far (the transfer offset).</param>
/// <param name="TotalBytes">Total staged image size.</param>
/// <param name="ChunksSent">UPGRADE_DATA packets sent so far.</param>
/// <param name="TimestampUtc">When this snapshot was taken.</param>
public sealed record UpgradeProgress(
	UpgradeSessionState State,
	long BytesSent,
	long TotalBytes,
	int ChunksSent,
	DateTimeOffset TimestampUtc);

/// <summary>One recorded state transition, for diagnostics (the session logs every phase step and protocol milestone).</summary>
public sealed record UpgradeTransition(UpgradeSessionState From, UpgradeSessionState To, string Note, DateTimeOffset AtUtc);

/// <summary>Outcome of a successful <see cref="UpgradeSession"/> run.</summary>
/// <param name="DeviceVersion">Firmware the device reports after the upgrade (null when the profile carries no version read word).</param>
/// <param name="BytesSent">Image bytes transferred.</param>
/// <param name="TotalBytes">Total staged image size.</param>
/// <param name="ChunksSent">UPGRADE_DATA packets sent.</param>
/// <param name="CompletedAtUtc">When the session reached <see cref="UpgradeSessionState.Done"/>.</param>
public sealed record UpgradeResult(
	FirmwareVersion? DeviceVersion,
	long BytesSent,
	long TotalBytes,
	int ChunksSent,
	DateTimeOffset CompletedAtUtc);

/// <summary>
/// Firmware stage 2a (X-11): the pure VM-upgrade/DFU state machine over the GAIA tunnel, implementing exactly the
/// sequence reconstructed in docs/PROTOCOL-upgrade.md (Qualcomm VMU family). Pure means: no WinRT, no UI, no DI
/// registration — every dependency is an injected delegate.
///
/// <b>Unobserved-command rule:</b> the machine only ever sends words carried by its
/// <see cref="UpgradeProtocolProfile"/>; a missing word fails with
/// <see cref="UpgradeSessionFailure.UnobservedCommand"/> instead of inventing bytes (ref/04 rule 3 — no M5 OTA
/// capture exists yet, R-06 BLOCKED-HW; X-12 replay pins the exact sequence).
///
/// <b>Consent:</b> every frame goes through <see cref="CommandPipeline.ExecuteAsync"/> — no bypass. The tunnel words
/// are catalog <c>Dangerous</c> class, and <see cref="SafetyGate"/> consumes single-use tokens <i>per send</i>, so the
/// injected token factory is called before <i>every</i> Dangerous frame (the first one included) and must return a
/// token bound to (profile vendor, that word); typically it wraps <see cref="ConsentService"/>.Issue. A null or
/// mismatched token fails with <see cref="UpgradeSessionFailure.ConsentRequired"/> before any byte leaves the app.
/// DevMode must be enabled (Advanced hub) or the gate refuses the tunnel outright.
///
/// <b>Battery:</b> the injected provider must report a percentage strictly above the profile floor (default 30 %);
/// null/unknown refuses the start. The device re-checks its own battery in UPGRADE_START_CFM and via
/// ERROR_BATTERY_LOW [REF].
///
/// <b>Image input</b> is the X-10 output only: <see cref="PreparedPackage"/> (the MD5 gate already passed — the
/// session re-checks <see cref="PreparedPackage.Md5Verified"/> defensively). The image bytes are read through the
/// injectable loader (default: <see cref="File.ReadAllBytesAsync"/> on <see cref="PreparedPackage.PlainImagePath"/> —
/// the single file access this class performs; it never writes anything).
///
/// <b>Resumable:</b> a mid-transfer link loss ends the run in <see cref="UpgradeSessionState.Reconnect"/> with a
/// typed <see cref="UpgradeSessionFailure.LinkLost"/>; persist the last <see cref="Current"/> progress and call
/// <see cref="ResumeAsync"/> once the supervisor re-established the link — the machine re-syncs and follows the
/// device's resume point (docs/PROTOCOL-upgrade.md §5). X-13 wires the page; X-13/X-12 own captures and replay.
/// </summary>
public sealed class UpgradeSession
{
	private const int MaxDispatchDepth = 8;

	// UPGRADE_START_CFM status values [REF GAIAControl OpCodes.UpgradeStartCFM.Status].
	private const byte StartStatusSuccess = 0x00;
	private const byte StartStatusNotReady = 0x09;

	// UPGRADE_DATA first payload byte [REF GAIAControl OpCodes.UpgradeData.LastPacket].
	private const byte IsLastPacket = 0x01;

	// VMU return codes [REF GAIAControl ReturnCodes].
	private const ushort WarnSyncIdIsDifferent = 0x81;
	private const ushort ErrorBatteryLow = 0x21;

	// Resume points [REF GAIAControl ResumePoints].
	private const byte ResumePointDataTransfer = 0x00;
	private const byte ResumePointValidation = 0x01;
	private const byte ResumePointTransferComplete = 0x02;
	private const byte ResumePointInProgress = 0x03;
	private const byte ResumePointCommit = 0x04;

	private readonly UpgradeProtocolProfile _profile;
	private readonly Func<CommandPipeline?> _pipelineProvider;
	private readonly Func<ushort, ConsentToken?> _consentTokenFactory;
	private readonly Func<Task<int?>>? _batteryProvider;
	private readonly Func<PreparedPackage, CancellationToken, Task<byte[]>>? _imageLoader;
	private readonly TimeProvider _time;
	private readonly Action<string>? _log;

	private readonly Lock _gate = new();
	private readonly Lock _waitLock = new();
	private readonly List<UpgradeTransition> _transitions = [];
	private UpgradeProgress? _current;
	private IProgress<UpgradeProgress>? _progress;

	private CommandPipeline? _pipeline;
	private CommandPipeline? _subscribed;
	private VmuWait? _activeWait;
	private VmuPacket? _asyncError;

	private int _runFlag;
	private RunStatus _status;
	private int _stateInt = (int)UpgradeSessionState.Idle;
	private CancellationTokenSource? _runCts;
	private UpgradeSessionFailure _lastFailure;

	private byte[] _image = [];
	private long _offset;
	private int _chunks;
	private long _resumeBaselineBytes;
	private bool _restartChecked;
	private bool _tunnelOpened;

	/// <summary>Raised (on the run thread) for every state transition — feeds diagnostics and X-13's phase display.</summary>
	public event EventHandler<UpgradeTransition>? Transitioned;

	/// <summary>
	/// Creates the session. <paramref name="pipelineProvider"/> returns the current live pipeline (or null — typically
	/// <c>() => supervisor.Current?.Pipeline</c>) and is re-polled after every expected reboot. <paramref name="consentTokenFactory"/>
	/// must return a single-use <see cref="ConsentToken"/> bound to (profile vendor, command word) on every call — the
	/// gate consumes one token per Dangerous frame. <paramref name="batteryPercentProvider"/> reports the headset battery
	/// percentage (null = unknown = refuse to start). <paramref name="imageLoader"/> reads the staged image bytes for a
	/// prepared package (default: File.ReadAllBytesAsync of <see cref="PreparedPackage.PlainImagePath"/>).
	/// </summary>
	public UpgradeSession(
		UpgradeProtocolProfile profile,
		Func<CommandPipeline?> pipelineProvider,
		Func<ushort, ConsentToken?> consentTokenFactory,
		Func<Task<int?>>? batteryPercentProvider = null,
		Func<PreparedPackage, CancellationToken, Task<byte[]>>? imageLoader = null,
		TimeProvider? time = null,
		Action<string>? log = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(pipelineProvider);
		ArgumentNullException.ThrowIfNull(consentTokenFactory);
		profile.Validate();

		_profile = profile;
		_pipelineProvider = pipelineProvider;
		_consentTokenFactory = consentTokenFactory;
		_batteryProvider = batteryPercentProvider;
		_imageLoader = imageLoader;
		_time = time ?? TimeProvider.System;
		_log = log;
	}

	/// <summary>Current phase (diagnostics; written only by the run thread).</summary>
	public UpgradeSessionState State => (UpgradeSessionState)Volatile.Read(ref _stateInt);

	/// <summary>Latest progress snapshot — persist this and feed it back into <see cref="ResumeAsync"/> after a link loss.</summary>
	public UpgradeProgress? Current
	{
		get
		{
			lock (_gate)
			{
				return _current;
			}
		}
	}

	/// <summary>All recorded transitions, oldest first — one row per phase step and protocol milestone.</summary>
	public IReadOnlyList<UpgradeTransition> Transitions
	{
		get
		{
			lock (_gate)
			{
				return [.. _transitions];
			}
		}
	}

	/// <summary>True when no run is in flight and a call to <see cref="ResumeAsync"/> is legal (cold resume, or a
	/// resumable failure: link lost, reply timeout, reconnect timeout).</summary>
	public bool CanResume
	{
		get
		{
			if (Volatile.Read(ref _runFlag) != 0)
			{
				return false;
			}

			return _status == RunStatus.NeverRun || IsResumableState();
		}
	}

	/// <summary>
	/// Runs the full upgrade sequence (docs/PROTOCOL-upgrade.md §4): Precheck → Connect → AwaitReady → dispatch by
	/// resume point → Transfer → Commit → (Reconnect) → End → Verify → Done. Throws <see cref="UpgradeSessionException"/>
	/// on failure; caller cancellation propagates as <see cref="OperationCanceledException"/> after a best-effort VMU
	/// abort sequence. Single-flight: one run per instance; a second call fails with
	/// <see cref="UpgradeSessionFailure.AlreadyRunning"/> (or <see cref="UpgradeSessionFailure.SessionEnded"/> after a
	/// terminal outcome).
	/// </summary>
	/// <param name="package">The X-10 output — the MD5-verified, staged image.</param>
	/// <param name="expectedVersion">Firmware version expected after the upgrade; null = parse it from the image name (MMM-mmm-ppp, fw-format.md §1 [INFER]).</param>
	/// <param name="progress">Progress sink (one report per chunk and per state change).</param>
	public async Task<UpgradeResult> RunAsync(
		PreparedPackage package,
		FirmwareVersion? expectedVersion = null,
		IProgress<UpgradeProgress>? progress = null,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(package);
		BeginRun(fresh: true);
		return await RunCoreAsync(package, null, expectedVersion, progress, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Continues an upgrade after a resumable failure (link lost / reply timeout / reconnect timeout) or starts a cold
	/// resume on a fresh instance with persisted progress. Re-runs the prechecks, re-opens the tunnel, re-syncs, and
	/// follows the device's resume point — continue, restart from zero, or typed refusal per
	/// <see cref="UpgradeProtocolProfile.ResumePolicy"/> (docs/PROTOCOL-upgrade.md §5).
	/// </summary>
	/// <param name="package">The same package the interrupted run transferred.</param>
	/// <param name="saved">The persisted <see cref="UpgradeProgress"/> of the interrupted run (e.g. the last <see cref="Current"/>).</param>
	/// <param name="expectedVersion">See <see cref="RunAsync"/>.</param>
	/// <param name="progress">Progress sink.</param>
	public async Task<UpgradeResult> ResumeAsync(
		PreparedPackage package,
		UpgradeProgress saved,
		FirmwareVersion? expectedVersion = null,
		IProgress<UpgradeProgress>? progress = null,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(package);
		ArgumentNullException.ThrowIfNull(saved);
		BeginRun(fresh: false);
		return await RunCoreAsync(package, saved, expectedVersion, progress, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Requests cancellation of the in-flight run (idempotent, thread-safe). The run sends the best-effort VMU abort
	/// sequence (UPGRADE_ABORT_REQ, tunnel disconnect), moves to <see cref="UpgradeSessionState.Aborted"/> and rethrows
	/// the cancellation to its awaiter.
	/// </summary>
	public void Abort()
	{
		try
		{
			_runCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The run already ended — nothing to abort.
		}
	}

	private async Task<UpgradeResult> RunCoreAsync(
		PreparedPackage package,
		UpgradeProgress? saved,
		FirmwareVersion? expectedVersion,
		IProgress<UpgradeProgress>? progress,
		CancellationToken ct)
	{
		_progress = progress;
		_lastFailure = default;
		_runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var token = _runCts.Token;
		try
		{
			await PrecheckAsync(package, saved, token).ConfigureAwait(false);
			await OpenTunnelAsync(token).ConfigureAwait(false);
			_tunnelOpened = true;
			var resumePoint = await SyncAndStartAsync(token).ConfigureAwait(false);
			await RunFromResumePointAsync(resumePoint, 0, token).ConfigureAwait(false);
			await CloseTunnelAsync(token).ConfigureAwait(false);

			var expected = expectedVersion ?? TryParseImageVersion(package.InnerImageName);
			if (expected is null)
			{
				_log?.Invoke($"upgrade: no expected version for '{package.InnerImageName}' — the version re-read will not be compared");
			}

			var deviceVersion = await VerifyAsync(expected, token).ConfigureAwait(false);
			MoveTo(UpgradeSessionState.Done, $"upgrade complete — {_offset}/{_image.Length} bytes in {_chunks} chunks");
			return new UpgradeResult(deviceVersion, _offset, _image.Length, _chunks, _time.GetUtcNow());
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested || (_runCts?.IsCancellationRequested ?? false))
		{
			await AbortBestEffortAsync().ConfigureAwait(false);
			MoveTo(UpgradeSessionState.Aborted, "cancelled by the caller — best-effort UPGRADE_ABORT_REQ and tunnel disconnect were sent");
			throw;
		}
		catch (LinkException ex)
		{
			_lastFailure = UpgradeSessionFailure.LinkLost;
			MoveTo(UpgradeSessionState.Reconnect, "link lost mid-session — persist progress and call ResumeAsync once the link is back");
			throw new UpgradeSessionException(UpgradeSessionFailure.LinkLost,
				$"The Bluetooth link to the headset was lost: {ex.Message} — ResumeAsync continues from the device's state.", ex);
		}
		catch (UpgradeSessionException ex)
		{
			_lastFailure = ex.Reason;
			MoveTo(UpgradeSessionState.Failed, $"failed: {ex.Reason}");
			throw;
		}
		catch (SafetyDeniedException ex)
		{
			_lastFailure = UpgradeSessionFailure.GateDenied;
			MoveTo(UpgradeSessionState.Failed, "the safety gate refused an upgrade frame (DevMode/consent)");
			throw new UpgradeSessionException(UpgradeSessionFailure.GateDenied,
				$"The safety gate refused an upgrade frame — DevMode and a matching consent token are required: {ex.Message}", ex);
		}
		catch (GaiaProtocolException ex)
		{
			_lastFailure = UpgradeSessionFailure.DeviceRejected;
			MoveTo(UpgradeSessionState.Failed, "the headset rejected an upgrade command");
			throw new UpgradeSessionException(UpgradeSessionFailure.DeviceRejected,
				$"The headset rejected an upgrade command: {ex.Message}", ex);
		}
		catch (TimeoutException ex)
		{
			_lastFailure = UpgradeSessionFailure.ReplyTimeout;
			MoveTo(UpgradeSessionState.Failed, "a device reply timed out — resumable via ResumeAsync");
			throw new UpgradeSessionException(UpgradeSessionFailure.ReplyTimeout,
				$"The headset did not answer in time: {ex.Message} — ResumeAsync continues from the device's state.", ex);
		}
		catch (OperationCanceledException ex)
		{
			// A timeout surfaced as cancellation although neither the caller nor Abort() cancelled — treat as a reply timeout.
			_lastFailure = UpgradeSessionFailure.ReplyTimeout;
			MoveTo(UpgradeSessionState.Failed, "an upgrade wait ended without a reply");
			throw new UpgradeSessionException(UpgradeSessionFailure.ReplyTimeout,
				$"An upgrade wait ended without a reply: {ex.Message}", ex);
		}
#pragma warning disable CA1031 // terminal wrapper: the session must always fail typed, never leak raw exceptions to X-13
		catch (Exception ex)
#pragma warning restore CA1031
		{
			_lastFailure = UpgradeSessionFailure.ProtocolError;
			MoveTo(UpgradeSessionState.Failed, "unexpected failure");
			throw new UpgradeSessionException(UpgradeSessionFailure.ProtocolError,
				$"The upgrade session failed unexpectedly: {ex.Message}", ex);
		}
		finally
		{
			DetachPipeline();
			_progress = null;
			_status = RunStatus.Finished;
			if (_runCts is not null)
			{
				_runCts.Dispose();
				_runCts = null;
			}

			Interlocked.Exchange(ref _runFlag, 0);
		}
	}

	private void BeginRun(bool fresh)
	{
		if (Interlocked.CompareExchange(ref _runFlag, 1, 0) != 0)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.AlreadyRunning,
				"An upgrade run is already in flight on this session instance.", null);
		}

		try
		{
			if (fresh && _status == RunStatus.Finished)
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.SessionEnded,
					"This upgrade session already ran to a terminal state — create a new UpgradeSession.", null);
			}

			if (!fresh && _status == RunStatus.Finished && !IsResumableState())
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.SessionEnded,
					"This upgrade session already ended without a resumable failure — create a new UpgradeSession.", null);
			}
		}
		catch
		{
			Interlocked.Exchange(ref _runFlag, 0);
			throw;
		}
	}

	private bool IsResumableState() => State switch
	{
		UpgradeSessionState.Reconnect => true,
		UpgradeSessionState.Failed => _lastFailure is UpgradeSessionFailure.LinkLost
				or UpgradeSessionFailure.ReplyTimeout
				or UpgradeSessionFailure.ReconnectFailed,
		_ => false,
	};

	private async Task PrecheckAsync(PreparedPackage package, UpgradeProgress? saved, CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.Precheck, saved is null
				? $"fresh run for {package.InnerImageName} ({package.ImageBytes} bytes)"
				: $"resume: {saved.BytesSent}/{saved.TotalBytes} bytes were previously sent");

		int? battery = null;
		if (_batteryProvider is not null)
		{
			battery = await _batteryProvider().ConfigureAwait(false);
		}

		if (battery is null)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.BatteryUnknown,
				"The headset battery level is unknown — refusing to start the upgrade (charge or reconnect the headphones first).", null);
		}

		if (battery.Value <= _profile.BatteryFloorPercent)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.BatteryLow,
				$"The headset battery is at {battery.Value}%, at or below the {_profile.BatteryFloorPercent}% floor — charge the headphones before upgrading.", null);
		}

		if (!package.Md5Verified)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.PackageInvalid,
				$"The package '{package.InnerImageName}' did not pass the MD5 gate — only verified images may enter the upgrade tunnel.", null);
		}

		byte[] image;
		var loader = _imageLoader ?? DefaultLoadImage;
		try
		{
			image = await loader(package, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.PackageInvalid,
					$"The staged image '{package.PlainImagePath}' could not be read: {ex.Message}", ex);
		}

		if (image.Length == 0)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.PackageInvalid,
					$"The staged image '{package.PlainImagePath}' is empty.", null);
		}

		if (saved is not null && (saved.TotalBytes != image.Length || saved.BytesSent < 0 || saved.BytesSent > image.Length))
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.PackageInvalid,
					$"The saved progress ({saved.BytesSent}/{saved.TotalBytes} bytes) does not match the staged image ({image.Length} bytes) — a different package was prepared.", null);
		}

		_image = image;
		_offset = 0; // the reference resets the host offset and lets the device's first window drive continuation [REF]
		_chunks = saved is { ChunksSent: > 0 } x ? x.ChunksSent : 0;
		_resumeBaselineBytes = saved?.BytesSent ?? 0;
		_restartChecked = false;
		ReportProgress();
	}

	private static async Task<byte[]> DefaultLoadImage(PreparedPackage package, CancellationToken ct) =>
			await File.ReadAllBytesAsync(package.PlainImagePath, ct).ConfigureAwait(false);

	private async Task OpenTunnelAsync(CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.Connect, "acquiring the GAIA session and opening the VM-upgrade tunnel");
		var pipeline = _pipelineProvider() ?? throw new UpgradeSessionException(UpgradeSessionFailure.NoConnection,
			"No live headset session — the pipeline provider returned null. Connect the headphones first.", null);
		AttachPipeline(pipeline);
		await RegisterTunnelNotificationAsync(pipeline, ct).ConfigureAwait(false);

		var connectWord = RequireWord(_profile.TunnelConnectWord, "tunnel connect word");
		await SendFrameAsync(_profile.UpgradeVendor, connectWord, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
		_log?.Invoke($"upgrade: tunnel open on {_profile.UpgradeVendor}:{connectWord:X4}");
	}

	// The v2 sample registers for the VMU_PACKET event before connecting and re-registers after every reconnect [REF].
	private async Task RegisterTunnelNotificationAsync(CommandPipeline pipeline, CancellationToken ct)
	{
		if (_profile.TunnelNotificationFeature is not { } feature)
		{
			return;
		}

		try
		{
			await pipeline.ExecuteAsync(
					_profile.UpgradeVendor,
					Registration.RegisterCommand,
					new byte[] { feature },
					new CommandOptions { Timeout = _profile.CommandTimeout },
					ct)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is GaiaProtocolException or TimeoutException)
		{
			_log?.Invoke($"upgrade: tunnel notification registration {_profile.UpgradeVendor}:{feature:X2} failed: {ex.Message} (continuing — replies may ride the control ACK)");
		}
	}

	private async Task<byte> SyncAndStartAsync(CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.AwaitReady, "VMU synchronization and device readiness");
		var opcodes = _profile.Opcodes;

		// UPGRADE_SYNC_REQ carries the last bytes of MD5(image) as the upgrade identifier [REF].
		var syncCfm = await SendVmuExpectAsync(opcodes.SyncReq, ComputeSyncId(), opcodes.SyncCfm, ct).ConfigureAwait(false);
		var resumePoint = (byte)ResumePointDataTransfer;
		if (syncCfm.Data.Length >= 6)
		{
			resumePoint = syncCfm.Data.Span[0];
			var identifier = BinaryPrimitives.ReadUInt32BigEndian(syncCfm.Data.Span[1..]);
			var protocolVersion = syncCfm.Data.Span[5];
			_log?.Invoke($"upgrade: sync confirmed — resume point {resumePoint}, id 0x{identifier:X8}, protocol 0x{protocolVersion:X2}");
		}
		else
		{
			_log?.Invoke($"upgrade: short UPGRADE_SYNC_CFM ({syncCfm.Data.Length} bytes) — resume point 0 (reference default)");
		}

		for (var attempt = 1; ; attempt++)
		{
			var startCfm = await SendVmuExpectAsync(opcodes.StartReq, ReadOnlyMemory<byte>.Empty, opcodes.StartCfm, ct).ConfigureAwait(false);
			if (startCfm.Data.Length < 3)
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.ProtocolError,
					$"UPGRADE_START_CFM is malformed ({startCfm.Data.Length} bytes, expected 3).", null);
			}

			var status = startCfm.Data.Span[0];
			var deviceBattery = BinaryPrimitives.ReadUInt16BigEndian(startCfm.Data.Span[1..]);
			if (status == StartStatusSuccess)
			{
				_log?.Invoke($"upgrade: device ready — battery {deviceBattery}% reported in UPGRADE_START_CFM");
				return resumePoint;
			}

			if (status == StartStatusNotReady && attempt < _profile.StartNotReadyRetries)
			{
				_log?.Invoke($"upgrade: device not ready (0x09), attempt {attempt}/{_profile.StartNotReadyRetries} — retrying");
				await Task.Delay(_profile.StartNotReadyRetryDelay, _time, ct).ConfigureAwait(false);
				continue;
			}

			if (status == StartStatusNotReady)
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.DeviceNotReady,
						$"The headset stayed not-ready (0x09) through {_profile.StartNotReadyRetries} start attempts.", null);
			}

			throw new UpgradeSessionException(UpgradeSessionFailure.DeviceRejected,
					$"The headset refused the upgrade start with status 0x{status:X2}.", null);
		}
	}

	// Dispatch on the device's resume point — one code path for a fresh start and for every (re)connect [REF sample switch].
	private async Task RunFromResumePointAsync(byte resumePoint, int depth, CancellationToken ct)
	{
		if (depth > MaxDispatchDepth)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.ProtocolError,
					$"The device's resume points did not converge after {MaxDispatchDepth} dispatches.", null);
		}

		var opcodes = _profile.Opcodes;
		switch (resumePoint)
		{
			case ResumePointDataTransfer:
				MoveTo(UpgradeSessionState.Start, "resume point 0 — data transfer");
				await SendVmuAsync(opcodes.StartDataReq, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
				await TransferAsync(ct).ConfigureAwait(false);
				await ConfirmTransferCompleteAsync(depth, ct).ConfigureAwait(false);
				break;

			case ResumePointValidation:
				MoveTo(UpgradeSessionState.Commit, "resume point 1 — validation");
				await ValidationLoopAsync(ct).ConfigureAwait(false);
				await ConfirmTransferCompleteAsync(depth, ct).ConfigureAwait(false);
				break;

			case ResumePointTransferComplete:
				MoveTo(UpgradeSessionState.Commit, "resume point 2 — transfer-complete confirmation");
				await ConfirmTransferCompleteAsync(depth, ct).ConfigureAwait(false);
				break;

			case ResumePointInProgress:
				MoveTo(UpgradeSessionState.Commit, "resume point 3 — post-reboot in-progress confirmation");
				await SendVmuAsync(opcodes.InProgressRes, new[] { _profile.ContinueAction }, ct).ConfigureAwait(false);
				await CommitConfirmAsync(ct).ConfigureAwait(false);
				break;

			case ResumePointCommit:
				MoveTo(UpgradeSessionState.Commit, "resume point 4 — commit confirmation");
				await CommitConfirmAsync(ct).ConfigureAwait(false);
				break;

			default:
				throw new UpgradeSessionException(UpgradeSessionFailure.ProtocolError,
						$"The device reported an unknown resume point 0x{resumePoint:X2}.", null);
		}
	}

	private async Task TransferAsync(CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.Transfer, "device-pulled chunked transfer (UPGRADE_DATA_BYTES_REQ windows)");
		var opcodes = _profile.Opcodes;
		while (true)
		{
			var push = await AwaitVmuAsync(
					[opcodes.DataBytesReq, opcodes.ValidationDoneCfm, opcodes.TransferCompleteInd],
					_profile.VmuReplyTimeout,
					ct)
				.ConfigureAwait(false);

			if (push.OpCode == opcodes.TransferCompleteInd)
			{
				return;
			}

			if (push.OpCode == opcodes.ValidationDoneCfm)
			{
				// The device is still validating: wait the reported time, then re-ask [REF].
				var waitMs = push.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(push.Data.Span) : 0;
				var delay = TimeSpan.FromMilliseconds(Math.Min(waitMs, _profile.MaxValidationWait.TotalMilliseconds));
				_log?.Invoke($"upgrade: validation in progress — waiting {delay.TotalMilliseconds:0} ms before re-asking");
				await Task.Delay(delay, _time, ct).ConfigureAwait(false);
				await SendVmuAsync(opcodes.ValidationDoneReq, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
				continue;
			}

			// UPGRADE_DATA_BYTES_REQ: [number of bytes u32BE][file start offset u32BE] [REF].
			if (push.Data.Length < 8)
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.ProtocolError,
						$"UPGRADE_DATA_BYTES_REQ is malformed ({push.Data.Length} bytes, expected 8).", null);
			}

			var requested = BinaryPrimitives.ReadUInt32BigEndian(push.Data.Span);
			var fileOffset = BinaryPrimitives.ReadUInt32BigEndian(push.Data.Span[4..]);

			// Reference arithmetic: a positive offset that still fits is added to the host position [REF]; if R-06
			// shows absolute semantics the fix is one line + an ERRATA note [CAP-R06].
			if (fileOffset > 0 && fileOffset + _offset < _image.Length)
			{
				_offset += fileOffset;
			}

			if (!_restartChecked)
			{
				_restartChecked = true;
				if (_offset == 0 && _resumeBaselineBytes > 0 && _profile.ResumePolicy == UpgradeResumePolicy.FailOnRestart)
				{
					throw new UpgradeSessionException(UpgradeSessionFailure.RestartRefused,
							$"The device restarted the transfer from zero although {_resumeBaselineBytes} bytes were already sent — the profile policy refuses the restart.", null);
				}
			}

			var remaining = _image.Length - _offset;
			long window = requested;
			if (window > remaining)
			{
				window = remaining; // reference clamp to the file end [REF]
			}

			if (window < 0)
			{
				window = 0;
			}

			while (window > 0)
			{
				ct.ThrowIfCancellationRequested();
				var chunk = (int)Math.Min(Math.Min(window, _profile.MaxChunkFileBytes), remaining);
				var last = _offset + chunk >= _image.Length;
				await SendDataPacketAsync(last, _image.AsMemory((int)_offset, chunk), ct).ConfigureAwait(false);
				_offset += chunk;
				window -= chunk;
				remaining -= chunk;
				ReportProgress();
				if (_profile.InterChunkDelay > TimeSpan.Zero)
				{
					await Task.Delay(_profile.InterChunkDelay, _time, ct).ConfigureAwait(false);
				}
			}

			// Requested window exhausted — ask whether validation has finished [REF].
			await SendVmuAsync(opcodes.ValidationDoneReq, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
		}
	}

	// Validation tail of the transfer phase: the device answers IS_VALIDATION_DONE_REQ with a waiting time until it
	// is done, then raises TRANSFER_COMPLETE_IND [REF].
	private async Task ValidationLoopAsync(CancellationToken ct)
	{
		var opcodes = _profile.Opcodes;
		await SendVmuAsync(opcodes.ValidationDoneReq, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
		while (true)
		{
			var push = await AwaitVmuAsync(
					[opcodes.ValidationDoneCfm, opcodes.TransferCompleteInd],
					_profile.VmuReplyTimeout,
					ct)
				.ConfigureAwait(false);

			if (push.OpCode == opcodes.TransferCompleteInd)
			{
				return;
			}

			var waitMs = push.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(push.Data.Span) : 0;
			var delay = TimeSpan.FromMilliseconds(Math.Min(waitMs, _profile.MaxValidationWait.TotalMilliseconds));
			_log?.Invoke($"upgrade: validation in progress — waiting {delay.TotalMilliseconds:0} ms before re-asking");
			await Task.Delay(delay, _time, ct).ConfigureAwait(false);
			await SendVmuAsync(opcodes.ValidationDoneReq, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
		}
	}

	private async Task SendDataPacketAsync(bool last, ReadOnlyMemory<byte> fileBytes, CancellationToken ct)
	{
		// UPGRADE_DATA payload: [last-packet flag u8][file bytes] [REF].
		var payload = new byte[1 + fileBytes.Length];
		payload[0] = last ? IsLastPacket : (byte)0;
		fileBytes.Span.CopyTo(payload.AsSpan(1));
		await SendVmuAsync(_profile.Opcodes.Data, payload, ct).ConfigureAwait(false);
		_chunks++;
	}

	// TRANSFER_COMPLETE_RES(continue) → the device reboots to validate/apply (the link drop is EXPECTED) →
	// reconnect-query → dispatch on the new resume point [REF].
	private async Task ConfirmTransferCompleteAsync(int depth, CancellationToken ct)
	{
		await SendVmuAsync(_profile.Opcodes.TransferCompleteRes, new[] { _profile.ContinueAction }, ct).ConfigureAwait(false);
		var resumePoint = await AwaitDeviceAfterRebootAsync(ct).ConfigureAwait(false);
		await RunFromResumePointAsync(resumePoint, depth + 1, ct).ConfigureAwait(false);
	}

	private async Task CommitConfirmAsync(CancellationToken ct)
	{
		var opcodes = _profile.Opcodes;
		var push = await AwaitVmuAsync([opcodes.CommitReq, opcodes.CompleteInd], _profile.VmuReplyTimeout, ct).ConfigureAwait(false);
		if (push.OpCode == opcodes.CompleteInd)
		{
			_log?.Invoke("upgrade: UPGRADE_COMPLETE_IND without a COMMIT_REQ — the device had already committed");
		}
		else
		{
			// The device asked for permission to commit; the session is consent-gated as a whole, so it always continues [REF].
			await SendVmuAsync(opcodes.CommitCfm, new[] { _profile.ContinueAction }, ct).ConfigureAwait(false);
		}

		var complete = await TryAwaitVmuAsync(opcodes.CompleteInd, _profile.CommitCompleteTimeout, ct).ConfigureAwait(false);
		if (complete is null)
		{
			// Flash writing can outrun the wait; the version re-read remains the success gate [INFER].
			_log?.Invoke("upgrade: UPGRADE_COMPLETE_IND not observed — falling through to the tunnel disconnect and the version re-read");
		}
	}

	private async Task<byte> AwaitDeviceAfterRebootAsync(CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.Reconnect, "device reboot expected — waiting for the link and the tunnel to return");
		var deadline = _time.GetUtcNow() + _profile.ReconnectTimeout;
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				var pipeline = _pipelineProvider();
				if (pipeline is not null)
				{
					AttachPipeline(pipeline);
					await RegisterTunnelNotificationAsync(pipeline, ct).ConfigureAwait(false);
					var connectWord = RequireWord(_profile.TunnelConnectWord, "tunnel connect word");
					await SendFrameAsync(_profile.UpgradeVendor, connectWord, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
					return await SyncAndStartAsync(ct).ConfigureAwait(false);
				}
			}
			catch (Exception ex) when (ex is LinkException or TimeoutException or IOException or ObjectDisposedException)
			{
				_log?.Invoke($"upgrade: headset not back yet ({ex.GetType().Name}: {ex.Message})");
			}

			if (_time.GetUtcNow() >= deadline)
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.ReconnectFailed,
						$"The headset did not come back within {_profile.ReconnectTimeout.TotalSeconds:0} s after the commit — ResumeAsync can retry.", null);
			}

			await Task.Delay(_profile.ReconnectPollInterval, _time, ct).ConfigureAwait(false);
		}
	}

	private async Task CloseTunnelAsync(CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.End, "closing the VM-upgrade tunnel");
		var word = RequireWord(_profile.TunnelDisconnectWord, "tunnel disconnect word");
		try
		{
			await SendFrameAsync(_profile.UpgradeVendor, word, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is LinkException or TimeoutException or GaiaProtocolException)
		{
			// The upgrade already completed — a failed goodbye must not fail the session.
			_log?.Invoke($"upgrade: tunnel disconnect after completion failed (ignored): {ex.Message}");
		}
	}

	private async Task<FirmwareVersion?> VerifyAsync(FirmwareVersion? expectedVersion, CancellationToken ct)
	{
		MoveTo(UpgradeSessionState.Verify, "firmware version re-read");
		if (_profile.VersionReadWord is not { } word)
		{
			_log?.Invoke("upgrade: the profile carries no version read word — verification skipped");
			return null;
		}

		if (_pipeline is not { } pipeline)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.NoConnection,
					"The session pipeline vanished before the version re-read.", null);
		}

		var reply = await pipeline.ExecuteAsync(
				_profile.VersionReadVendor,
				word,
				ReadOnlyMemory<byte>.Empty,
				new CommandOptions { Timeout = _profile.CommandTimeout },
				ct)
			.ConfigureAwait(false);
		if (reply.Payload.Length != 6)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.ProtocolError,
					$"The version re-read returned {reply.Payload.Length} bytes (expected 6 = 3 × u16BE).", null);
		}

		var version = new FirmwareVersion(
			BinaryPrimitives.ReadUInt16BigEndian(reply.Payload.Span),
			BinaryPrimitives.ReadUInt16BigEndian(reply.Payload.Span[2..]),
			BinaryPrimitives.ReadUInt16BigEndian(reply.Payload.Span[4..]));
		_log?.Invoke($"upgrade: the device reports firmware {version}");
		if (expectedVersion is not null && version != expectedVersion)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.VersionMismatch,
					$"The device reports firmware {version} after the upgrade — expected {expectedVersion}.", null);
		}

		return version;
	}

	private async Task AbortBestEffortAsync()
	{
		if (!_tunnelOpened || _pipeline is null)
		{
			return;
		}

		var opcodes = _profile.Opcodes;
#pragma warning disable CA1031 // best-effort cleanup after cancellation: nothing here may mask the cancellation
		try
		{
			using var abortCts = new CancellationTokenSource(TimeSpan.FromSeconds(3), _time);
			await SendVmuAsync(opcodes.AbortReq, ReadOnlyMemory<byte>.Empty, abortCts.Token).ConfigureAwait(false);
			await TryAwaitVmuAsync(opcodes.AbortCfm, TimeSpan.FromSeconds(2), abortCts.Token).ConfigureAwait(false);
			var word = _profile.TunnelDisconnectWord;
			if (word is { } disconnect)
			{
				await SendFrameAsync(_profile.UpgradeVendor, disconnect, ReadOnlyMemory<byte>.Empty, abortCts.Token).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			_log?.Invoke($"upgrade: best-effort abort sequence failed (ignored): {ex.Message}");
		}
#pragma warning restore CA1031
	}

	/// <summary>Sends one VMU packet without waiting for a VMU reply (the GAIA ACK of the carrier word is awaited inside).</summary>
	private async Task SendVmuAsync(byte opcode, ReadOnlyMemory<byte> data, CancellationToken ct)
	{
		await ConsumeAsyncErrorAsync(ct).ConfigureAwait(false);
		var carrier = _profile.DataPacketsOnDataWord && opcode == _profile.Opcodes.Data
				? RequireWord(_profile.TunnelDataWord, "tunnel data word")
				: RequireWord(_profile.TunnelControlWord, "tunnel control word");
		await SendFrameAsync(_profile.UpgradeVendor, carrier, BuildVmuPacket(opcode, data), ct).ConfigureAwait(false);
	}

	/// <summary>Sends one VMU request and awaits its VMU reply (push channel, or the control ACK payload when the
	/// profile says so) — docs/PROTOCOL-upgrade.md §7.</summary>
	private async Task<VmuPacket> SendVmuExpectAsync(byte opcode, ReadOnlyMemory<byte> data, byte expectedReply, CancellationToken ct)
	{
		await ConsumeAsyncErrorAsync(ct).ConfigureAwait(false);
		var carrier = RequireWord(_profile.TunnelControlWord, "tunnel control word");
		var packet = BuildVmuPacket(opcode, data);
		var wait = ArmWait(expectedReply);
		try
		{
			var ack = await SendFrameAsync(_profile.UpgradeVendor, carrier, packet, ct).ConfigureAwait(false);
			if (_profile.RepliesInControlAck && TryParseVmuPacket(ack.Payload) is { } fromAck && fromAck.OpCode == expectedReply)
			{
				return fromAck;
			}

			var reply = await wait.Task.WaitAsync(_profile.VmuReplyTimeout, _time, ct).ConfigureAwait(false);
			return await InterpretAsync(reply, ct).ConfigureAwait(false);
		}
		finally
		{
			DisarmWait(wait);
		}
	}

	/// <summary>Awaits an unsolicited device VMU packet with one of the expected opcodes (async device errors are
	/// always accepted and interpreted).</summary>
	private async Task<VmuPacket> AwaitVmuAsync(byte[] expected, TimeSpan timeout, CancellationToken ct)
	{
		await ConsumeAsyncErrorAsync(ct).ConfigureAwait(false);
		var wait = ArmWait(expected);
		VmuPacket packet;
		try
		{
			packet = await wait.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
		}
		finally
		{
			DisarmWait(wait);
		}

		return await InterpretAsync(packet, ct).ConfigureAwait(false);
	}

	/// <summary>Awaits one opcode and returns null on timeout instead of failing (used for UPGRADE_COMPLETE_IND).</summary>
	private async Task<VmuPacket?> TryAwaitVmuAsync(byte expected, TimeSpan timeout, CancellationToken ct)
	{
		await ConsumeAsyncErrorAsync(ct).ConfigureAwait(false);
		var wait = ArmWait(expected);
		try
		{
			try
			{
				var packet = await wait.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
				return await InterpretAsync(packet, ct).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				return null;
			}
		}
		finally
		{
			DisarmWait(wait);
		}
	}

	// Turns a received packet into a result: UPGRADE_ERROR_WARN_IND is echoed then failed, UPGRADE_ABORT_CFM is a
	// device abort, everything else is returned to the caller [REF].
	private async Task<VmuPacket> InterpretAsync(VmuPacket packet, CancellationToken ct)
	{
		var opcodes = _profile.Opcodes;
		if (packet.OpCode != opcodes.ErrorWarnInd)
		{
			if (packet.OpCode == opcodes.AbortCfm)
			{
				throw new UpgradeSessionException(UpgradeSessionFailure.DeviceAborted,
						"The headset aborted the upgrade (UPGRADE_ABORT_CFM).", null);
			}

			return packet;
		}

		var code = packet.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(packet.Data.Span) : (ushort)0;
		try
		{
			// The host must echo the error/warning back before anything else [REF].
			await SendVmuAsync(opcodes.ErrorWarnRes, packet.Data.ToArray(), ct).ConfigureAwait(false);
		}
#pragma warning disable CA1031 // the echo is protocol-mandated but must never mask the original error
		catch (Exception ex)
#pragma warning restore CA1031
		{
			_log?.Invoke($"upgrade: UPGRADE_ERROR_WARN_RES echo failed (ignored): {ex.Message}");
		}

		throw code switch
		{
			WarnSyncIdIsDifferent => new UpgradeSessionException(UpgradeSessionFailure.DeviceRejected,
					"The headset reports a different upgrade in progress (WARN_SYNC_ID_IS_DIFFERENT) — the staged image does not match the device state.", null),
			ErrorBatteryLow => new UpgradeSessionException(UpgradeSessionFailure.BatteryLow,
					"The headset reports its battery is too low to continue the upgrade (ERROR_BATTERY_LOW).", null),
			_ => new UpgradeSessionException(UpgradeSessionFailure.DeviceRejected,
					$"The upgrade failed with VMU return code 0x{code:X4}.", null),
		};
	}

	// An error/abort that arrived while nobody was waiting: fail the next step with it.
	private async Task ConsumeAsyncErrorAsync(CancellationToken ct)
	{
		var error = Interlocked.Exchange(ref _asyncError, null);
		if (error is not null)
		{
			await InterpretAsync(error, ct).ConfigureAwait(false);
		}
	}

	private async Task<GaiaFrame> SendFrameAsync(GaiaVendor vendor, ushort word, ReadOnlyMemory<byte> payload, CancellationToken ct)
	{
		var pipeline = _pipeline ?? throw new UpgradeSessionException(UpgradeSessionFailure.NoConnection,
			"The session has no pipeline — Connect did not run or the link ended.", null);
		var options = OptionsForWord(vendor, word);
		return await pipeline.ExecuteAsync(vendor, word, payload, options, ct).ConfigureAwait(false);
	}

	// Dangerous words need DevMode + a single-use consent token per send (the gate consumes tokens per frame, so the
	// factory is invoked before EVERY Dangerous frame — the first one included).
	private CommandOptions OptionsForWord(GaiaVendor vendor, ushort word)
	{
		if (GaiaCatalog.SafetyOf(vendor, word) != SafetyClass.Dangerous)
		{
			return new CommandOptions { Timeout = _profile.CommandTimeout };
		}

		var token = _consentTokenFactory(word);
		if (token is null)
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.ConsentRequired,
					$"A consent token for {vendor}:{GaiaWord.AsCommand(word):X4} is required before this Dangerous frame can be sent.", null);
		}

		if (token.Vendor != vendor || GaiaWord.AsCommand(token.Command) != GaiaWord.AsCommand(word))
		{
			throw new UpgradeSessionException(UpgradeSessionFailure.ConsentRequired,
					$"The consent token for {vendor}:{GaiaWord.AsCommand(word):X4} is bound to {token.Vendor}:{GaiaWord.AsCommand(token.Command):X4} — tokens must match the command word.", null);
		}

		return new CommandOptions { Timeout = _profile.CommandTimeout, Consent = token };
	}

	private static ushort RequireWord(ushort? word, string name) =>
		word ?? throw new UpgradeSessionException(UpgradeSessionFailure.UnobservedCommand,
				$"The upgrade profile carries no {name} — refusing to send an unobserved command (R-06 capture pending).", null);

	// VMU packet framing: [opcode u8][length u16BE][data] [REF VMUPacket]. The declared length is advisory — the
	// reference reads whatever follows the header.
	private static byte[] BuildVmuPacket(byte opcode, ReadOnlyMemory<byte> data)
	{
		var packet = new byte[3 + data.Length];
		packet[0] = opcode;
		BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), (ushort)data.Length);
		data.Span.CopyTo(packet.AsSpan(3));
		return packet;
	}

	private static VmuPacket? TryParseVmuPacket(ReadOnlyMemory<byte> payload)
	{
		if (payload.Length < 3)
		{
			return null;
		}

		return new VmuPacket(payload.Span[0], payload[3..]);
	}

	// The upgrade identifier: the LAST SyncIdByteCount bytes of MD5(image) [REF UpgradeManager.sendSyncReq].
	private byte[] ComputeSyncId()
	{
		var digest = MD5.HashData(_image);
		var count = Math.Min(_profile.SyncIdByteCount, digest.Length);
		var id = new byte[count];
		digest.AsSpan(digest.Length - count).CopyTo(id);
		return id;
	}

	// The version triple embedded in the image name (fw-format.md §1: 6.21.5 → "006-021-005") [INFER].
	private static FirmwareVersion? TryParseImageVersion(string innerImageName)
	{
		var parts = innerImageName.Split('-');
		for (var i = 0; i + 2 < parts.Length; i++)
		{
			if (parts[i].Length == 3 && parts[i + 1].Length == 3 && parts[i + 2].Length == 3
				&& int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
				&& int.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
				&& int.TryParse(parts[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
			{
				return new FirmwareVersion(major, minor, patch);
			}
		}

		return null;
	}

	private void OnUnsolicited(object? sender, GaiaFrame frame)
	{
		try
		{
			if (_profile.TunnelNotificationWord is not { } pushWord
				|| frame.Vendor != _profile.UpgradeVendor
				|| frame.Command != pushWord)
			{
				return;
			}

			var payload = frame.Payload;
			if (_profile.NotificationCarriesEventByte && payload.Length > 0)
			{
				payload = payload[1..];
			}

			if (TryParseVmuPacket(payload) is not { } packet)
			{
				_log?.Invoke($"upgrade: unparseable tunnel push dropped: {GaiaFormatter.Hex(frame.Payload.Span)}");
				return;
			}

			var delivered = false;
			lock (_waitLock)
			{
				if (_activeWait is { } wait && wait.Accepts(packet.OpCode))
				{
					delivered = wait.Deliver(packet);
				}
			}

			if (!delivered)
			{
				StoreUnexpectedPush(packet);
			}
		}
#pragma warning disable CA1031 // the pipeline pump thread must never observe exceptions from push handling
		catch (Exception ex)
#pragma warning restore CA1031
		{
			_log?.Invoke($"upgrade: tunnel push handling failed: {ex.Message}");
		}
	}

	// Device errors that arrive while nobody waits are stored and fail the next step (echo + typed failure).
	private void StoreUnexpectedPush(VmuPacket packet)
	{
		var opcodes = _profile.Opcodes;
		if (packet.OpCode != opcodes.ErrorWarnInd && packet.OpCode != opcodes.AbortCfm)
		{
			_log?.Invoke($"upgrade: unexpected VMU opcode 0x{packet.OpCode:X2} while no reply was pending");
			return;
		}

		if (Interlocked.CompareExchange(ref _asyncError, packet, null) is null)
		{
			_log?.Invoke($"upgrade: async device report stored (opcode 0x{packet.OpCode:X2}) — it will fail the session at the next step");
		}
	}

	private VmuWait ArmWait(params byte[] expected)
	{
		var wait = new VmuWait([.. expected, _profile.Opcodes.ErrorWarnInd, _profile.Opcodes.AbortCfm]);
		lock (_waitLock)
		{
			_activeWait = wait;
		}

		return wait;
	}

	private void DisarmWait(VmuWait? wait)
	{
		if (wait is null)
		{
			return;
		}

		lock (_waitLock)
		{
			if (ReferenceEquals(_activeWait, wait))
			{
				_activeWait = null;
			}
		}

		wait.Abandon();
	}

	private void AttachPipeline(CommandPipeline pipeline)
	{
		if (ReferenceEquals(_subscribed, pipeline))
		{
			_pipeline = pipeline;
			return;
		}

		DetachPipeline();
		_pipeline = pipeline;
		_subscribed = pipeline;
		pipeline.Unsolicited += OnUnsolicited;
	}

	private void DetachPipeline()
	{
		var subscribed = _subscribed;
		if (subscribed is not null)
		{
			subscribed.Unsolicited -= OnUnsolicited;
		}

		_subscribed = null;
		_pipeline = null;
	}

	private void MoveTo(UpgradeSessionState next, string note)
	{
		var from = (UpgradeSessionState)Volatile.Read(ref _stateInt);
		if (from == next)
		{
			return;
		}

		Volatile.Write(ref _stateInt, (int)next);
		var transition = new UpgradeTransition(from, next, note, _time.GetUtcNow());
		lock (_gate)
		{
			_transitions.Add(transition);
		}

		Transitioned?.Invoke(this, transition);
		ReportProgress();
	}

	private void ReportProgress()
	{
		var progress = new UpgradeProgress(
			(UpgradeSessionState)Volatile.Read(ref _stateInt),
			_offset,
			_image.Length,
			_chunks,
			_time.GetUtcNow());
		lock (_gate)
		{
			_current = progress;
		}

		_progress?.Report(progress);
	}

	// One parsed VMU packet (docs/PROTOCOL-upgrade.md §2).
	private sealed record VmuPacket(byte OpCode, ReadOnlyMemory<byte> Data);

	// A one-shot wait for a VMU packet with one of the expected opcodes (plus the always-accepted error/abort pair).
	private sealed class VmuWait(byte[] expected)
	{
		private readonly TaskCompletionSource<VmuPacket> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<VmuPacket> Task => _completion.Task;

		public bool Accepts(byte opcode) => Array.IndexOf(expected, opcode) >= 0;

		public bool Deliver(VmuPacket packet) => _completion.TrySetResult(packet);

		public void Abandon() => _completion.TrySetCanceled();
	}

	private enum RunStatus
	{
		NeverRun,
		Finished,
	}
}
