using System;
using System.Collections.ObjectModel;
using Momentum5.App.ViewModels;
using Momentum5.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.Client;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Momentum5.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
	public DiagnosticsPage()
	{
		InitializeComponent();
		_refresh = new Momentum5.App.Services.UiCoalescer(App.Services.Ui, Rebuild);
		LogPath.Text = $"Frame log (JSONL): {App.Services.FrameLogDirectory}";
		Loaded += (_, _) =>
		{
			var s = App.Services;
			s.Store.Changed += OnStore;
			s.Supervisor.StateChanged += OnSession;
			s.SettingsChanged += OnSettings;
			Rebuild();
		};
		Unloaded += (_, _) =>
		{
			var s = App.Services;
			s.Store.Changed -= OnStore;
			s.Supervisor.StateChanged -= OnSession;
			s.SettingsChanged -= OnSettings;
		};
		Rebuild();
	}

	/// <summary>Connection metadata, always including the transport (the only place it is shown without developer mode). Kept live.</summary>
	private readonly Momentum5.App.Services.UiCoalescer _refresh;

	private ObservableCollection<InfoRow> Info { get; } = [];

	private void OnStore(object? sender, HeadsetSnapshot snapshot) => _refresh.Request();

	private void OnSession(object? sender, SessionState state) => _refresh.Request();

	private void OnSettings(object? sender, EventArgs e) => Rebuild();

	private void Rebuild()
	{
		var s = App.Services;
		var session = s.Store.Current.Session;
		var state = s.Supervisor.State;
		var rows = new (string, string)[]
		{
			("Connection", state.Detail is { Length: > 0 } d ? $"{state.Phase} · {d}" : state.Phase.ToString()),
			("Transport", session?.TransportId ?? "—"),
			("Model", session?.Model ?? "—"),
			("Firmware", session?.Firmware ?? "—"),
			("Serial", session?.Serial is { Length: > 0 } sn ? (s.Current.AnonymizeDiagnostics ? "masked" : sn) : "—"),
			("Trust", session?.Trust.ToString() ?? "—"),
			("Features", session is { Features.Count: > 0 } ? string.Join(", ", session.Features) : "—"),
			("Device address", s.Current.LastDeviceAddress is { } a ? (s.Current.AnonymizeDiagnostics ? "masked" : a.ToString("X12", System.Globalization.CultureInfo.InvariantCulture)) : "—"),
			("Simulator", s.Simulated ? "Yes" : "No"),
		};
		for (var i = 0; i < rows.Length; i++)
		{
			var row = new InfoRow(rows[i].Item1, rows[i].Item2);
			if (i < Info.Count)
			{
				if (Info[i] != row)
				{
					Info[i] = row;
				}
			}
			else
			{
				Info.Add(row);
			}
		}
	}

	private async void OpenLogs_Click(object sender, RoutedEventArgs e)
	{
		System.IO.Directory.CreateDirectory(App.Services.FrameLogDirectory);
		await Launcher.LaunchFolderPathAsync(App.Services.FrameLogDirectory);
	}

	private void OpenProbe_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(GattLabPage));

	private void Trace_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(TracePage));

	private void Adverts_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(AdvertsPage));

	private async void Export_Click(object sender, RoutedEventArgs e)
	{
		var picker = new FileSavePicker { SuggestedFileName = $"momentum5-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}" };
		picker.FileTypeChoices.Add("Zip archive", [".zip"]);
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainShell));
		var file = await picker.PickSaveFileAsync();
		if (file is null)
		{
			return;
		}

		try
		{
			var s = App.Services;
			await ExportBundle.CreateAsync(file.Path, s.FrameLogDirectory, s.Store.Current, s.Current, s.Current.AnonymizeDiagnostics);
			ExportStatus.Text = $"Saved {file.Path}";
		}
		catch (Exception ex)
		{
			ExportStatus.Text = $"Export failed: {ex.Message}";
		}
	}
}
