using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class DevicePage : Page
{
	public DevicePage()
	{
		Vm = new DeviceViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public DeviceViewModel Vm { get; }

	private async void Rename_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new ContentDialog
		{
			XamlRoot = XamlRoot,
			Title = "Rename headset?",
			Content = $"The headset name changes from '{Vm.CurrentName}' to '{Vm.NameInput}'. Other devices may show the new name after reconnecting.",
			PrimaryButtonText = "Rename",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close,
		};
		if (await dialog.ShowAsync() == ContentDialogResult.Primary)
		{
			await Vm.RenameAsync();
		}
	}
}
