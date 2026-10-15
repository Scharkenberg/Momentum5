using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App;
using Momentum5.App.Shell;
using Momentum5.App.ViewModels;
using Momentum5.Client;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// X-08 factory reset page (Advanced hub tag "reset", DevMode-gated by the shell navigation entry):
/// explanation card, arming checkbox, typed-RESET consent dialog (X-02) and the S:0040 send, then the
/// app-side cleanup (last device forgotten, EQ presets kept) and the re-pair guide. All logic lives in
/// <see cref="FactoryResetViewModel"/>; this code-behind builds it and forwards the button click.
/// </summary>
public sealed partial class FactoryResetPage : Page
{
	public FactoryResetPage()
	{
		ViewModel = new FactoryResetViewModel(
			App.Service<ConnectionSupervisor>(),
			App.Service<ConsentDialogService>(),
			App.Service<StartupController>(),
			App.Service<AppLog>(),
			App.Service<IUiDispatcher>());
		InitializeComponent();
	}

	public FactoryResetViewModel ViewModel { get; }

	private async void OnResetClick(object sender, RoutedEventArgs e) => await ViewModel.ResetAsync().ConfigureAwait(true);

	// x:Bind function helpers (instance members, as this XAML compiler emits instance calls).

	public Visibility WarningVis(string? warning) =>
		string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility TextVis(string? text) =>
		string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility BusyVis(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;

	public Visibility NotConnectedVis(bool connected) => connected ? Visibility.Collapsed : Visibility.Visible;

	public Visibility DoneVis(bool done) => done ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>The arming checkbox stays usable until the reset flow completed once.</summary>
	public bool CanArm(bool done) => !done;

	/// <summary>Reset button: needs a session, the arming checkbox, no in-flight flow, and not already done.</summary>
	public bool CanReset(bool connected, bool? acknowledged, bool busy, bool done) =>
		connected && acknowledged == true && !busy && !done;
}
