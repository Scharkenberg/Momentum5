using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.Shell;
using Momentum5.App.ViewModels;
using Momentum5.Core.Settings;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// X-04 parametric EQ page: a read-only dump of the per-band table (band number, centre frequency,
/// raw Q byte and raw filter-type byte) with a "Read from device" button. The write commands for the
/// PEQ family are not confirmed for this firmware (research lanes R-04/R-05 pending), so the page
/// never writes; a firmware rejection (error 00/01) latches a read-only note in the view model.
/// </summary>
public sealed partial class ParametricEqPage : Page
{
	public ParametricEqPage()
	{
		ViewModel = new ParametricEqViewModel(
			App.Service<IUiDispatcher>(),
			App.Service<IHeadsetState>(),
			App.Service<ISettingService>());
		InitializeComponent();
	}

	public ParametricEqViewModel ViewModel { get; }

	/// <summary>Seeds the page from the device (snapshot events only reach VMs created before they fired).</summary>
	private void OnPageLoaded(object sender, RoutedEventArgs e) => _ = ViewModel.InitializeAsync();

	private void OnReadFromDevice(object sender, RoutedEventArgs e) => _ = ViewModel.RefreshAsync();

	public Visibility WarningVis(string? text) =>
		string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility BusyVis(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;

	public bool NotBusy(bool busy) => !busy;
}
