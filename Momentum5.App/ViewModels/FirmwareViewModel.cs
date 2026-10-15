using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Pages.Advanced;
using Momentum5.App.Shell;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.App.ViewModels;

/// <summary>
/// Run-scoped bridge between the typed-phrase consent dialog (X-02) and the per-frame consent tokens the X-11
/// upgrade session demands. <see cref="Grant"/> opens a window in which <see cref="Issue"/> may mint single-use
/// tokens for the Dangerous tunnel words: the token issued by the dialog is consumed by the first matching frame,
/// and every further Dangerous frame gets a fresh <see cref="ConsentToken"/> issued (and audited) against the
/// exact consequence text the user confirmed, so the audit hash stays bound to what was shown. Without an active
/// grant <see cref="Issue"/> returns null and the session refuses with
/// <see cref="UpgradeSessionFailure.ConsentRequired"/> before any byte leaves the app. Registered as a singleton
/// so the composition-rooted <see cref="UpgradeSession"/> factory shares it with the view model.
///
/// Note: X-11 demands one token per Dangerous frame, and every data chunk rides the tunnel word, so a full
/// transfer mints many audited tokens — one per frame, all carrying the same consequence hash.
/// </summary>
public sealed class FirmwareConsentGate
{
	private readonly Lock _gate = new();
	private readonly TimeProvider _time;
	private ConsentToken? _opening;
	private string? _consequence;
	private GaiaVendor _vendor;
	private DateTimeOffset _deadline;

	/// <summary>Creates the gate; <paramref name="time"/> clocks the grant window (tests inject a fake).</summary>
	public FirmwareConsentGate(TimeProvider? time = null)
	{
		_time = time ?? TimeProvider.System;
	}

	/// <summary>
	/// Opens the consent window. <paramref name="openingToken"/> is the token the typed-phrase dialog issued
	/// (consumed by the first matching Dangerous frame), <paramref name="consequenceText"/> is the text the user
	/// confirmed — later tokens are issued against the same text — and <paramref name="lifetime"/> bounds the whole
	/// run, because a firmware transfer can outlast the dialog token's 5-minute default lifetime.
	/// </summary>
	public void Grant(ConsentToken openingToken, string consequenceText, TimeSpan lifetime)
	{
		ArgumentNullException.ThrowIfNull(openingToken);
		ArgumentException.ThrowIfNullOrWhiteSpace(consequenceText);
		lock (_gate)
		{
			_opening = openingToken;
			_vendor = openingToken.Vendor;
			_consequence = consequenceText;
			_deadline = _time.GetUtcNow() + lifetime;
		}
	}

	/// <summary>Closes the consent window — called when the run ends, whatever the outcome.</summary>
	public void Revoke()
	{
		lock (_gate)
		{
			_opening = null;
			_consequence = null;
			_vendor = default;
			_deadline = default;
		}
	}

	/// <summary>
	/// Token factory for <see cref="UpgradeSession"/>: returns the opening token for its own word (single use),
	/// a fresh single-use token for any other word of the granted vendor while the window is open, or null when
	/// there is no active grant — null makes the session refuse with ConsentRequired before sending anything.
	/// </summary>
	public ConsentToken? Issue(GaiaVendor vendor, ushort command, ConsentService consent)
	{
		ArgumentNullException.ThrowIfNull(consent);
		lock (_gate)
		{
			if (_consequence is null || vendor != _vendor || _time.GetUtcNow() > _deadline)
			{
				return null;
			}

			if (_opening is { } opening && GaiaWord.AsCommand(command) == opening.Command)
			{
				_opening = null;
				return opening;
			}

			var remaining = _deadline - _time.GetUtcNow();
			return consent.Issue(vendor, command, _consequence, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));
		}
	}
}

