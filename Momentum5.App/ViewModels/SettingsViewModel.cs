using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum5.App.Hotkeys;
using Momentum5.App.Services;
using Momentum5.Client;

namespace Momentum5.App.ViewModels;

public sealed record ZoneRow(LocationZone Zone)
{
	public string Title => Zone.Name;

	public string Detail => string.Create(CultureInfo.InvariantCulture, $"{Zone.Latitude:0.#####}, {Zone.Longitude:0.#####} · {Zone.RadiusMeters:0} m · {(Zone.Mode ?? "no mode")} · {(Zone.Preset ?? "no preset")}");
}

public sealed record HotkeyRow(QuickAction Action, string Label);

/// <summary>U-16 (plus the hotkey and zone sections of U-19 / U-21): every control writes straight through <see cref="AppServices.UpdateSettings"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
	public const string DevModePhrase = "MOMENTUM5";
	private readonly AppServices _services;
	private bool _loading;

	public SettingsViewModel(AppServices services)
	{
		_services = services;
		UnlockCommand = new RelayCommand(Unlock, () => string.Equals(UnlockText.Trim(), DevModePhrase, StringComparison.Ordinal));
		LockCommand = new RelayCommand(() => Update(s => s with { DevModeUnlocked = false }));
		ApplyHotkeysCommand = new RelayCommand(ApplyHotkeys);
		ResetHotkeysCommand = new RelayCommand(() =>
		{
			Update(s => s with { Hotkeys = new Dictionary<string, string>() });
			LoadHotkeys();
		});
		AddZoneCommand = new RelayCommand(AddZone);
		RemoveZoneCommand = new RelayCommand<ZoneRow>(RemoveZone);
		UseCurrentLocationCommand = new AsyncRelayCommand(UseCurrentLocationAsync);
		_services.SettingsChanged += OnChanged;
		if (App.Hotkeys is { } h)
		{
			h.StatusChanged += OnHotkeyStatus;
		}

		Load();
	}

	public IRelayCommand UnlockCommand { get; }

	public IRelayCommand LockCommand { get; }

	public IRelayCommand ApplyHotkeysCommand { get; }

	public IRelayCommand ResetHotkeysCommand { get; }

	public IRelayCommand AddZoneCommand { get; }

	public IRelayCommand<ZoneRow> RemoveZoneCommand { get; }

	public IAsyncRelayCommand UseCurrentLocationCommand { get; }

	public ObservableCollection<ZoneRow> Zones { get; } = [];

	public ObservableCollection<string> PresetNames { get; } = [];

	public IReadOnlyList<HotkeyRow> HotkeyRows { get; } =
	[
		new(QuickAction.Anc, "Noise cancelling"),
		new(QuickAction.Transparency, "Transparency"),
		new(QuickAction.Immersive, "Immersive audio"),
	];

	public string[] HotkeyGestures { get; } = new string[3];

	public string AncGesture
	{
		get => HotkeyGestures[0];
		set => SetGesture(0, value, nameof(AncGesture));
	}

	public string TransparencyGesture
	{
		get => HotkeyGestures[1];
		set => SetGesture(1, value, nameof(TransparencyGesture));
	}

	public string ImmersiveGesture
	{
		get => HotkeyGestures[2];
		set => SetGesture(2, value, nameof(ImmersiveGesture));
	}

	public string HotkeyStatusText => string.Join("\n", HotkeyRows.Select(r =>
		$"{r.Label}: {(App.Hotkeys?.Status.TryGetValue(r.Action, out var s) == true ? s : "not active")}"));

	public string AboutText => $"Momentum5 · {typeof(SettingsViewModel).Assembly.GetName().Version} · {(_services.Simulated ? "simulator" : "hardware")} · .NET {Environment.Version}";

	public string LogFolder => _services.FrameLogDirectory;

	[ObservableProperty]
	public partial bool AutoConnect { get; set; }

	[ObservableProperty]
	public partial int ThemeIndex { get; set; }

	[ObservableProperty]
	public partial int LogLevelIndex { get; set; }

	[ObservableProperty]
	public partial bool Anonymize { get; set; }

	[ObservableProperty]
	public partial bool CloseToTray { get; set; }

	[ObservableProperty]
	public partial bool CompactMode { get; set; }

	[ObservableProperty]
	public partial bool TrayFlyout { get; set; }

	[ObservableProperty]
	public partial bool NotifyConnected { get; set; }

	[ObservableProperty]
	public partial bool NotifyDisconnected { get; set; }

	[ObservableProperty]
	public partial bool NotifyBatteryLow { get; set; }

	[ObservableProperty]
	public partial bool NotifyReadOnly { get; set; }

	[ObservableProperty]
	public partial bool NotifyCharging { get; set; }

	[ObservableProperty]
	public partial bool DevModeUnlocked { get; set; }

	[ObservableProperty]
	public partial string UnlockText { get; set; } = string.Empty;

	[ObservableProperty]
	public partial bool ZonesEnabled { get; set; }

	[ObservableProperty]
	public partial string NewZoneName { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string NewZoneLatitude { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string NewZoneLongitude { get; set; } = string.Empty;

	[ObservableProperty]
	public partial double NewZoneRadius { get; set; } = 150;

	/// <summary>0 none, 1 Anc, 2 Transparency, 3 Off.</summary>
	[ObservableProperty]
	public partial int NewZoneModeIndex { get; set; }

	[ObservableProperty]
	public partial string? NewZonePreset { get; set; }

	[ObservableProperty]
	public partial string ZoneMessage { get; set; } = string.Empty;

	partial void OnAutoConnectChanged(bool value) => Write(() => _services.SetAutoConnect(value));

	partial void OnThemeIndexChanged(int value) => Update(s => s with { Theme = (AppTheme)Math.Clamp(value, 0, 2) });

	partial void OnLogLevelIndexChanged(int value) => Update(s => s with { LogLevel = (AppLogLevel)Math.Clamp(value, 0, 3) });

	partial void OnAnonymizeChanged(bool value) => Update(s => s with { AnonymizeDiagnostics = value });

	partial void OnCloseToTrayChanged(bool value) => Update(s => s with { CloseToTray = value });

	partial void OnCompactModeChanged(bool value) => Update(s => s with { CompactMode = value });

	partial void OnTrayFlyoutChanged(bool value) => Update(s => s with { TrayFlyout = value });

	partial void OnNotifyConnectedChanged(bool value) => Update(s => s with { Notifications = s.Notifications with { Connected = value } });

	partial void OnNotifyDisconnectedChanged(bool value) => Update(s => s with { Notifications = s.Notifications with { Disconnected = value } });

	partial void OnNotifyBatteryLowChanged(bool value) => Update(s => s with { Notifications = s.Notifications with { BatteryLow = value } });

	partial void OnNotifyReadOnlyChanged(bool value) => Update(s => s with { Notifications = s.Notifications with { FirmwareChanged = value } });

	partial void OnNotifyChargingChanged(bool value) => Update(s => s with { Notifications = s.Notifications with { Charging = value } });

	partial void OnZonesEnabledChanged(bool value) => Update(s => s with { ZonesEnabled = value });

	partial void OnUnlockTextChanged(string value) => UnlockCommand.NotifyCanExecuteChanged();

	private void Write(Action action)
	{
		if (!_loading)
		{
			action();
		}
	}

	private void Update(Func<AppSettings, AppSettings> change) => Write(() => _services.UpdateSettings(change));

	private void Unlock()
	{
		if (string.Equals(UnlockText.Trim(), DevModePhrase, StringComparison.Ordinal))
		{
			Update(s => s with { DevModeUnlocked = true });
			UnlockText = string.Empty;
		}
	}

	private void SetGesture(int index, string value, string property)
	{
		if (HotkeyGestures[index] != value)
		{
			HotkeyGestures[index] = value ?? string.Empty;
			OnPropertyChanged(property);
		}
	}

	private void LoadHotkeys()
	{
		for (var i = 0; i < HotkeyRows.Count; i++)
		{
			HotkeyGestures[i] = HotkeyService.GestureFor(_services, HotkeyRows[i].Action);
		}

		OnPropertyChanged(nameof(AncGesture));
		OnPropertyChanged(nameof(TransparencyGesture));
		OnPropertyChanged(nameof(ImmersiveGesture));
		OnPropertyChanged(nameof(HotkeyStatusText));
	}

	private void ApplyHotkeys()
	{
		var dict = new Dictionary<string, string>();
		for (var i = 0; i < HotkeyRows.Count; i++)
		{
			dict[QuickActions.Id(HotkeyRows[i].Action)] = HotkeyGestures[i].Trim();
		}

		_services.UpdateSettings(s => s with { Hotkeys = dict });
	}

	private void OnHotkeyStatus(object? sender, EventArgs e) => _services.Ui.Post(() => OnPropertyChanged(nameof(HotkeyStatusText)));

	private void OnChanged(object? sender, EventArgs e) => Load();

	private void Load()
	{
		_loading = true;
		try
		{
			var s = _services.Current;
			AutoConnect = s.AutoConnect;
			ThemeIndex = (int)s.Theme;
			LogLevelIndex = (int)s.LogLevel;
			Anonymize = s.AnonymizeDiagnostics;
			CloseToTray = s.CloseToTray;
			CompactMode = s.CompactMode;
			TrayFlyout = s.TrayFlyout;
			NotifyConnected = s.Notifications.Connected;
			NotifyDisconnected = s.Notifications.Disconnected;
			NotifyBatteryLow = s.Notifications.BatteryLow;
			NotifyReadOnly = s.Notifications.FirmwareChanged;
			NotifyCharging = s.Notifications.Charging;
			DevModeUnlocked = s.DevModeUnlocked;
			ZonesEnabled = s.ZonesEnabled;
			Zones.Clear();
			foreach (var z in s.Zones)
			{
				Zones.Add(new ZoneRow(z));
			}

			if (PresetNames.Count == 0)
			{
				foreach (var p in PresetsViewModel.BuiltIns)
				{
					PresetNames.Add(p.Name);
				}

				foreach (var p in new PresetStore().Load())
				{
					if (!PresetNames.Contains(p.Name))
					{
						PresetNames.Add(p.Name);
					}
				}
			}

			LoadHotkeys();
		}
		finally
		{
			_loading = false;
		}
	}

	private void AddZone()
	{
		if (string.IsNullOrWhiteSpace(NewZoneName)
			|| !double.TryParse(NewZoneLatitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || lat is < -90 or > 90
			|| !double.TryParse(NewZoneLongitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) || lon is < -180 or > 180
			|| NewZoneRadius is < 20 or > 50_000)
		{
			ZoneMessage = "Enter a name, latitude (-90..90), longitude (-180..180) with a dot as decimal separator, and a radius of 20 to 50000 m.";
			return;
		}

		var mode = NewZoneModeIndex switch { 1 => "Anc", 2 => "Transparency", 3 => "Off", _ => null };
		var zone = new LocationZone(NewZoneName.Trim(), lat, lon, NewZoneRadius, string.IsNullOrEmpty(NewZonePreset) ? null : NewZonePreset, mode);
		_services.UpdateSettings(s => s with { Zones = [.. s.Zones.Where(z => !string.Equals(z.Name, zone.Name, StringComparison.OrdinalIgnoreCase)), zone] });
		ZoneMessage = $"Saved zone '{zone.Name}'.";
		NewZoneName = string.Empty;
	}

	private void RemoveZone(ZoneRow? row)
	{
		if (row is not null)
		{
			_services.UpdateSettings(s => s with { Zones = [.. s.Zones.Where(z => z != row.Zone)] });
		}
	}

	private async Task UseCurrentLocationAsync()
	{
		if (App.Zones is null)
		{
			ZoneMessage = "Location zones are not available.";
			return;
		}

		if (await App.Zones.CurrentPositionAsync() is { } p)
		{
			NewZoneLatitude = p.Latitude.ToString("0.#####", CultureInfo.InvariantCulture);
			NewZoneLongitude = p.Longitude.ToString("0.#####", CultureInfo.InvariantCulture);
			ZoneMessage = "Filled in your current position.";
		}
		else
		{
			ZoneMessage = "Windows did not provide a location (check Settings, Privacy, Location, and allow desktop apps).";
		}
	}

	public void Dispose()
	{
		_services.SettingsChanged -= OnChanged;
		if (App.Hotkeys is { } h)
		{
			h.StatusChanged -= OnHotkeyStatus;
		}
	}
}
