using Momentum5.Core.Domain;

namespace Momentum5.Core.Settings;

public static partial class Keys
{
	public static readonly SettingKey<EqConfig> EqConfig = new(nameof(EqConfig));
	public static readonly SettingKey<IReadOnlyList<double>> EqAllGains = new(nameof(EqAllGains));
	public static readonly SettingKey<bool> BassBoost = new(nameof(BassBoost));
	public static readonly SettingKey<SoundMode> SoundMode = new(nameof(SoundMode));
	public static readonly SettingKey<byte> PersonalizationState = new(nameof(PersonalizationState));

	/// <summary>Gain in dB of band <paramref name="index"/> (0.1 dB resolution).</summary>
	public static SettingKey<double> EqBand(int index) => new(nameof(EqBand), index);

	/// <summary>Centre frequency in Hz of band <paramref name="index"/>.</summary>
	public static SettingKey<int> EqBandFreq(int index) => new(nameof(EqBandFreq), index);

	/// <summary>
	/// Raw Q byte of band <paramref name="index"/> (GET 100D). Payload semantics are unverified
	/// (ref/01 status U) — the raw byte is exposed without interpretation; no SET exists.
	/// </summary>
	public static SettingKey<int> EqBandQ(int index) => new(nameof(EqBandQ), index);

	/// <summary>
	/// Raw filter-type byte of band <paramref name="index"/> (GET 100F). Payload semantics are
	/// unverified (ref/01 status U) — the raw byte is exposed without interpretation; no SET exists.
	/// </summary>
	public static SettingKey<int> EqBandFilterType(int index) => new(nameof(EqBandFilterType), index);
}
