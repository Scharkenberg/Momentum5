using System.Text.Json.Serialization;
using Momentum5.Core.Domain;

namespace Momentum5.Client;

public enum AppTheme
{
	System,
	Light,
	Dark,
}

public enum AppLogLevel
{
	Error,
	Warning,
	Information,
	Debug,
}

public sealed record NotificationFlags(bool Connected = false, bool Disconnected = false, bool BatteryLow = false, bool FirmwareChanged = false, bool Charging = false);

/// <summary>Opt-in location zone (U-21). <see cref="Mode"/> is "Anc", "Transparency", "Off" or null; <see cref="Preset"/> a preset name or null.</summary>
public sealed record LocationZone(string Name, double Latitude, double Longitude, double RadiusMeters, string? Preset = null, string? Mode = null);

/// <summary>
/// Saved window geometry. <see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/> are DIPs relative to the work-area origin of the saved monitor
/// (so a different DPI still lands in the same place); the Abs* values are the physical-pixel rectangle used to find the nearest monitor when the saved one is gone.
/// <see cref="MonitorId"/> is the display's device-interface name (carries the EDID-derived PnP id), <see cref="MonitorPnp"/> only that PnP id part.
/// </summary>
public sealed record WindowPlacement(
	double X,
	double Y,
	double Width,
	double Height,
	bool Maximized,
	string? MonitorId,
	string? MonitorPnp,
	int AbsLeft,
	int AbsTop,
	int AbsWidth,
	int AbsHeight);

public sealed record AppSettings
{
	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; init; } = CurrentSchemaVersion;

	public ulong? LastDeviceAddress { get; init; }

	public bool AutoConnect { get; init; } = true;

	public bool EnableGattGaia { get; init; }

	public AppTheme Theme { get; init; } = AppTheme.System;

	public AppLogLevel LogLevel { get; init; } = AppLogLevel.Information;

	public bool DevModeUnlocked { get; init; }

	public bool AnonymizeDiagnostics { get; init; } = true;

	public bool CloseToTray { get; init; }

	/// <summary>The main window shows the extremely compact single-panel view instead of the full navigation shell.</summary>
	public bool CompactMode { get; init; }

	/// <summary>Left-clicking the tray icon opens the compact flyout (closes when focus moves elsewhere) instead of the main window.</summary>
	public bool TrayFlyout { get; init; } = true;

	/// <summary>The M5 reads anti-wind Auto back as Max, so which of the two was chosen is remembered here.</summary>
	public bool AntiWindAuto { get; init; }

	/// <summary>Last placement of the full window (kept while the compact view is active).</summary>
	public WindowPlacement? Window { get; init; }

	/// <summary>Last placement of the compact window.</summary>
	public WindowPlacement? CompactWindow { get; init; }

	public NotificationFlags Notifications { get; init; } = new();

	/// <summary>Location zones are off by default and inert without the Windows location permission.</summary>
	public bool ZonesEnabled { get; init; }

	public IReadOnlyList<LocationZone> Zones { get; init; } = [];

	/// <summary>Action id → gesture, e.g. "toggleAnc" → "Ctrl+Alt+N".</summary>
	public IReadOnlyDictionary<string, string> Hotkeys { get; init; } = new Dictionary<string, string>();
}

public sealed record EqPreset(string Name, double[] Gains, bool Bass, SoundMode Mode);

internal sealed record PresetFile(int SchemaVersion, List<EqPreset> Presets);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PresetFile))]
internal sealed partial class ClientJsonContext : JsonSerializerContext
{
}
