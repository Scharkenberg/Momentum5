using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Momentum5.App.Pages;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Shell;

public sealed partial class ShellWindow : Window
{
	private static readonly Dictionary<string, Type> PageTypes = new()
	{
		["Dashboard"] = typeof(DashboardPage),
		["Noise"] = typeof(NoiseControlPage),
		["Sound"] = typeof(SoundPage),
		["Spatial"] = typeof(SpatialPage),
		["DeviceSettings"] = typeof(DeviceSettingsPage),
		["Device"] = typeof(DevicePage),
		["Multipoint"] = typeof(MultipointPage),
		["Connections"] = typeof(ConnectionPage),
		["Diagnostics"] = typeof(DiagnosticsPage),
		["Advanced"] = typeof(Pages.Advanced.AdvancedPage),
		["Settings"] = typeof(SettingsPage),
	};

	private readonly ConnectionStatusViewModel _status;

	public ShellWindow()
	{
		InitializeComponent();
		var services = App.Services;
		Title = services.Simulated ? "Momentum5 (simulator)" : "Momentum5";
		TitleText.Text = Title;
		ExtendsContentIntoTitleBar = true;
		AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
		TrySetIcon();
		InstallMinSize();
		Activated += (_, args) => CompactGlyph.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.5 : 1.0;
		SetTitleBar(DragArea);
		Root.Loaded += (_, _) =>
		{
			UpdateTitleInset();
			ApplyCaptionColors();
			UpdateLogo();
			Root.XamlRoot.Changed += (_, _) => UpdateLogo();
			if (_compactApplied && !_compactPlaced)
			{
				// Started in compact mode: fit to the content once it has been laid out (the only time the height may grow).
				DispatcherQueue.TryEnqueue(() => FitCompact(true));
			}
		};
		Root.SizeChanged += (_, _) =>
		{
			UpdateTitleInset();
			UpdateAutoCompact();
		};
		Root.ActualThemeChanged += (_, _) => ApplyCaptionColors();

		_status = new ConnectionStatusViewModel(services);
		_status.PropertyChanged += (_, _) => ApplyStatus();
		ApplyStatus();

		services.ErrorRaised += message =>
		{
			ErrorBar.Message = message;
			ErrorBar.IsOpen = true;
		};
		services.SettingsChanged += (_, _) =>
		{
			ApplyDevMode();
			ApplyTheme();
			ApplyCompact();
		};
		// Start where the window last was: full placement first (so the remembered normal size is right), then the compact one.
		var saved = services.Current;
		_restoring = true;
		if (saved.Window is { } full)
		{
			WindowPlacements.Restore(AppWindow, full, allowMaximize: !saved.CompactMode);
		}

		ApplyDevMode();
		ApplyTheme();
		ApplyCompact();
		if (saved.CompactMode && saved.CompactWindow is { } small)
		{
			WindowPlacements.Restore(AppWindow, small, allowMaximize: false);
			_compactPlaced = true;
		}

		_restoring = false;
		AppWindow.Changed += (_, args) =>
		{
			if (!_restoring && (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange))
			{
				SchedulePlacementSave();
			}
		};
		AppWindow.Closing += (_, args) =>
		{
			CapturePlacement();
			if (App.Tray is not null && App.Services.Current.CloseToTray && !App.IsExiting)
			{
				args.Cancel = true;
				AppWindow.Hide();
			}
		};

		Closed += (_, _) =>
		{
			_status.Dispose();
			_compact?.Shutdown();
		};
		ContentFrame.Navigated += (_, e) => SyncNav(e.SourcePageType);
		ContentFrame.Navigate(typeof(DashboardPage));
	}

	private bool _syncing;
	private bool _restoring;
	private bool _compactPlaced;
	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _placementTimer;

	/// <summary>Moves/resizes settle for half a second before the placement is written (one write per gesture).</summary>
	private void SchedulePlacementSave()
	{
		if (_placementTimer is null)
		{
			_placementTimer = DispatcherQueue.CreateTimer();
			_placementTimer.Interval = TimeSpan.FromMilliseconds(500);
			_placementTimer.IsRepeating = false;
			_placementTimer.Tick += (_, _) => CapturePlacement();
		}

		_placementTimer.Stop();
		_placementTimer.Start();
	}

