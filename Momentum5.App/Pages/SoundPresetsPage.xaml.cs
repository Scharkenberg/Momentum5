using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Momentum5.App.Pages;

public sealed partial class SoundPresetsPage : Page
{
	public SoundPresetsPage()
	{
		Vm = new PresetsViewModel(App.Services);
		InitializeComponent();
	}

	public PresetsViewModel Vm { get; }

	private void Back_Click(object sender, RoutedEventArgs e)
	{
		if (Frame.CanGoBack)
		{
			Frame.GoBack();
		}
		else
		{
			Frame.Navigate(typeof(SoundPage));
		}
	}

	private static nint WindowHandle() => WindowNative.GetWindowHandle(App.MainShell);

	private async void ExportFile_Click(object sender, RoutedEventArgs e)
	{
		var picker = new FileSavePicker { SuggestedFileName = "momentum5-presets" };
		picker.FileTypeChoices.Add("JSON", [".json"]);
		InitializeWithWindow.Initialize(picker, WindowHandle());
		var file = await picker.PickSaveFileAsync();
		if (file is not null)
		{
			await FileIO.WriteTextAsync(file, Vm.ExportJson());
		}
	}

	private async void ImportFile_Click(object sender, RoutedEventArgs e)
	{
		var picker = new FileOpenPicker();
		picker.FileTypeFilter.Add(".json");
		InitializeWithWindow.Initialize(picker, WindowHandle());
		var file = await picker.PickSingleFileAsync();
		if (file is not null)
		{
			Vm.Import(await FileIO.ReadTextAsync(file));
		}
	}

	private void CopyJson_Click(object sender, RoutedEventArgs e)
	{
		var data = new DataPackage();
		data.SetText(Vm.ExportJson());
		Clipboard.SetContent(data);
	}

	private async void PasteJson_Click(object sender, RoutedEventArgs e)
	{
		var content = Clipboard.GetContent();
		if (content.Contains(StandardDataFormats.Text))
		{
			Vm.Import(await content.GetTextAsync());
		}
	}
}
