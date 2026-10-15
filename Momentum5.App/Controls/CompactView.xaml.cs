using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Momentum5.App.Pages;
using Momentum5.App.ViewModels;
using Momentum5.Client;

namespace Momentum5.App.Controls;

/// <summary>
/// The compact shell: a tight header, an icon rail covering every section of the app, and either the quick controls
/// or the full page for the selected section (rendered with compact density). Hosted by the compact window mode and the tray flyout.
/// </summary>
public sealed partial class CompactView : UserControl
{
	/// <summary>Preferred width in DIPs for hosts that size themselves.</summary>
	public const double PreferredWidth = 360;

	private static readonly Dictionary<string, Type> Pages = new()
	{
		["Noise"] = typeof(NoiseControlPage),
		["Sound"] = typeof(SoundPage),
		["Spatial"] = typeof(SpatialPage),
		["Connections"] = typeof(ConnectionPage),
		["Multipoint"] = typeof(MultipointPage),
		["DeviceSettings"] = typeof(DeviceSettingsPage),
		["Diagnostics"] = typeof(DiagnosticsPage),
		["Advanced"] = typeof(Pages.Advanced.AdvancedPage),
		["Settings"] = typeof(SettingsPage),
	};

	public CompactView()
	{
		Vm = new DashboardViewModel(App.Services);
		Presets = new PresetsViewModel(App.Services);
		InitializeComponent();
		App.Services.SettingsChanged += OnSettingsChanged;
		ApplyDevMode();
		HeaderCard.SizeChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
		Home.SizeChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
	}

	public DashboardViewModel Vm { get; }

	public PresetsViewModel Presets { get; }

	/// <summary>Show the "open full app" button (only the tray flyout does).</summary>
	public bool ShowOpenFull
	{
		get => OpenFullButton.Visibility == Visibility.Visible;
		set => OpenFullButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>Height the header plus the quick controls need; hosts fit their window to it.</summary>
	public double HomeContentHeight => HeaderCard.ActualHeight + HeaderCard.Margin.Top + HeaderCard.Margin.Bottom + Home.ContentHeight;

	/// <summary>True while the quick controls (not another section) are shown.</summary>
	public bool IsHome => HomeScroll.Visibility == Visibility.Visible;

	/// <summary>Raised when the header or the quick controls change size.</summary>
	public event EventHandler? LayoutChanged;

	/// <summary>Raised when the user asks for the full window.</summary>
	public event EventHandler? OpenFullRequested;

	/// <summary>Releases subscriptions (called when the owning window closes).</summary>
	public void Shutdown()
	{
		App.Services.SettingsChanged -= OnSettingsChanged;
		Vm.Dispose();
	}

	/// <summary>Refreshes data that is not pushed (the multipoint list); called whenever the flyout is shown.</summary>
	public void Refresh() => _ = Vm.LoadConnectionsAsync();

	private void OnSettingsChanged(object? sender, EventArgs e) => ApplyDevMode();

	private void ApplyDevMode() =>
		AdvancedRailItem.Visibility = App.Services.Current.DevModeUnlocked ? Visibility.Visible : Visibility.Collapsed;

	private void Rail_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// SelectedIndex="0" fires this during InitializeComponent, before the named elements are assigned.
		if (HomeScroll is null || PageFrame is null || Rail.SelectedItem is not ListViewItem { Tag: string tag })
		{
			return;
		}

		if (tag == "Home")
		{
			PageFrame.Visibility = Visibility.Collapsed;
			HomeScroll.Visibility = Visibility.Visible;
		}
		else if (Pages.TryGetValue(tag, out var page))
		{
			HomeScroll.Visibility = Visibility.Collapsed;
			PageFrame.Visibility = Visibility.Visible;
			PageFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
		}
	}

	private void Connect_Click(object sender, RoutedEventArgs e)
	{
		var services = App.Services;
		if (services.Supervisor.State.Phase is SessionPhase.Ready or SessionPhase.ReadOnly or SessionPhase.Connecting)
		{
			services.Disconnect();
		}
		else
		{
			services.Connect();
		}
	}

	private void OpenFull_Click(object sender, RoutedEventArgs e) => OpenFullRequested?.Invoke(this, EventArgs.Empty);
}
