namespace Momentum5.Core.Settings;

public static partial class Keys
{
	/// <summary>
	/// MMI (touch/button) mapping of slot <paramref name="index"/> — the RAW reply bytes of GET 1601.
	/// The payload format is not decoded (ERRATA R-03/R-04), so nothing is interpreted client-side.
	/// </summary>
	public static SettingKey<byte[]> MmiConfig(int index) => new(nameof(MmiConfig), index);

	/// <summary>
	/// RAW reply of GET 1605. The official M4 device model documents it as a BOOL "is default config"
	/// (NOT a full dump); M5 semantics are unverified, so the value stays raw bytes.
	/// </summary>
	public static readonly SettingKey<byte[]> MmiIsDefault = new(nameof(MmiIsDefault));
}
