using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class DeviceSettingsPage : Page
{
	public DeviceSettingsPage()
	{
		Vm = new DeviceSettingsViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public DeviceSettingsViewModel Vm { get; }

	public double LanguageOpacity(bool enabled) => enabled ? 1.0 : 0.4;

	public Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

	private void Name_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DevicePage));
}
