using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Momentum5.App.Pages;

public sealed partial class GattLabPage : Page
{
	public GattLabPage()
	{
		Vm = new GattLabViewModel(App.Services);
		Vm.ReportRequested += OnReportRequested;
		InitializeComponent();
		Unloaded += (_, _) =>
		{
			Vm.ReportRequested -= OnReportRequested;
			Vm.Dispose();
		};
	}

	public GattLabViewModel Vm { get; }

	public Visibility BusyVisibility(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;

	private static void OnReportRequested(object? sender, string json)
	{
		var data = new DataPackage();
		data.SetText(json);
		Clipboard.SetContent(data);
	}

	private async void Save_Click(object sender, RoutedEventArgs e)
	{
		var json = Vm.GetReportJson();
		if (json is null)
		{
			Vm.Status = "Run the diagnostics first; there is no report yet.";
			return;
		}

		var picker = new FileSavePicker { SuggestedFileName = $"m5-gatt-{DateTime.Now:yyyyMMdd-HHmmss}" };
		picker.FileTypeChoices.Add("JSON", [".json"]);
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainShell));
		var file = await picker.PickSaveFileAsync();
		if (file is not null)
		{
			await FileIO.WriteTextAsync(file, json);
		}
	}
}
