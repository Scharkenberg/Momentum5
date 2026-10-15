using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App;
using Momentum5.App.Shell;
using Momentum5.App.ViewModels;
using Momentum5.Client;
using Momentum5.Core.Settings;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// X-13 firmware update page (Advanced hub tag "firmware", DevMode-gated by the shell navigation entry): the
/// staged flow check (X-09) → prepare (X-10) → review → battery gate → typed-phrase consent (X-02 dialog, invoked
/// from this code-behind) → execute (X-11 progress/abort/resume) → result with recovery guidance, plus the
/// link-loss RecoveryInfo bar. All flow logic lives in <see cref="FirmwareViewModel"/>; this code-behind builds
/// it, forwards clicks, and drives the dialog and the recovery bar.
/// </summary>
public sealed partial class FirmwarePage : Page
{
	private readonly ConsentDialogService _dialog;

	public FirmwarePage()
	{
		ViewModel = new FirmwareViewModel(
			App.Service<UpdateCheck>(),
			App.Service<PackagePrep>(),
			App.Service<UpgradeProtocolProfile>(),
			App.Service<ConnectionSupervisor>(),
			App.Service<FirmwareConsentGate>(),
			App.Service<AppLog>(),
			App.Service<IUiDispatcher>(),
			App.Service<IHeadsetState>(),
			() => App.Service<UpgradeSession>());
		_dialog = App.Service<ConsentDialogService>();
		ViewModel.RecoveryHintChanged += OnRecoveryHintChanged;
		InitializeComponent();
	}

	/// <summary>The page view model (built from the composition root before InitializeComponent).</summary>
	public FirmwareViewModel ViewModel { get; }

	private void OnCheckClick(object sender, RoutedEventArgs e) => _ = ViewModel.CheckAsync();

	private void OnPrepareClick(object sender, RoutedEventArgs e) => _ = ViewModel.PrepareAsync();

	private async void OnBeginUpdateClick(object sender, RoutedEventArgs e)
	{
		var token = await _dialog.ConfirmAsync(ViewModel.BuildConsentRequest()).ConfigureAwait(true);
		await ViewModel.ExecuteAsync(token).ConfigureAwait(true);
	}

	private async void OnResumeClick(object sender, RoutedEventArgs e)
	{
		var token = await _dialog.ConfirmAsync(ViewModel.BuildConsentRequest()).ConfigureAwait(true);
		await ViewModel.ResumeAsync(token).ConfigureAwait(true);
	}

	private void OnAbortClick(object sender, RoutedEventArgs e) => ViewModel.Abort();

	/// <summary>Shows/dismisses the page-owned recovery bar when the view model's recovery guidance changes.</summary>
	private void OnRecoveryHintChanged(object? sender, EventArgs e)
	{
		if (ViewModel.RecoveryHint is { } hint)
		{
			RecoveryInfo.Show(RecoveryBar, "Firmware update interrupted", hint);
		}
		else
		{
			RecoveryInfo.Dismiss(RecoveryBar);
		}
	}

	// x:Bind function helpers (instance members, as this XAML compiler emits instance calls).

	public Visibility WarningVis(string? warning) =>
		string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility TextVis(string? text) =>
		string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Inverse of <see cref="TextVis"/> — the placeholder line for an absent value.</summary>
	public Visibility EmptyTextVis(string? text) =>
		string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

	public Visibility BusyVis(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;

	public Visibility NotConnectedVis(bool connected) => connected ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Shown while the gated value is not available yet.</summary>
	public Visibility NotAvailableVis(bool available) => available ? Visibility.Collapsed : Visibility.Visible;

	public Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Check button: enabled while no check is in flight.</summary>
	public bool CanCheck(bool checking) => !checking;

	/// <summary>Prepare button: needs an available update with a package URL and no preparation in flight.</summary>
	public bool CanPrepare(bool updateAvailable, bool hasPackage, bool preparing) =>
		updateAvailable && hasPackage && !preparing;

	/// <summary>Start button: needs a session, a staged package, a healthy battery, no in-flight run, and a profile tunnel word.</summary>
	public bool CanBegin(bool connected, bool prepared, bool batteryOk, bool executing, bool profileHasTunnel) =>
		connected && prepared && batteryOk && !executing && profileHasTunnel;

	/// <summary>Abort button: enabled while a run is in flight.</summary>
	public bool CanAbort(bool executing) => executing;

	/// <summary>Resume button: a resumable failure, no in-flight run, and a live session.</summary>
	public bool CanResumeUpdate(bool resumable, bool executing, bool connected) =>
		resumable && !executing && connected;
}