	private void CapturePlacement()
	{
		if (_restoring || !AppWindow.IsVisible)
		{
			return;
		}

		var state = (AppWindow.Presenter as OverlappedPresenter)?.State;
		if (state == OverlappedPresenterState.Minimized)
		{
			return;
		}

		var compact = _compactApplied;
		var previous = compact ? App.Services.Current.CompactWindow : App.Services.Current.Window;
		var next = WindowPlacements.Capture(AppWindow, state == OverlappedPresenterState.Maximized, previous);
		if (next is null || next == previous)
		{
			return;
		}

		App.Services.UpdateSettings(s => compact ? s with { CompactWindow = next } : s with { Window = next }, notify: false);
	}

	/// <summary>Opens a page by its nav tag (tray menu).</summary>
	public void NavigateTo(string tag)
	{
		if (PageTypes.TryGetValue(tag, out var type))
		{
			ContentFrame.Navigate(type, null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
		}
	}

	private void TrySetIcon()
	{
		try
		{
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
			if (System.IO.File.Exists(path))
			{
				AppWindow.SetIcon(path);
			}
		}
		catch (Exception ex)
		{
			AppLog.StaticWrite($"window icon: {ex.Message}");
		}
	}
	private Controls.CompactView? _compact;
	private bool _compactApplied;
	private bool _autoCompact;
	private SizeInt32 _normalSize;

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hWnd);

	/// <summary>Real monitor scale from the window DPI (XamlRoot is not available yet during construction).</summary>
	private double Scale
	{
		get
		{
			var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
			return dpi > 0 ? dpi / 96.0 : Content?.XamlRoot?.RasterizationScale ?? 1.0;
		}
	}

