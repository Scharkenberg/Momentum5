using System;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.Tray;

/// <summary>
/// Tray icon (H.NotifyIcon.WinUI). Left click opens the quick panel or the window. The context menu is a native popup menu, so every item
/// is wired through <c>Command</c> (the popup mode never raises <c>Click</c>), and its text/checks are refreshed from state changes rather than on opening.
/// Layout: device header, noise toggles, window actions, connection, exit.
/// </summary>
public sealed class TrayService : IDisposable
{
	private readonly AppServices _services;
	private readonly UiCoalescer _refresh;
	private readonly TaskbarIcon _icon;
	private readonly MenuFlyoutItem _header;
	private readonly MenuFlyoutItem _connect;
	private readonly ToggleMenuFlyoutItem _anc;
	private readonly ToggleMenuFlyoutItem _transparency;
	private readonly ToggleMenuFlyoutItem _immersive;
	private readonly ToggleMenuFlyoutItem _compactMode;

	public TrayService(AppServices services, Action open, Action exit, Action toggleFlyout, Action openSettings)
	{
		_services = services;
		_refresh = new UiCoalescer(services.Ui, Refresh);
		_header = new MenuFlyoutItem { Text = "Momentum5", IsEnabled = false };
		_anc = Toggle("Noise cancelling", QuickAction.Anc);
		_transparency = Toggle("Transparency", QuickAction.Transparency);
		_immersive = Toggle("Immersive audio", QuickAction.Immersive);
		_compactMode = new ToggleMenuFlyoutItem
		{
			Text = "Compact mode",
			Command = new RelayCommand(() =>
			{
				_services.UpdateSettings(s => s with { CompactMode = !s.CompactMode });
				open();
			}),
		};
		_connect = new MenuFlyoutItem { Text = "Connect", Command = new RelayCommand(ToggleConnection) };

		var menu = new MenuFlyout();
		menu.Items.Add(_header);
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(_anc);
		menu.Items.Add(_transparency);
		menu.Items.Add(_immersive);
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(new MenuFlyoutItem { Text = "Open Momentum5", Command = new RelayCommand(open) });
		menu.Items.Add(new MenuFlyoutItem { Text = "Quick panel", Command = new RelayCommand(toggleFlyout) });
		menu.Items.Add(_compactMode);
		menu.Items.Add(new MenuFlyoutItem { Text = "Settings", Command = new RelayCommand(openSettings) });
		menu.Items.Add(new MenuFlyoutSeparator());
		menu.Items.Add(_connect);
		menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(exit) });

		_icon = new TaskbarIcon
		{
			ToolTipText = "Momentum5",
			ContextFlyout = menu,
			ContextMenuMode = ContextMenuMode.PopupMenu,
			NoLeftClickDelay = true,
			LeftClickCommand = new RelayCommand(() =>
			{
				if (_services.Current.TrayFlyout)
				{
					toggleFlyout();
				}
				else
				{
					open();
				}
			}),
		};
		ApplyIcon();
		_icon.ForceCreate(enablesEfficiencyMode: false);

		services.Store.Changed += OnChanged;
		services.Supervisor.StateChanged += OnSession;
		services.SettingsChanged += OnSettings;
		Refresh();
	}

	/// <summary>The tray icon is the same app icon as the window and the compact title bar.</summary>
	private void ApplyIcon()
	{
		try
		{
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
			if (System.IO.File.Exists(path))
			{
				_icon.Icon = new System.Drawing.Icon(path);
				return;
			}
		}
		catch (Exception ex)
		{
			Shell.AppLog.StaticWrite($"tray icon: {ex.Message}");
		}

		_icon.IconSource = new GeneratedIconSource { Text = "M5", FontSize = 36 };
	}

	private ToggleMenuFlyoutItem Toggle(string text, QuickAction action) =>
		new() { Text = text, Command = new RelayCommand(() => _ = QuickActions.ToggleAsync(_services, action)) };

	private void ToggleConnection()
	{
		if (_services.Supervisor.State.Phase is SessionPhase.Ready or SessionPhase.ReadOnly or SessionPhase.Connecting)
		{
			_services.Disconnect();
		}
		else
		{
			_services.Connect();
		}
	}

	private void OnChanged(object? sender, HeadsetSnapshot snapshot) => _refresh.Request();

	private void OnSession(object? sender, SessionState state) => _refresh.Request();

	private void OnSettings(object? sender, EventArgs e) => Refresh();

	private void Refresh()
	{
		var snapshot = _services.Store.Current;
		var phase = _services.Supervisor.State.Phase;
		var connected = phase is SessionPhase.Ready or SessionPhase.ReadOnly;
		_connect.Text = connected || phase == SessionPhase.Connecting ? "Disconnect" : "Connect";
		_compactMode.IsChecked = _services.Current.CompactMode;

		var writable = snapshot.Session is { Trust: not TrustTier.Unknown };
		Sync(_anc, QuickAction.Anc, writable);
		Sync(_transparency, QuickAction.Transparency, writable);
		Sync(_immersive, QuickAction.Immersive, writable);

		string line;
		if (connected)
		{
			var name = snapshot.TryGet(Keys.LocalName, out var local) && local.Length > 0 ? local : snapshot.Session?.Model ?? "Headset";
			line = name;
			if (snapshot.TryGet(Keys.BatteryPercent, out var p))
			{
				var charging = snapshot.TryGet(Keys.ChargingState, out var cs) && cs == ChargingState.Charging;
				line = $"{name} · {p} %{(charging ? " (charging)" : string.Empty)}";
			}
		}
		else
		{
			line = phase switch
			{
				SessionPhase.Connecting => "Connecting…",
				SessionPhase.Backoff => "Reconnecting…",
				SessionPhase.RadioOff => "Bluetooth is off",
				SessionPhase.Unsupported => "Unsupported device",
				_ => "Not connected",
			};
		}

		_header.Text = line;
		_icon.ToolTipText = $"Momentum5 · {line}";
	}

	private void Sync(ToggleMenuFlyoutItem item, QuickAction action, bool writable)
	{
		item.IsChecked = QuickActions.IsOn(_services, action);
		item.IsEnabled = writable && QuickActions.IsAvailable(_services, action);
	}

	public void Dispose()
	{
		_services.Store.Changed -= OnChanged;
		_services.Supervisor.StateChanged -= OnSession;
		_services.SettingsChanged -= OnSettings;
		_icon.Dispose();
	}
}