/// <summary>
/// X-13 firmware-update state: the staged flow Check (X-09 <see cref="UpdateCheck"/>) → Prepare
/// (X-10 <see cref="PackagePrep"/>, typed failures as state, never crashes) → Review (current vs latest via
/// <see cref="FirmwareVersion"/>, changelog link, package info) → battery gate (the real
/// <see cref="Keys.BatteryPercent"/> snapshot key; unknown or at/below the profile floor refuses) → Execute
/// (X-11 <see cref="UpgradeSession"/> resolved fresh per run, progress through
/// <see cref="UpgradeProgress"/> reports, link-loss/resume state, abort) → result with recovery guidance. The
/// typed-phrase consent dialog itself runs in the page code-behind (X-02 <see cref="ConsentDialogService"/>);
/// the issued token arrives here through <see cref="ExecuteAsync"/> and opens the
/// <see cref="FirmwareConsentGate"/> window. Session tracking follows the supervisor
/// (FactoryResetViewModel precedent) and the snapshot through <see cref="ShellViewModelBase"/>; no WinRT types.
/// </summary>
public sealed partial class FirmwareViewModel : ShellViewModelBase
{
	/// <summary>The phrase the consent dialog requires (the X-13 bucket's typed phrase).</summary>
	public const string ConfirmPhrase = "I understand this can brick my headphones";

	/// <summary>
	/// The consequence text shown in the consent dialog and hashed into every audit entry of the run. The exact
	/// text the user confirmed and the text the tokens are issued against cannot drift apart.
	/// </summary>
	public const string ConsentConsequence =
		"The firmware on the MOMENTUM 5 Wireless is overwritten with the downloaded image. If the transfer is interrupted or the image is rejected mid-way, the headphones can become unusable (\u201Cbricked\u201D) and may need a 20-second button reset or a re-flash through the official Sennheiser Smart Control Plus app. Keep the battery above the 30 % floor, keep the headphones powered and in range, and do not close this app, power off the PC, or turn the headphones off during the update.";

	/// <summary>
	/// The raw Qualcomm tunnel-open word (Q:0C00, Dangerous class) the typed phrase is bound to — the first
	/// Dangerous frame an X-11 run sends. The profile default matches; a profile without a tunnel word is gated
	/// off by <see cref="ProfileHasTunnel"/>.
	/// </summary>
	public const ushort TunnelOpenCommand = 0x0C00;

	/// <summary>
	/// How long the consent window stays open for one run — transfers can take tens of minutes, far beyond the
	/// 5-minute default token lifetime.
	/// </summary>
	public static readonly TimeSpan RunConsentLifetime = TimeSpan.FromHours(2);

	private readonly UpdateCheck _check;
	private readonly PackagePrep _prep;
	private readonly UpgradeProtocolProfile _profile;
	private readonly ConnectionSupervisor _supervisor;
	private readonly FirmwareConsentGate _gate;
	private readonly AppLog _log;
	private readonly Func<UpgradeSession> _sessionFactory;

	private PreparedPackage? _package;
	private FirmwareVersion? _latest;
	private Uri? _packageUrl;
	private UpgradeProgress? _savedProgress;
	private UpgradeSession? _activeSession;

	/// <summary>
	/// Creates the view model. <paramref name="sessionFactory"/> resolves a fresh, fully wired
	/// <see cref="UpgradeSession"/> per run (transient in the composition root) — sessions are single-flight, one
	/// run per instance.
	/// </summary>
	public FirmwareViewModel(
		UpdateCheck check,
		PackagePrep prep,
		UpgradeProtocolProfile profile,
		ConnectionSupervisor supervisor,
		FirmwareConsentGate gate,
		AppLog log,
		IUiDispatcher dispatcher,
		IHeadsetState state,
		Func<UpgradeSession> sessionFactory)
		: base(dispatcher, state)
	{
		_check = check;
		_prep = prep;
		_profile = profile;
		_supervisor = supervisor;
		_gate = gate;
		_log = log;
		_sessionFactory = sessionFactory;

		ProfileHasTunnel = profile.TunnelConnectWord is not null;
		ApplySession();
		ApplySnapshotValues(State.Current);
		_supervisor.StateChanged += OnSupervisorStateChanged;
	}