	/// <summary>Title-bar logo: the full icon from 32 physical px, the simplified 24 px variant below that.</summary>
	private void UpdateLogo()
	{
		try
		{
			var file = 24 * Scale >= 32 ? "AppIcon.png" : "AppIcon24.png";
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", file);
			if (System.IO.File.Exists(path))
			{
				LogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) { DecodePixelWidth = (int)Math.Round(24 * Scale) };
			}
		}
		catch (Exception ex)
		{
			AppLog.StaticWrite($"title logo: {ex.Message}");
		}
	}

	private void UpdateTitleInset() =>
		InsetColumn.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / Scale));

	/// <summary>Caption buttons follow the app theme (not the system theme) and sit on the Mica surface.</summary>
	private void ApplyCaptionColors()
	{
		var dark = Root.ActualTheme == ElementTheme.Dark;
		var fg = dark ? Colors.White : Colors.Black;
		var hover = dark ? Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x1A, 0, 0, 0);
		var press = dark ? Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x2E, 0, 0, 0);
		var dim = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
		var bar = AppWindow.TitleBar;
		bar.BackgroundColor = Colors.Transparent;
		bar.InactiveBackgroundColor = Colors.Transparent;
		bar.ButtonBackgroundColor = Colors.Transparent;
		bar.ButtonInactiveBackgroundColor = Colors.Transparent;
		bar.ButtonForegroundColor = fg;
		bar.ButtonInactiveForegroundColor = dim;
		bar.ButtonHoverBackgroundColor = hover;
		bar.ButtonHoverForegroundColor = fg;
		bar.ButtonPressedBackgroundColor = press;
		bar.ButtonPressedForegroundColor = fg;
	}

	/// <summary>When the full window is made very narrow it switches to the compact panel by itself (and back once it is wide enough).</summary>
	private void UpdateAutoCompact()
	{
		if (_compactApplied)
		{
			return;
		}

		var widthDip = Root.ActualWidth;
		var want = _autoCompact ? widthDip < 500 : widthDip is > 0 and < 440;
		if (want == _autoCompact)
		{
			return;
		}

		_autoCompact = want;
		_compact ??= CreateCompact();
		Nav.Visibility = want ? Visibility.Collapsed : Visibility.Visible;
		CompactHost.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
	}

	private void CompactButton_Click(object sender, RoutedEventArgs e) =>
		App.Services.UpdateSettings(s => s with { CompactMode = !s.CompactMode });

	/// <summary>Switches the window between the full navigation shell and the extremely compact single-panel view.</summary>
	private void ApplyCompact()
	{
		var compact = App.Services.Current.CompactMode;
		if (compact == _compactApplied)
		{
			return;
		}

		_compactApplied = compact;
		_autoCompact = false;
		if (compact)
		{
			if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } maximized)
			{
				maximized.Restore();
			}

			_normalSize = AppWindow.Size;
			if (_normalSize.Width < 640 * Scale)
			{
				// The window was narrow (auto-compact); return to a sensible full size later.
				_normalSize = new SizeInt32((int)(1100 * Scale), (int)(800 * Scale));
			}
			_compact ??= CreateCompact();
			Nav.Visibility = Visibility.Collapsed;
			CompactHost.Visibility = Visibility.Visible;
			CompactGlyph.Glyph = "\uE740"; // FullScreen = back to the full app
			ToolTipService.SetToolTip(CompactButton, "Full window");
			AutomationPropertiesHelper.SetName(CompactButton, "Full window");
			FitCompact();
		}
		else
		{
			CompactHost.Visibility = Visibility.Collapsed;
			Nav.Visibility = Visibility.Visible;
			CompactGlyph.Glyph = "\uE73F"; // BackToWindow glyph used as "compact"
			ToolTipService.SetToolTip(CompactButton, "Compact mode");
			AutomationPropertiesHelper.SetName(CompactButton, "Compact mode");
			if (_normalSize.Width > 0)
			{
				AppWindow.Resize(_normalSize);
			}
		}
	}

	/// <summary>
	/// Entering compact mode: width becomes the compact width, height may only shrink (never grow) to the Home content.
	/// Nothing else resizes the window afterwards, so navigating (Home included) can never make it taller.
	/// </summary>
	private void FitCompact(bool allowGrow = false)
	{
		if (!_compactApplied || _compact is null)
		{
			return;
		}

		var scale = Scale;
		var currentHeight = AppWindow.ClientSize.Height;
		var heightPx = currentHeight;
		if (_compact.HomeContentHeight > 1)
		{
			var fitPx = (int)Math.Round((TitleBarRoot.Height + _compact.HomeContentHeight + 6) * scale);
			var minPx = (int)Math.Round(MinClientHeightDip * scale);
			var fit = Math.Max(fitPx, minPx);
			if (allowGrow)
			{
				var workPx = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea.Height;
				heightPx = Math.Min(fit, Math.Max(minPx, workPx - (int)Math.Round(80 * scale)));
			}
			else
			{
				heightPx = Math.Min(currentHeight, fit);
			}
		}
		else if (allowGrow)
		{
			heightPx = Math.Max(currentHeight, (int)Math.Round(480 * scale));
		}

		AppWindow.ResizeClient(new SizeInt32((int)Math.Round(Controls.CompactView.PreferredWidth * scale), heightPx));
	}

	private Controls.CompactView CreateCompact()
	{
		var view = new Controls.CompactView();
		view.OpenFullRequested += (_, _) => App.Services.UpdateSettings(s => s with { CompactMode = false });
		CompactHost.Children.Add(view);
		return view;
	}

	// ---- minimum window size (360 DIP wide client area) ----
	private const double MinClientHeightDip = 300;
	private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);
	private SubclassProc? _subclass;

	[DllImport("comctl32.dll")]
	private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint id, nuint data);

	[DllImport("comctl32.dll")]
	private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct Point32
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MinMaxInfo
	{
		public Point32 Reserved;
		public Point32 MaxSize;
		public Point32 MaxPosition;
		public Point32 MinTrack;
		public Point32 MaxTrack;
	}

	private void InstallMinSize()
	{
		_subclass = (hWnd, msg, wParam, lParam, id, data) =>
		{
			var result = DefSubclassProc(hWnd, msg, wParam, lParam);
			if (msg == 0x0024) // WM_GETMINMAXINFO
			{
				var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
				var frameW = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
				var frameH = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
				var scale = Scale;
				info.MinTrack.X = (int)Math.Ceiling(Controls.CompactView.PreferredWidth * scale) + frameW;
				info.MinTrack.Y = (int)Math.Ceiling(MinClientHeightDip * scale) + frameH;
				Marshal.StructureToPtr(info, lParam, false);
			}

			return result;
		};
		SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _subclass, 1, 0);
	}

	private void SyncNav(Type page)
	{
		foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>().Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>()))
		{
			if (item.Tag is string tag && PageTypes.TryGetValue(tag, out var type) && type == page)
			{
				if (!ReferenceEquals(Nav.SelectedItem, item))
				{
					_syncing = true;
					Nav.SelectedItem = item;
					_syncing = false;
				}

				return;
			}
		}
	}

	private void ApplyTheme()
	{
		if (Content is FrameworkElement root)
		{
			root.RequestedTheme = App.Services.Current.Theme switch
			{
				Momentum5.Client.AppTheme.Light => ElementTheme.Light,
				Momentum5.Client.AppTheme.Dark => ElementTheme.Dark,
				_ => ElementTheme.Default,
			};
		}
	}

	private void ApplyDevMode() =>
		AdvancedItem.Visibility = App.Services.Current.DevModeUnlocked ? Visibility.Visible : Visibility.Collapsed;

	private void ApplyStatus()
	{
		StatusText.Text = _status.ChipText;
		UpdateChip();
		ToolTipService.SetToolTip(StatusChip, _status.Detail is { Length: > 0 } d ? $"{_status.Text}\n{d}" : _status.Text);
		AutomationPropertiesHelper.SetName(StatusChip, _status.Text);
		var key = _status.Kind switch
		{
			StatusKind.Good => "SystemFillColorSuccessBrush",
			StatusKind.Warning => "SystemFillColorCautionBrush",
			StatusKind.Error => "SystemFillColorCriticalBrush",
			_ => "SystemFillColorNeutralBrush",
		};
		StatusDot.Fill = (Brush)Application.Current.Resources[key];
		TitleDot.Fill = StatusDot.Fill;
		ToolTipService.SetToolTip(TitleDot, _status.Text);
	}

	/// <summary>Wide pane: dot + device name. Icon-rail pane: just a dot. Details live in the tooltip and on tap.</summary>
	private void UpdateChip()
	{
		var wide = Nav.IsPaneOpen;
		StatusText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
		StatusDot.Width = StatusDot.Height = wide ? 10 : 12;
		StatusChip.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
		// Icon rail: exactly as wide as the rail, so the dot sits on the same centre line as the icons.
		StatusChip.Width = wide ? double.NaN : Nav.CompactPaneLength;
		StatusChip.Margin = wide ? new Thickness(12, 8, 12, 8) : new Thickness(0, 10, 0, 10);
		StatusChip.Padding = wide ? new Thickness(10, 6, 10, 6) : new Thickness(0, 6, 0, 6);
		StatusChip.Background = wide ? (Brush)Application.Current.Resources["PillBrush"] : new SolidColorBrush(Colors.Transparent);
		ChipRow.HorizontalAlignment = wide ? HorizontalAlignment.Left : HorizontalAlignment.Center;
	}

	private void Nav_PaneChanged(NavigationView sender, object args) => UpdateChip();

	private void Nav_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
	{
		UpdateChip();
		// Minimal mode floats the hamburger button over the top-left of the content: reserve its row so page titles are never covered.
		ContentFrame.Margin = new Thickness(0, args.DisplayMode == NavigationViewDisplayMode.Minimal ? 44 : 0, 0, 0);
	}

	private void StatusChip_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
	{
		var text = _status.Detail is { Length: > 0 } d ? $"{_status.Text}\n{d}" : _status.Text;
		new Flyout
		{
			Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, IsTextSelectionEnabled = true },
		}.ShowAt(StatusChip);
	}

	private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		if (_syncing || args.SelectedItem is not NavigationViewItem { Tag: string tag } item)
		{
			return;
		}

		if (PageTypes.TryGetValue(tag, out var type))
		{
			ContentFrame.Navigate(type, null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
		}
		else
		{
			ContentFrame.Navigate(typeof(StubPage), item.Content?.ToString() ?? tag);
		}
	}
}

internal static class AutomationPropertiesHelper
{
	public static void SetName(UIElement element, string name) =>
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
