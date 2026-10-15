using Momentum5.Core.Domain;

namespace Momentum5.Core.Settings;

public static partial class Keys
{
	public static readonly SettingKey<bool> ImmersiveAudio = new(nameof(ImmersiveAudio));
	public static readonly SettingKey<bool> HeadTracking = new(nameof(HeadTracking));
	public static readonly SettingKey<byte[]> ImmersiveQuery = new(nameof(ImmersiveQuery));
	public static readonly SettingKey<bool> WearDetection = new(nameof(WearDetection));
	public static readonly SettingKey<byte> PhysicalState = new(nameof(PhysicalState));
	public static readonly SettingKey<bool> AutoAnswer = new(nameof(AutoAnswer));
	public static readonly SettingKey<bool> SmartPause = new(nameof(SmartPause));
	public static readonly SettingKey<bool> ComfortCall = new(nameof(ComfortCall));

	/// <summary>True = touch controls on. Raw command 1606 is "locked" (1 = locked), so the binding inverts it.</summary>
	public static readonly SettingKey<bool> TouchControlsEnabled = new(nameof(TouchControlsEnabled));

	public static readonly SettingKey<PromptMode> PromptMode = new(nameof(PromptMode));

	/// <summary>Phone-call transparency level 0 (off) .. 4. Command 0805 (called sidetone in older notes).</summary>
	public static readonly SettingKey<byte> PhoneCallTransparency = new(nameof(PhoneCallTransparency));

	/// <summary>Voice prompt volume, dB, about -20 .. +6.</summary>
	public static readonly SettingKey<sbyte> PromptVolume = new(nameof(PromptVolume));

	public static readonly SettingKey<byte[]> PromptLanguage = new(nameof(PromptLanguage));
	public static readonly SettingKey<BtCompatibility> BtCompatibility = new(nameof(BtCompatibility));
	public static readonly SettingKey<bool> LowLatency = new(nameof(LowLatency));
	public static readonly SettingKey<byte> Codec = new(nameof(Codec));
	public static readonly SettingKey<uint> SampleRate = new(nameof(SampleRate));
}
