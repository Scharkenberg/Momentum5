using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

/// <summary>Multipoint. Formats are from M4 notes; the M5 capture shows a different family (150D/150E): verify on hardware.</summary>
public static class ConnectionBindings
{
	private static readonly TimeSpan Slow = TimeSpan.FromSeconds(9);

	private static readonly IPayloadFormat<PairedDevice> PairedInfoFormat =
		PayloadFormat.Create<PairedDevice>(DecodePairedInfo, EncodePairedInfo);

	// ConnectPaired carries only the index byte (added by the binding); the value is a dummy.
	private static readonly IPayloadFormat<bool> ActionFormat =
		PayloadFormat.Create<bool>(DecodeAction, EncodeAction);

	public static IReadOnlyList<ISettingBinding> All { get; } = Build();

	private static List<ISettingBinding> Build() =>
	[
		Bind.Create(Keys.PairedCount, SettingGroup.Connections, 0x1400, null, Codecs.U8, timeout: Slow, samples: [(byte)2]),
		Bind.Create(
			Keys.PairedInfo(0), SettingGroup.Connections, 0x1401, null, PairedInfoFormat, indexed: true,
			samples: [new PairedDevice(3, 2, true, "Pixel"), new PairedDevice(3, 1, false, "Laptop")]),
		Bind.Create(
			Keys.PairedStatus(0), SettingGroup.Connections, 0x1404, null, PayloadFormat.Of(Codecs.RawBytes), push: 0x1484,
			indexed: true, samples: [Convert.FromHexString("0101")]),
		Bind.Create(Keys.OwnIndex, SettingGroup.Connections, 0x1407, null, Codecs.U8, samples: [(byte)0]),
		Bind.Create(Keys.MaxConnections, SettingGroup.Connections, 0x1409, null, Codecs.U8, samples: [(byte)2]),
		Bind.Create(
			Keys.ConnectPaired(0), SettingGroup.Connections, null, 0x1402, ActionFormat, indexed: true,
			safety: SafetyClass.Gated, samples: [true]),
	];

	// [index][priority][connected][name\0]
	private static bool DecodePairedInfo(ReadOnlySpan<byte> payload, int index, out PairedDevice value)
	{
		if (payload.Length >= 4 && payload[0] == index && Codecs.CString.TryDecode(payload[3..], out var name))
		{
			value = new PairedDevice(payload[0], payload[1], payload[2] != 0, name);
			return true;
		}

		value = new PairedDevice(0, 0, false, string.Empty);
		return false;
	}

	private static bool EncodePairedInfo(PairedDevice value, int index, out byte[] payload, out string? error)
	{
		payload = [(byte)value.Index, (byte)value.Priority, (byte)(value.Connected ? 1 : 0), .. Codecs.CString.Encode(value.Name)];
		error = null;
		return true;
	}

	private static bool DecodeAction(ReadOnlySpan<byte> payload, int index, out bool value)
	{
		value = true;
		return payload.IsEmpty;
	}

	private static bool EncodeAction(bool value, int index, out byte[] payload, out string? error)
	{
		payload = [];
		error = null;
		return true;
	}
}
