using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App;
using Momentum5.App.Shell;
using Momentum5.App.ViewModels;
using Momentum5.Core.Settings;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// X-05 touch buttons (MMI) page (Advanced hub tag "mmi"): the raw 1601 slot table and the 1605 default-state
/// row — read-only, because the payload format is not decoded (R-03/R-04). The editor (SET 1600) is deferred
/// until the R-04 capture; the 1604 reset stays a Blocked raw row reachable only through the GAIA console.
/// </summary>
public sealed partial class MmiPage : Page
{
	public MmiPage()
	{
		ViewModel = new MmiViewModel(
			App.Service<IUiDispatcher>(),
			App.Service<IHeadsetState>(),
			App.Service<ISettingService>());
		InitializeComponent();
	}

	public MmiViewModel ViewModel { get; }

	/// <summary>Seeds the page from the device (snapshot events only reach VMs created before they fired).</summary>
	private void OnPageLoaded(object sender, RoutedEventArgs e) => _ = ViewModel.InitializeAsync();

	private void OnReadClick(object sender, RoutedEventArgs e) => _ = ViewModel.ReadAsync();

	// x:Bind function helpers (instance members, as this XAML compiler emits instance calls).

	public Visibility WarningVis(string? warning) =>
		string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility BusyVis(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;

	public Visibility NotAvailableVis(bool available) => available ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Read button enabled while no read is in flight.</summary>
	public bool NotBusy(bool busy) => !busy;
}
