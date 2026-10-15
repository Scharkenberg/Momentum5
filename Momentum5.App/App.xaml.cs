using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Momentum5.App.Hotkeys;
using Momentum5.App.Notifications;
using Momentum5.App.Services;
using Momentum5.App.Tray;
using Momentum5.App.Zones;
using WinRT.Interop;

namespace Momentum5.App
{
	/// <summary>Application entry: single instance, composition root, shell window and the tray/hotkey/toast/zone services.</summary>
	public partial class App : Application
	{
		private Window? _window;
		private SingleInstance? _instance;

		public App()
		{
			InitializeComponent();
		}

		/// <summary>Main shell window (file pickers need its handle).</summary>
		public static Window? MainShell { get; private set; }

		/// <summary>Composition root shared by all pages; created before the first window.</summary>
		public static AppServices Services { get; private set; } = null!;

		/// <summary>Lazy service lookup for the Advanced pages.</summary>
		public static T Service<T>() where T : class => Shell.AdvancedServices.Get<T>();

		public static TrayService? Tray { get; private set; }

		private static Shell.FlyoutWindow? _flyout;

		/// <summary>Shows or hides the tray quick panel (created on first use).</summary>
		public static void ToggleFlyout()
		{
			_flyout ??= new Shell.FlyoutWindow();
			_flyout.Toggle();
		}

		public static HotkeyService? Hotkeys { get; private set; }

		public static ToastService? Toasts { get; private set; }

		public static ZoneService? Zones { get; private set; }

		/// <summary>True once Exit was chosen, so close-to-tray lets the window close.</summary>
		public static bool IsExiting { get; private set; }

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(nint hwnd);

		protected override async void OnLaunched(LaunchActivatedEventArgs args)
		{
			_instance = SingleInstance.TryAcquire();
			if (_instance is null)
			{
				Exit();
				return;
			}

			var simulate = Environment.GetEnvironmentVariable("M5_SIMULATE") == "1"
				|| Environment.GetCommandLineArgs().Contains("--simulate");
			Services = new AppServices(
				new DispatcherQueueUiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()),
				simulate);

			_window = new Shell.ShellWindow();
			MainShell = _window;
			_window.Closed += OnWindowClosed;
			_window.Activate();
			_instance.Listen(() => Services.Ui.Post(ShowWindow));

			try
			{
				var hwnd = WindowNative.GetWindowHandle(_window);
				Toasts = new ToastService(Services, ShowWindow);
				Hotkeys = new HotkeyService(hwnd, Services);
				Zones = new ZoneService(Services, Toasts);
				Tray = new TrayService(Services, ShowWindow, ExitApp, ToggleFlyout, OpenSettings);
			}
			catch (Exception ex)
			{
				Services.ReportError($"Tray, hotkeys or toasts failed to start: {ex.Message}");
			}

			try
			{
				await Services.StartAsync();
			}
			catch (Exception ex)
			{
				Services.ReportError($"Startup failed: {ex.Message}");
			}
		}

		public static void ShowWindow()
		{
			if (MainShell is not { } window)
			{
				return;
			}

			window.AppWindow.Show();
			window.Activate();
			_ = SetForegroundWindow(WindowNative.GetWindowHandle(window));
		}

		/// <summary>Tray "Settings": leaves compact mode (it has no Settings page), shows the window and opens the page.</summary>
		public static void OpenSettings()
		{
			if (Services.Current.CompactMode)
			{
				Services.UpdateSettings(s => s with { CompactMode = false });
			}

			ShowWindow();
			(MainShell as Shell.ShellWindow)?.NavigateTo("Settings");
		}

		public static void ExitApp()
		{
			IsExiting = true;
			MainShell?.Close();
		}

		private async void OnWindowClosed(object sender, WindowEventArgs args)
		{
			_flyout?.Shutdown();
			Tray?.Dispose();
			Hotkeys?.Dispose();
			Zones?.Dispose();
			Toasts?.Dispose();
			await Services.DisposeAsync();
			_instance?.Dispose();
			Exit();
		}
	}
}