	/// <summary>True while a headset session is live (the update needs a connected device).</summary>
	[ObservableProperty]
	public partial bool IsConnected { get; private set; }

	/// <summary>Session line ("Not connected." or "Connected · M5AEBT Black · firmware 6.21.5 · Verified").</summary>
	[ObservableProperty]
	public partial string SessionText { get; private set; } = "Not connected.";

	/// <summary>Current device firmware line, read from the snapshot (GAIA 0x1201).</summary>
	[ObservableProperty]
	public partial string CurrentVersionText { get; private set; } = "Current firmware: unknown";

	/// <summary>True while the metadata check (X-09) is in flight.</summary>
	[ObservableProperty]
	public partial bool IsChecking { get; private set; }

	/// <summary>One-line check outcome: update available, up-to-date, or the failure note from X-09.</summary>
	[ObservableProperty]
	public partial string? CheckResultText { get; private set; }

	/// <summary>True when the metadata offers a strictly newer firmware.</summary>
	[ObservableProperty]
	public partial bool UpdateAvailable { get; private set; }

	/// <summary>Published firmware version text ("unknown" when the metadata carried none).</summary>
	[ObservableProperty]
	public partial string LatestVersionText { get; private set; } = "unknown";

	/// <summary>Changelog URL text (empty when the metadata carried none).</summary>
	[ObservableProperty]
	public partial string ChangelogUrlText { get; private set; } = string.Empty;

	/// <summary>Changelog URL for the hyperlink (null when the metadata carried none).</summary>
	[ObservableProperty]
	public partial Uri? ChangelogUri { get; private set; }

	/// <summary>True when the check result carries a package download URL (gates the prepare stage).</summary>
	[ObservableProperty]
	public partial bool HasPackage { get; private set; }

	/// <summary>Package download URL text (empty when absent).</summary>
	[ObservableProperty]
	public partial string PackageUrlText { get; private set; } = string.Empty;

	/// <summary>Declared package size text ("not declared" when the metadata carried none).</summary>
	[ObservableProperty]
	public partial string PackageSizeText { get; private set; } = "not declared";

	/// <summary>Review card: "Installed x → published y" line.</summary>
	[ObservableProperty]
	public partial string ReviewVersionsText { get; private set; } = "Run a check first — the versions land here.";

	/// <summary>True while the package preparation (X-10) is in flight.</summary>
	[ObservableProperty]
	public partial bool IsPreparing { get; private set; }

	/// <summary>Review card: staged package line (image name, size, MD5 verified, cache path).</summary>
	[ObservableProperty]
	public partial string? ReviewPackageText { get; private set; }

	/// <summary>X-10 typed failure as state (class-specific hint plus the exception message); null while healthy.</summary>
	[ObservableProperty]
	public partial string? PrepareFailureText { get; private set; }

	/// <summary>True once a verified package is staged (gates the review and start stages).</summary>
	[ObservableProperty]
	public partial bool Prepared { get; private set; }

	/// <summary>Battery line ("unknown" refuses the update; the level is re-checked by the X-11 precheck).</summary>
	[ObservableProperty]
	public partial string BatteryText { get; private set; } = "Headset battery: unknown";

	/// <summary>True when the snapshot battery is strictly above the profile floor (default 30 %).</summary>
	[ObservableProperty]
	public partial bool BatteryOk { get; private set; }

	/// <summary>True when the upgrade profile carries a tunnel word (a profile without one cannot flash).</summary>
	[ObservableProperty]
	public partial bool ProfileHasTunnel { get; private set; }

	/// <summary>True while an upgrade run (or resume) is in flight.</summary>
	[ObservableProperty]
	public partial bool IsExecuting { get; private set; }

	/// <summary>Current X-11 phase in user-facing words ("Precheck", "Transferring", …).</summary>
	[ObservableProperty]
	public partial string PhaseText { get; private set; } = string.Empty;

	/// <summary>Transfer progress as a 0..1 fraction of the staged image bytes.</summary>
	[ObservableProperty]
	public partial double TransferProgress { get; private set; }

