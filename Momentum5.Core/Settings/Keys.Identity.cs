using Momentum5.Core.Domain;

namespace Momentum5.Core.Settings;

/// <summary>All setting keys. UI and services talk in keys; Protocol maps them to commands.</summary>
public static partial class Keys
{
	public static readonly SettingKey<string> Model = new(nameof(Model));
	public static readonly SettingKey<FirmwareVersion> Firmware = new(nameof(Firmware));
	public static readonly SettingKey<byte[]> HwRevision = new(nameof(HwRevision));
	public static readonly SettingKey<byte[]> FirmwareVersions = new(nameof(FirmwareVersions));
	public static readonly SettingKey<string> Serial = new(nameof(Serial));
	public static readonly SettingKey<byte> PrimarySide = new(nameof(PrimarySide));
	public static readonly SettingKey<int> BatteryPercent = new(nameof(BatteryPercent));
	public static readonly SettingKey<ChargingState> ChargingState = new(nameof(ChargingState));
	public static readonly SettingKey<AutoOffTimer> AutoOff = new(nameof(AutoOff));
	public static readonly SettingKey<bool> BatteryProtection = new(nameof(BatteryProtection));
	public static readonly SettingKey<string> LocalName = new(nameof(LocalName));
}
