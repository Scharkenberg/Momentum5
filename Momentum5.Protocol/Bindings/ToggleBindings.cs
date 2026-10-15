using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

public static class ToggleBindings
{
	public static IReadOnlyList<ISettingBinding> All { get; } = Build();

	private static List<ISettingBinding> Build() =>
	[
		// Spatial
		Bind.Create(Keys.ImmersiveAudio, SettingGroup.Spatial, 0x3605, 0x3604, Codecs.Bool, push: 0x3685, samples: [false, true]),
		Bind.Create(Keys.HeadTracking, SettingGroup.Spatial, 0x3603, 0x3602, Codecs.Bool, push: 0x3683, samples: [false, true]),
		Bind.Create(Keys.ImmersiveQuery, SettingGroup.Spatial, 0x3601, null, Codecs.RawBytes, samples: [Convert.FromHexString("010F")]),

		// Device
		Bind.Create(Keys.WearDetection, SettingGroup.Device, 0x0401, 0x0400, Codecs.Bool, samples: [true, false]),
		Bind.Create(Keys.PhysicalState, SettingGroup.Device, 0x0402, null, Codecs.U8, push: 0x0482, samples: [(byte)0]),
		Bind.Create(Keys.SmartPause, SettingGroup.Device, 0x080D, 0x080C, Codecs.Bool, samples: [false, true]),

		// [CAP2] raw 1606 = "locked" (1 = locked, Controls OFF); the key is "controls enabled", so the wire value is inverted.
		Bind.Create(Keys.TouchControlsEnabled, SettingGroup.Device, 0x1607, 0x1606, Codecs.InvertedBool, push: 0x1687, samples: [true, false]),

		// Calls
		Bind.Create(Keys.AutoAnswer, SettingGroup.Calls, 0x080B, 0x080A, Codecs.Bool, samples: [false, true]),
		Bind.Create(Keys.ComfortCall, SettingGroup.Calls, 0x0815, 0x0814, Codecs.Bool, samples: [false, true]),

		// [CAP2] 0805 = phone-call transparency 0..4 (ack 0905, push 0886); GET 0806 follows the M4 table.
		Bind.Create(
			Keys.PhoneCallTransparency, SettingGroup.Calls, 0x0806, 0x0805, Codecs.U8Range(0, 4), push: 0x0886,
			samples: [(byte)0, (byte)2, (byte)4]),

		// Sound
		Bind.Create(
			Keys.PromptMode, SettingGroup.Sound, 0x0802, 0x0801, Codecs.ByteEnum<PromptMode>(), push: 0x0882,
			samples: [PromptMode.Voice, PromptMode.Off, PromptMode.TonesOnly]),
		Bind.Create(Keys.PromptVolume, SettingGroup.Sound, 0x081D, 0x081C, Codecs.I8, push: 0x089D, samples: [(sbyte)-7, (sbyte)-20, (sbyte)6]),
		Bind.Create(Keys.PromptLanguage, SettingGroup.Sound, 0x0807, null, Codecs.RawBytes, samples: [Convert.FromHexString("00")]),
		Bind.Create(
			Keys.BtCompatibility, SettingGroup.Sound, 0x0406, 0x0405, Codecs.ByteEnum<BtCompatibility>(),
			samples: [BtCompatibility.BetterAudio, BtCompatibility.Compatibility]),
		Bind.Create(
			Keys.LowLatency, SettingGroup.Sound, 0x0818, 0x0817, Codecs.Bool, push: 0x0898, safety: SafetyClass.Gated,
			samples: [false, true]),
		Bind.Create(Keys.Codec, SettingGroup.Sound, 0x0800, null, Codecs.U8, push: 0x0880, samples: [(byte)0]),
		Bind.Create(Keys.SampleRate, SettingGroup.Sound, null, null, Codecs.U32BE, push: 0x089A, samples: [48000u, 44100u, 16000u, 0u]),
	];
}
