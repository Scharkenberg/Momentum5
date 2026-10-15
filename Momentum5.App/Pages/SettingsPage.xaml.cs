using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;
using Windows.System;

namespace Momentum5.App.Pages;

public sealed partial class SettingsPage : Page
{
	public SettingsPage()
	{
		Vm = new SettingsViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public SettingsViewModel Vm { get; }

	public Visibility Locked(bool unlocked) => unlocked ? Visibility.Collapsed : Visibility.Visible;

	public Visibility Unlocked(bool unlocked) => unlocked ? Visibility.Visible : Visibility.Collapsed;

	private async void OpenLogs_Click(object sender, RoutedEventArgs e)
	{
		System.IO.Directory.CreateDirectory(Vm.LogFolder);
		await Launcher.LaunchFolderPathAsync(Vm.LogFolder);
	}

	private void RemoveZone_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: ZoneRow row })
		{
			Vm.RemoveZoneCommand.Execute(row);
		}
	}
}
