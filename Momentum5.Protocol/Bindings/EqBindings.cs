using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

public static class EqBindings
{
	private const byte OutOfRange = 0x83;
	private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

	private static readonly IPayloadFormat<EqConfig> ConfigFormat =
		PayloadFormat.Create<EqConfig>(DecodeConfig, EncodeConfig);

	private static readonly IPayloadFormat<double> BandFormat =
		PayloadFormat.Create<double>(DecodeBand, EncodeBand);

	private static readonly IPayloadFormat<IReadOnlyList<double>> AllGainsFormat =
		PayloadFormat.Create<IReadOnlyList<double>>(DecodeAllGains, EncodeAllGains);

	private static readonly IPayloadFormat<int> FreqFormat =
		PayloadFormat.Create<int>(DecodeFreq, EncodeFreq);

	private static readonly IPayloadFormat<SoundMode> SoundModeFormat =
		PayloadFormat.Create<SoundMode>(DecodeSoundMode, EncodeSoundMode);

	// Personalization (3) is readable but never writable.
	private static readonly IPayloadFormat<SoundMode> SoundModeSetFormat =
		PayloadFormat.Create<SoundMode>(DecodeSoundMode, EncodeSoundModeWrite);

	public static IReadOnlyList<ISettingBinding> All { get; } = Build();

	private static List<ISettingBinding> Build() =>
	[
		Bind.Create(
			Keys.EqConfig, SettingGroup.Eq, 0x1000, null, ConfigFormat, samples: [new EqConfig(8, -6.0, 6.0)]),
		Bind.Create(
			Keys.EqBand(0), SettingGroup.Eq, 0x1002, 0x1001, BandFormat, indexed: true, busyGrace: Grace,
			errorMapper: MapBandError, samples: [0.0, 0.1, -6.0, 6.0]),
		Bind.Create(
			Keys.EqAllGains, SettingGroup.Eq, 0x1003, null, AllGainsFormat,
			selector: Bind.ZeroSelector, samples: [new double[8]]),
		Bind.Create(
			Keys.EqBandFreq(0), SettingGroup.Eq, 0x100B, null, FreqFormat, indexed: true,
			errorMapper: MapBandError, samples: [90, 325, 1500, 2500, 4500, 8000, 13000, 16000]),
		Bind.Create(Keys.BassBoost, SettingGroup.Eq, 0x1009, 0x1008, Codecs.Bool, samples: [false, true]),
		Bind.Create(
			Keys.SoundMode, SettingGroup.Sound, 0x0804, 0x0803, SoundModeFormat, setFormat: SoundModeSetFormat,
			busyGrace: Grace, samples: [SoundMode.GraphicEq, SoundMode.Off, SoundMode.SpeechClarity]),
		Bind.Create(Keys.PersonalizationState, SettingGroup.Sound, 0x2001, null, Codecs.U8, samples: [(byte)0]),
	];

	private static Exception? MapBandError(byte reason, int index) =>
		reason == OutOfRange ? new EqBandOutOfRangeException(index) : null;

	private static bool DecodeConfig(ReadOnlySpan<byte> payload, int index, out EqConfig value)
	{
		if (payload.Length >= 3)
		{
			value = new EqConfig(payload[0], unchecked((sbyte)payload[1]) / 10.0, unchecked((sbyte)payload[2]) / 10.0);
			return true;
		}

		value = new EqConfig(0, 0, 0);
		return false;
	}

	private static bool EncodeConfig(EqConfig value, int index, out byte[] payload, out string? error)
	{
		payload = [(byte)value.Bands, unchecked((byte)(sbyte)Math.Round(value.MinDb * 10)), unchecked((byte)(sbyte)Math.Round(value.MaxDb * 10)), 0, 0];
		error = null;
		return true;
	}

	private static bool DecodeBand(ReadOnlySpan<byte> payload, int index, out double value)
	{
		if (payload.Length == 1)
		{
			value = unchecked((sbyte)payload[0]) / 10.0;
			return true;
		}

		value = 0;
		return false;
	}

	private static bool EncodeBand(double value, int index, out byte[] payload, out string? error)
	{
		var tenths = Math.Round(value * 10, MidpointRounding.AwayFromZero);
		if (tenths is < sbyte.MinValue or > sbyte.MaxValue)
		{
			payload = [];
			error = "EQ gain out of range.";
			return false;
		}

		payload = [unchecked((byte)(sbyte)tenths)];
		error = null;
		return true;
	}

	private static bool DecodeAllGains(ReadOnlySpan<byte> payload, int index, out IReadOnlyList<double> value)
	{
		if (payload.Length == 0)
		{
			value = [];
			return false;
		}

		var gains = new double[payload.Length];
		for (var i = 0; i < gains.Length; i++)
		{
			gains[i] = unchecked((sbyte)payload[i]) / 10.0;
		}

		value = gains;
		return true;
	}

	private static bool EncodeAllGains(IReadOnlyList<double> value, int index, out byte[] payload, out string? error)
	{
		payload = new byte[value.Count];
		for (var i = 0; i < payload.Length; i++)
		{
			var tenths = Math.Round(value[i] * 10, MidpointRounding.AwayFromZero);
			if (tenths is < sbyte.MinValue or > sbyte.MaxValue)
			{
				payload = [];
				error = "EQ gain out of range.";
				return false;
			}

			payload[i] = unchecked((byte)(sbyte)tenths);
		}

		error = null;
		return true;
	}

	private static bool DecodeFreq(ReadOnlySpan<byte> payload, int index, out int value)
	{
		if (payload.Length == 3 && payload[0] == index)
		{
			value = (payload[1] << 8) | payload[2];
			return true;
		}

		value = 0;
		return false;
	}

	private static bool EncodeFreq(int value, int index, out byte[] payload, out string? error)
	{
		if (value is < 0 or > ushort.MaxValue)
		{
			payload = [];
			error = "Frequency out of range.";
			return false;
		}

		payload = [(byte)index, (byte)(value >> 8), (byte)(value & 0xFF)];
		error = null;
		return true;
	}

	private static bool DecodeSoundMode(ReadOnlySpan<byte> payload, int index, out SoundMode value)
	{
		if (payload.Length == 2 && payload[0] == 0 && Enum.IsDefined((SoundMode)payload[1]))
		{
			value = (SoundMode)payload[1];
			return true;
		}

		value = SoundMode.Off;
		return false;
	}

	private static bool EncodeSoundMode(SoundMode value, int index, out byte[] payload, out string? error)
	{
		payload = [0x00, (byte)value];
		error = null;
		return true;
	}

	private static bool EncodeSoundModeWrite(SoundMode value, int index, out byte[] payload, out string? error)
	{
		if (value == SoundMode.Personalization)
		{
			payload = [];
			error = "Personalization cannot be selected from here; use Sound Personalization.";
			return false;
		}

		return EncodeSoundMode(value, index, out payload, out error);
	}
}
