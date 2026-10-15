using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.Shell;
using Momentum5.App.ViewModels;
using Momentum5.Client;
using Windows.ApplicationModel.DataTransfer;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// X-03 GAIA console (Advanced sub-page "console", DevMode-gated by the shell navigation entry): catalog
/// browser over <see cref="Momentum5.Protocol.GaiaCatalog"/>, guarded sender and described TX/RX log. The
/// view model owns all logic (client-side safety rules, per-session Blocked overrides, log cap); this
/// code-behind does the WinRT bits — clipboard copy and log auto-scroll, throttled to one call per 200 ms
/// like the trace tab.
/// </summary>
public sealed partial class ConsolePage : Page
{
	private static readonly TimeSpan ScrollThrottle = TimeSpan.FromMilliseconds(200);

	private readonly Stopwatch _scrollWatch = Stopwatch.StartNew();

	public ConsolePage()
	{
		ViewModel = new ConsoleViewModel(
			App.Service<ConnectionSupervisor>(),
			App.Service<ConsentService>(),
			App.Service<AppLog>(),
			App.Service<IUiDispatcher>());
		InitializeComponent();
		ViewModel.LogAppended += OnLogAppended;
	}

	public ConsoleViewModel ViewModel { get; }

	private void OnFilterChanged(object sender, TextChangedEventArgs e)
	{
		if (sender is TextBox box)
		{
			ViewModel.SetFilter(box.Text);
		}
	}

	private void OnSafetyFilterChanged(object sender, SelectionChangedEventArgs e)
	{
		if (sender is ComboBox { SelectedIndex: >= 0 } box)
		{
			ViewModel.SetSafetyFilter(box.SelectedIndex);
		}
	}

	private void OnSendOptionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (sender is RadioButtons { SelectedIndex: >= 0 } buttons)
		{
			ViewModel.SetSendOption(buttons.SelectedIndex);
		}
	}

	private async void OnSendClick(object sender, RoutedEventArgs e) => await ViewModel.SendAsync().ConfigureAwait(true);

	private void OnUnlockClick(object sender, RoutedEventArgs e) => ViewModel.TryUnlock();

	private void OnClearLogClick(object sender, RoutedEventArgs e) => ViewModel.ClearLog();

	private void OnCopyLogClick(object sender, RoutedEventArgs e) => CopyLog();

	private void OnLogAppended(object? sender, int appendedCount)
	{
		if (appendedCount <= 0 || _scrollWatch.Elapsed < ScrollThrottle)
		{
			return;
		}

		var rows = ViewModel.LogLines;
		if (rows.Count == 0)
		{
			return;
		}

		_scrollWatch.Restart();
		LogList.ScrollIntoView(rows[rows.Count - 1]);
	}

	/// <summary>Copies the whole console log (timestamped lines) to the clipboard.</summary>
	private void CopyLog()
	{
		var text = ViewModel.GetLogText();
		if (text.Length == 0)
		{
			return;
		}

		try
		{
			var package = new DataPackage();
			package.SetText(text);
			Clipboard.SetContent(package);
		}
		catch (Exception ex) when (ex is InvalidOperationException or COMException)
		{
			// clipboard unavailable (rare): the log itself is unaffected
			Debug.WriteLine($"console clipboard: {ex.Message}");
		}
	}

	// x:Bind function helpers (instance members, as this XAML compiler emits instance calls).

	public Visibility BoolVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	public Visibility NotVis(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Static twin for bindings qualified with the page type inside data templates.</summary>
	public static Visibility TextVisStatic(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility TextVis(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
}
