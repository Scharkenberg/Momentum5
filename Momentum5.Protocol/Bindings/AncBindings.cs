using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

/// <summary>
/// ANC and transparency. The three ANC mode keys share GET 1A01 / push 1A81 (a table of (id,value) pairs)
/// and SET 1A00 which takes ONE pair. Registration order matters: it is the table order (1, 3, 2).
/// </summary>
public static class AncBindings
{
	private const byte IdAntiWind = 1;
	private const byte IdComfort = 2;
	private const byte IdAdaptive = 3;

	private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

	private static readonly IPayloadFormat<byte> AntiWindFormat =
		PayloadFormat.Create<byte>(DecodeAntiWind, EncodeAntiWind);

	private static readonly IPayloadFormat<bool> AdaptiveFormat =
		PayloadFormat.Create<bool>(
			(ReadOnlySpan<byte> p, int i, out bool v) => DecodePairBool(p, IdAdaptive, out v),
			(bool v, int i, out byte[] p, out string? e) => EncodePairBool(IdAdaptive, v, out p, out e));

	private static readonly IPayloadFormat<bool> ComfortFormat =
		PayloadFormat.Create<bool>(
			(ReadOnlySpan<byte> p, int i, out bool v) => DecodePairBool(p, IdComfort, out v),
			(bool v, int i, out byte[] p, out string? e) => EncodePairBool(IdComfort, v, out p, out e));

	// SET carries exactly one pair; the 6-byte table write is rejected by the M5.
	private static readonly IPayloadFormat<byte> AntiWindSetFormat =
		PayloadFormat.Create<byte>(DecodeAntiWindSet, EncodeAntiWind);

	private static readonly IPayloadFormat<bool> AdaptiveSetFormat =
		PayloadFormat.Create<bool>(
			(ReadOnlySpan<byte> p, int i, out bool v) => DecodeSinglePairBool(p, IdAdaptive, out v),
			(bool v, int i, out byte[] p, out string? e) => EncodePairBool(IdAdaptive, v, out p, out e));

	public static IReadOnlyList<ISettingBinding> All { get; } = Build();

	private static List<ISettingBinding> Build() =>
	[
		Bind.Create(
			Keys.AncEnabled, SettingGroup.Anc, 0x1A05, 0x1A04, Codecs.Bool, push: 0x1A85,
			busyGrace: Grace, refreshTogether: true, samples: [true, false]),
		Bind.Create(
			Keys.AncLevel, SettingGroup.Anc, 0x1A03, 0x1A02, Codecs.Level0to100, push: 0x1A83,
			refreshTogether: true, samples: [0, 50, 100]),
		Bind.Create(
			Keys.AncAntiWind, SettingGroup.Anc, 0x1A01, 0x1A00, AntiWindFormat, AntiWindSetFormat, push: 0x1A81,
			busyGrace: Grace, refreshTogether: true, samples: [(byte)0, (byte)1]),
		Bind.Create(
			Keys.AncAdaptive, SettingGroup.Anc, 0x1A01, 0x1A00, AdaptiveFormat, AdaptiveSetFormat, push: 0x1A81,
			busyGrace: Grace, refreshTogether: true, samples: [false, true]),
		Bind.Create(
			Keys.AncComfort, SettingGroup.Anc, 0x1A01, null, ComfortFormat, push: 0x1A81,
			refreshTogether: true, samples: [false]),
		Bind.Create(
			Keys.TransparencyStatus, SettingGroup.Transparency, 0x1805, 0x1804, Codecs.Bool, push: 0x1885,
			busyGrace: Grace, refreshTogether: true, samples: [false, true]),
		Bind.Create(
			Keys.TransparencyLevel, SettingGroup.Transparency, 0x1803, 0x1802, Codecs.Level0to100, push: 0x1883,
			refreshTogether: true, samples: [100, 50, 0]),
		Bind.Create(
			Keys.TransparencyAutoPause, SettingGroup.Transparency, 0x1801, 0x1800, Codecs.Bool,
			refreshTogether: true, samples: [false, true]),
	];

	private static bool TryFindPair(ReadOnlySpan<byte> payload, byte id, out byte value)
	{
		value = 0;
		if (payload.Length == 0 || (payload.Length & 1) != 0)
		{
			return false;
		}

		for (var i = 0; i < payload.Length; i += 2)
		{
			if (payload[i] == id)
			{
				value = payload[i + 1];
				return true;
			}
		}

		return false;
	}

	private static bool DecodeAntiWind(ReadOnlySpan<byte> payload, int index, out byte value)
	{
		if (TryFindPair(payload, IdAntiWind, out var raw))
		{
			value = raw > 1 ? (byte)1 : raw;
			return true;
		}

		value = 0;
		return false;
	}

	private static bool EncodeAntiWind(byte value, int index, out byte[] payload, out string? error)
	{
		if (value > 2)
		{
			payload = [];
			error = "Anti-wind value must be 0 (off), 1 (max) or 2 (auto).";
			return false;
		}

		payload = [IdAntiWind, value];
		error = null;
		return true;
	}

	private static bool DecodeAntiWindSet(ReadOnlySpan<byte> payload, int index, out byte value)
	{
		if (payload.Length == 2)
		{
			return DecodeAntiWind(payload, index, out value);
		}

		value = 0;
		return false;
	}

	private static bool DecodeSinglePairBool(ReadOnlySpan<byte> payload, byte id, out bool value)
	{
		if (payload.Length == 2)
		{
			return DecodePairBool(payload, id, out value);
		}

		value = false;
		return false;
	}

	private static bool DecodePairBool(ReadOnlySpan<byte> payload, byte id, out bool value)
	{
		if (TryFindPair(payload, id, out var raw) && raw <= 1)
		{
			value = raw == 1;
			return true;
		}

		value = false;
		return false;
	}

	private static bool EncodePairBool(byte id, bool value, out byte[] payload, out string? error)
	{
		payload = [id, (byte)(value ? 1 : 0)];
		error = null;
		return true;
	}
}
