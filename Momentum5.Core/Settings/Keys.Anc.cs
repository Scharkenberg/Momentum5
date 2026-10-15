namespace Momentum5.Core.Settings;

public static partial class Keys
{
	public static readonly SettingKey<bool> AncEnabled = new(nameof(AncEnabled));

	/// <summary>0 = full ANC, 50 = neutral, 100 = full transparency (slider).</summary>
	public static readonly SettingKey<int> AncLevel = new(nameof(AncLevel));

	/// <summary>0 off, 1 Max, 2 Auto (write only). Reads back 0/1: Auto vs Max is app-side state.</summary>
	public static readonly SettingKey<byte> AncAntiWind = new(nameof(AncAntiWind));

	public static readonly SettingKey<bool> AncAdaptive = new(nameof(AncAdaptive));

	/// <summary>Read-only: the M5 has no Comfort mode; never written.</summary>
	public static readonly SettingKey<bool> AncComfort = new(nameof(AncComfort));

	public static readonly SettingKey<bool> TransparencyStatus = new(nameof(TransparencyStatus));
	public static readonly SettingKey<int> TransparencyLevel = new(nameof(TransparencyLevel));
	public static readonly SettingKey<bool> TransparencyAutoPause = new(nameof(TransparencyAutoPause));
}
