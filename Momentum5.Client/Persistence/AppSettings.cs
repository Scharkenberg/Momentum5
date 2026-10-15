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

public sealed record NotificationFlags(bool Connected = false, bool Disconnected = false, bool BatteryLow = false, bool FirmwareChanged = false);

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

	public NotificationFlags Notifications { get; init; } = new();

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