	/// <summary>Transfer line ("x KB of y KB · n chunks").</summary>
	[ObservableProperty]
	public partial string TransferText { get; private set; } = string.Empty;

	/// <summary>Refusal or unexpected failure text; null while healthy.</summary>
	[ObservableProperty]
	public partial string? Warning { get; private set; }

	/// <summary>Affirmative status line (dialog cancelled, package staged, run finished); null while silent.</summary>
	[ObservableProperty]
	public partial string? StatusNote { get; private set; }

	/// <summary>Terminal success text (new version, bytes transferred); null otherwise.</summary>
	[ObservableProperty]
	public partial string? ResultText { get; private set; }

	/// <summary>Terminal failure text (class-specific); null otherwise.</summary>
	[ObservableProperty]
	public partial string? FailureText { get; private set; }

	/// <summary>True when the last failure is resumable and saved progress exists (gates the resume button).</summary>
	[ObservableProperty]
	public partial bool Resumable { get; private set; }

	/// <summary>
	/// Recovery guidance for the current state (link loss, abort, failed verify); null while healthy. The page
	/// shows it through the X-02 RecoveryInfo bar; <see cref="RecoveryHintChanged"/> fires on every change.
	/// </summary>
	public string? RecoveryHint
	{
		get => _recoveryHint;
		private set
		{
			if (SetProperty(ref _recoveryHint, value))
			{
				RecoveryHintChanged?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	private string? _recoveryHint;

	/// <summary>Raised whenever <see cref="RecoveryHint"/> changes (the page shows/dismisses its RecoveryInfo bar).</summary>
	public event EventHandler? RecoveryHintChanged;

	/// <summary>
	/// Stage 0 (X-09): checks the cloud metadata against the device firmware. The check itself never throws for
	/// network or parse failures — they arrive as <see cref="FirmwareUpdateInfo.RawNote"/>; only the unknown-device
	/// firmware and unexpected errors surface as <see cref="Warning"/>.
	/// </summary>
	public async Task CheckAsync()
	{
		if (IsChecking)
		{
			return;
		}

		if (_supervisor.Current is null)
		{
			Warning = "Not connected — connect the headphones before checking for firmware updates.";
			return;
		}

		var current = ReadCurrentVersion();
		if (current is null)
		{
			Warning = "The headset firmware version is unknown — reconnect so the app can read it (GAIA 0x1201).";
			return;
		}

		IsChecking = true;
		Warning = null;
		StatusNote = null;
		CheckResultText = null;
		UpdateAvailable = false;
		HasPackage = false;
		Prepared = false;
		try
		{
			var info = await _check.CheckAsync(current.Value, CancellationToken.None).ConfigureAwait(true);
			ApplyCheckResult(current.Value, info);
			_log.Write($"firmware: check: {info.IsUpdateAvailable} current {current} latest {info.LatestFirmware}");
		}
		catch (Exception ex)
		{
			// Defensive: CheckAsync only throws for cancellation/misuse, never for network or parse failures.
			Warning = $"The update check failed unexpectedly: {ex.Message}";
			_log.Write($"firmware: check failed: {ex.Message}");
		}
		finally
		{
			IsChecking = false;
		}
	}

	/// <summary>
	/// Stage 1 (X-10): download, decrypt, MD5-verify and cache the package. X-10 reports no granular progress, so
	/// the page shows an indeterminate bar; typed <see cref="FirmwarePackageException"/> failures land in
	/// <see cref="PrepareFailureText"/> with a class-specific hint — never a crash.
	/// </summary>
	public async Task PrepareAsync()
	{
		if (IsPreparing)
		{
			return;
		}

		if (_packageUrl is not { } packageUrl)
		{
			Warning = "No package URL — check for updates first.";
			return;
		}

		IsPreparing = true;
		Warning = null;
		PrepareFailureText = null;
		ReviewPackageText = null;
		Prepared = false;
		try
		{
			var package = await _prep.PrepareAsync(packageUrl, CancellationToken.None).ConfigureAwait(true);
			_package = package;
			ReviewPackageText = $"{package.InnerImageName} · {package.ImageBytes / 1024} KB · MD5 verified · cached at {package.PlainImagePath}";
			Prepared = true;
			StatusNote = "Package verified and staged — review it below, then start the update.";
			_log.Write($"firmware: package staged: {package.InnerImageName} ({package.ImageBytes} bytes)");
		}
		catch (FirmwarePackageException ex)
		{
			PrepareFailureText = $"{PackageFailureHint(ex.Reason)} {ex.Message}";
			_log.Write($"firmware: package failed: {ex.Reason}: {ex.Message}");
		}
		catch (Exception ex)
		{
			PrepareFailureText = $"Preparing the package failed unexpectedly: {ex.Message}";
			_log.Write($"firmware: package failed unexpectedly: {ex.Message}");
		}
		finally
		{
			IsPreparing = false;
		}
	}

	/// <summary>
	/// Builds the typed-phrase consent request the page shows before a run: bound to the tunnel-open word (the
	/// first Dangerous frame), with the full consequence text and the X-13 confirm phrase.
	/// </summary>
	public ConsentRequest BuildConsentRequest() => new(
		Title: "Firmware update",
		Consequence: ConsentConsequence,
		ConfirmPhrase: ConfirmPhrase,
		Vendor: _profile.UpgradeVendor,
		Command: _profile.TunnelConnectWord ?? TunnelOpenCommand,
		Context: "Firmware upgrade");

	/// <summary>
	/// Stages 3–6: battery gate (re-read from the snapshot; unknown or at/below the floor refuses), the consent
	/// token from the typed dialog (null = cancelled, nothing is sent), then the X-11 run with progress, abort and
	/// the typed failure/recovery mapping. A fresh <see cref="UpgradeSession"/> is resolved per run.
	/// </summary>
	public async Task ExecuteAsync(ConsentToken? grantedToken)
	{
		if (IsExecuting)
		{
			return;
		}

		if (_package is not { } package)
		{
			Warning = "Prepare the package first — the update needs the verified image.";
			return;
		}

		if (grantedToken is null)
		{
			StatusNote = "Cancelled — nothing was sent to the headphones.";
			return;
		}

		if (_supervisor.Current is null)
		{
			Warning = "Not connected — connect the headphones before starting the update.";
			return;
		}

		var refusal = BatteryRefusal();
		if (refusal is not null)
		{
			Warning = refusal;
			return;
		}

		BeginRun("Precheck");
		_gate.Grant(grantedToken, ConsentConsequence, RunConsentLifetime);
		try
		{
			await RunPackageAsync(package, null, fresh: true).ConfigureAwait(true);
		}
		finally
		{
			IsExecuting = false;
			_gate.Revoke();
		}
	}

	/// <summary>
	/// Continues an interrupted run after a resumable failure (link lost / reply timeout / reconnect timeout):
	/// the same typed-phrase dialog guards the resume, the battery gate re-runs, and the saved progress is fed
	/// back into a fresh session (<see cref="UpgradeSession.ResumeAsync"/> follows the device's resume point).
	/// </summary>
	public async Task ResumeAsync(ConsentToken? grantedToken)
	{
		if (IsExecuting)
		{
			return;
		}

		if (_package is not { } package || _savedProgress is not { } saved)
		{
			Warning = "Nothing to resume — the interrupted run left no saved progress.";
			Resumable = false;
			return;
		}

		if (grantedToken is null)
		{
			StatusNote = "Cancelled — the interrupted update was not resumed.";
			return;
		}

		if (_supervisor.Current is null)
		{
			Warning = "Not connected — reconnect the headphones before resuming the update.";
			return;
		}

		var refusal = BatteryRefusal();
		if (refusal is not null)
		{
			Warning = refusal;
			return;
		}

		BeginRun("Reconnect");
		_gate.Grant(grantedToken, ConsentConsequence, RunConsentLifetime);
		try
		{
			await RunPackageAsync(package, saved, fresh: false).ConfigureAwait(true);
		}
		finally
		{
			IsExecuting = false;
			_gate.Revoke();
		}
	}

	/// <summary>Requests abort of the in-flight run (idempotent): the session sends the best-effort VMU abort sequence.</summary>
	public void Abort() => _activeSession?.Abort();

	private void BeginRun(string initialPhase)
	{
		Resumable = false;
		IsExecuting = true;
		Warning = null;
		StatusNote = null;
		ResultText = null;
		FailureText = null;
		RecoveryHint = null;
		TransferProgress = 0;
		TransferText = string.Empty;
		PhaseText = initialPhase;
	}

	private async Task RunPackageAsync(PreparedPackage package, UpgradeProgress? saved, bool fresh)
	{
		var upgrade = _sessionFactory();
		_activeSession = upgrade;
		upgrade.Transitioned += OnUpgradeTransitioned;
		var progress = new Progress<UpgradeProgress>(OnUpgradeProgress);
		try
		{
			var result = fresh
				? await upgrade.RunAsync(package, _latest, progress, CancellationToken.None).ConfigureAwait(true)
				: await upgrade.ResumeAsync(package, saved!, _latest, progress, CancellationToken.None).ConfigureAwait(true);
			ResultText = result.DeviceVersion is { } version
				? $"Update complete — the headphones now report firmware {version} ({result.BytesSent / 1024} KB transferred in {result.ChunksSent} chunks)."
				: $"Update complete — {result.BytesSent / 1024} KB transferred in {result.ChunksSent} chunks (the profile carries no version read, so the new version was not re-read).";
			RecoveryHint = null;
			StatusNote = "Firmware update finished — run the check again to confirm the installed version.";
			Resumable = false;
			_log.Write($"firmware: run complete: {result.BytesSent} bytes, device {result.DeviceVersion}");
		}
		catch (UpgradeSessionException ex)
		{
			_savedProgress = upgrade.Current;
			var resumable = ex.Reason is UpgradeSessionFailure.LinkLost
				or UpgradeSessionFailure.ReplyTimeout
				or UpgradeSessionFailure.ReconnectFailed;
			Resumable = resumable && _savedProgress is not null;
			FailureText = SessionFailureText(ex.Reason);
			RecoveryHint = resumable ? LinkLossRecoveryHint : DefaultRecoveryHint;
			_log.Write($"firmware: run failed: {ex.Reason}: {ex.Message}");
		}
		catch (OperationCanceledException)
		{
			FailureText = "The update was aborted — a best-effort abort sequence was sent to the headphones. If they do not respond or reconnect afterwards, follow the recovery guide.";
			RecoveryHint = DefaultRecoveryHint;
			Resumable = false;
			_log.Write("firmware: run aborted");
		}
		catch (Exception ex)
		{
			// Defensive: surface, never crash.
			FailureText = $"The update failed unexpectedly: {ex.Message}";
			RecoveryHint = DefaultRecoveryHint;
			Resumable = false;
			_log.Write($"firmware: run failed unexpectedly: {ex.Message}");
		}
		finally
		{
			upgrade.Transitioned -= OnUpgradeTransitioned;
			_activeSession = null;
		}
	}

	private void ApplyCheckResult(FirmwareVersion current, FirmwareUpdateInfo info)
	{
		UpdateAvailable = info.IsUpdateAvailable;
		HasPackage = info.PackageUrl is not null;
		LatestVersionText = info.LatestFirmware?.ToString() ?? "unknown";
		ChangelogUri = info.ChangelogUrl;
		ChangelogUrlText = info.ChangelogUrl?.ToString() ?? string.Empty;
		PackageUrlText = info.PackageUrl?.ToString() ?? string.Empty;
		PackageSizeText = info.PackageBytes is { } bytes ? $"{bytes / 1024} KB ({bytes} bytes)" : "not declared";
		CheckResultText = info.IsUpdateAvailable
			? $"Update available: {current} \u2192 {info.LatestFirmware}"
			: info.RawNote ?? $"Firmware {current} is up to date.";
		ReviewVersionsText = $"Installed {current} \u2192 published {info.LatestFirmware?.ToString() ?? "unknown"}";
		_latest = info.LatestFirmware;
		_packageUrl = info.PackageUrl;
	}

	private void OnUpgradeProgress(UpgradeProgress value)
	{
		PhaseText = DescribePhase(value.State);
		TransferProgress = value.TotalBytes > 0 ? Math.Min(1, (double)value.BytesSent / value.TotalBytes) : 0;
		TransferText = $"{value.BytesSent / 1024} KB of {value.TotalBytes / 1024} KB · {value.ChunksSent} chunks";
	}

	private void OnUpgradeTransitioned(object? sender, UpgradeTransition transition)
	{
		_log.Write($"firmware: {transition.From} \u2192 {transition.To}: {transition.Note}");
		Post(() => PhaseText = DescribePhase(transition.To));
	}

	private string? BatteryRefusal()
	{
		if (!Snapshot.TryGet(Keys.BatteryPercent, out int percent))
		{
			return "The headset battery level is unknown — the update refuses to start until the level is read.";
		}

		if (percent <= _profile.BatteryFloorPercent)
		{
			return $"The headset battery is at {percent} % — the update needs more than {_profile.BatteryFloorPercent} % to start. Charge the headphones first.";
		}

		return null;
	}

	private FirmwareVersion? ReadCurrentVersion()
	{
		if (State.Current.TryGet(Keys.Firmware, out FirmwareVersion fromSnapshot))
		{
			return fromSnapshot;
		}

		return FirmwareVersion.TryParse(_supervisor.Current?.Info.Firmware, out var parsed) ? parsed : null;
	}

	private void OnSupervisorStateChanged(object? sender, SessionState state) => Post(ApplySession);

	private void ApplySession()
	{
		var session = _supervisor.Current;
		IsConnected = session is not null;
		SessionText = session is null
			? "Not connected."
			: $"Connected · {session.Info.Model} · firmware {session.Info.Firmware} · {session.Info.Trust}";
		ApplyVersionText();
	}

	/// <inheritdoc />
	protected override void OnSnapshot(HeadsetSnapshot snapshot) => ApplySnapshotValues(snapshot);

	private void ApplySnapshotValues(HeadsetSnapshot snapshot)
	{
		ApplyVersionText();
		if (snapshot.TryGet(Keys.BatteryPercent, out int percent))
		{
			BatteryOk = percent > _profile.BatteryFloorPercent;
			BatteryText = BatteryOk
				? $"Headset battery: {percent} % — above the {_profile.BatteryFloorPercent} % floor."
				: $"Headset battery: {percent} % — at or below the {_profile.BatteryFloorPercent} % floor. Charge the headphones before updating.";
		}
		else
		{
			BatteryOk = false;
			BatteryText = "Headset battery: unknown — the update refuses to start until the level is read.";
		}
	}

	private void ApplyVersionText()
	{
		var current = ReadCurrentVersion();
		CurrentVersionText = current is null ? "Current firmware: unknown" : $"Current firmware: {current}";
	}

	/// <summary>Class-specific user text for an X-11 failure (each maps to one recovery hint).</summary>
	private static string SessionFailureText(UpgradeSessionFailure reason) => reason switch
	{
		UpgradeSessionFailure.AlreadyRunning => "A firmware run is already in flight on this session.",
		UpgradeSessionFailure.SessionEnded => "The upgrade session already ended — start a new flash round.",
		UpgradeSessionFailure.NoConnection => "No live headset session — reconnect the headphones and try again.",
		UpgradeSessionFailure.BatteryUnknown => "The headset battery level could not be read — the session refuses to start (unknown = refuse).",
		UpgradeSessionFailure.BatteryLow => "The headset battery is at or below the floor — charge the headphones and start again.",
		UpgradeSessionFailure.ConsentRequired => "No consent token was available for a Dangerous frame — nothing was sent. Start again and confirm the typed dialog.",
		UpgradeSessionFailure.UnobservedCommand => "The upgrade profile does not carry a word the session needed — refusing to send an unobserved command (R-06 capture pending).",
		UpgradeSessionFailure.GateDenied => "The safety gate refused a frame (DevMode off, or the consent token was rejected/expired) — nothing was sent.",
		UpgradeSessionFailure.PackageInvalid => "The staged package is unusable — prepare it again.",
		UpgradeSessionFailure.DeviceNotReady => "The headphones stayed \u201Cnot ready\u201D through all start retries.",
		UpgradeSessionFailure.DeviceRejected => "The headphones rejected a command or reported a fatal upgrade error.",
		UpgradeSessionFailure.DeviceAborted => "The headphones confirmed an abort on their own.",
		UpgradeSessionFailure.LinkLost => "The Bluetooth link to the headphones was lost during the update — the transfer stopped and can be resumed.",
		UpgradeSessionFailure.ReplyTimeout => "The headphones stopped answering during the update — the run can be resumed.",
		UpgradeSessionFailure.ReconnectFailed => "The headphones did not come back within the reconnect window after the commit — the run can be resumed.",
		UpgradeSessionFailure.ProtocolError => "A malformed or unexpected upgrade message arrived.",
		UpgradeSessionFailure.RestartRefused => "The device restarted the transfer from zero although bytes were already sent, and the profile policy says refuse.",
		UpgradeSessionFailure.VersionMismatch => "The version re-read after the update does not match the expected version — treat the update as failed.",
		_ => "The upgrade session failed.",
	};

	/// <summary>Class-specific user text for an X-10 failure (each maps to one recovery hint).</summary>
	private static string PackageFailureHint(FirmwarePackageFailure reason) => reason switch
	{
		FirmwarePackageFailure.Download => "The package could not be downloaded or staged — check the network and retry (the cache self-heals).",
		FirmwarePackageFailure.Decrypt => "The package could not be decrypted — the package format may have changed.",
		FirmwarePackageFailure.NotAZip => "The decrypted package is not a valid ZIP — the package format may have changed.",
		FirmwarePackageFailure.MissingImage => "The package contains no usable image entry.",
		FirmwarePackageFailure.MissingMd5 => "The package contains no usable MD5 manifest.",
		FirmwarePackageFailure.Md5Mismatch => "The package failed its MD5 integrity check and was NOT staged. Retry the download; a persistent mismatch means the published package is corrupt.",
		_ => "The package could not be prepared.",
	};

	private static string DescribePhase(UpgradeSessionState state) => state switch
	{
		UpgradeSessionState.Idle => "Idle",
		UpgradeSessionState.Precheck => "Precheck (battery, package, profile)",
		UpgradeSessionState.Connect => "Opening the upgrade tunnel",
		UpgradeSessionState.AwaitReady => "Synchronizing with the headphones",
		UpgradeSessionState.Start => "Starting the data transfer",
		UpgradeSessionState.Transfer => "Transferring",
		UpgradeSessionState.Commit => "Committing (the headphones confirm and reboot)",
		UpgradeSessionState.End => "Completing",
		UpgradeSessionState.Reconnect => "Waiting for the headphones to reconnect",
		UpgradeSessionState.Verify => "Verifying the new firmware version",
		UpgradeSessionState.Done => "Done",
		UpgradeSessionState.Failed => "Failed",
		UpgradeSessionState.Aborted => "Aborted",
		_ => state.ToString(),
	};

	private const string LinkLossRecoveryHint =
		"The link dropped mid-update. Wait about half a minute for the headphones (they may reboot), reconnect them, then choose \u201CResume update\u201D to continue the transfer where it stopped. If they do not come back, follow the 20-second button reset in docs/recovery.md.";

	private const string DefaultRecoveryHint =
		"If the headphones do not reconnect or behave erratically, follow docs/recovery.md: the 20-second button reset, re-flashing through the official Sennheiser Smart Control Plus app, and how to collect the frame log for a bug report.";
}
