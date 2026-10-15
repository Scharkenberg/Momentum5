using System.Buffers.Binary;
using System.Text;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

public static class IdentityBindings
{
	public const int MaxLocalNameBytes = 16;

	// AutoOff payload: [00][u16BE seconds] (3 bytes); a bare u16 is accepted on decode. Format UNVERIFIED on M5.
	private static readonly IPayloadFormat<AutoOffTimer> AutoOffFormat =
		PayloadFormat.Create<AutoOffTimer>(DecodeAutoOff, EncodeAutoOff);

	private static readonly IPayloadFormat<string> LocalNameSetFormat =
		PayloadFormat.Create<string>(DecodeLocalName, EncodeLocalName);

	public static IReadOnlyList<ISettingBinding> All { get; } = Build();

	private static List<ISettingBinding> Build() =>
	[
		Bind.Create(Keys.Model, SettingGroup.Identity, 0x1206, null, Codecs.CString, samples: ["M5AEBT Black"]),
		Bind.Create(
			Keys.Firmware, SettingGroup.Identity, 0x1201, null, Codecs.VersionTriple,
			timeout: TimeSpan.FromSeconds(10), samples: [new FirmwareVersion(6, 21, 5)]),
		Bind.Create(Keys.HwRevision, SettingGroup.Identity, 0x1200, null, Codecs.RawBytes, samples: [Convert.FromHexString("0001")]),
		Bind.Create(Keys.FirmwareVersions, SettingGroup.Identity, 0x1202, null, Codecs.RawBytes, samples: [Convert.FromHexString("0001")]),
		Bind.Create(Keys.Serial, SettingGroup.Identity, 0x0003, null, Codecs.CString, vendor: GaiaVendor.Qualcomm, samples: ["0000000000"]),
		Bind.Create(Keys.PrimarySide, SettingGroup.Identity, 0x0041, null, Codecs.U8, samples: [(byte)0]),
		Bind.Create(
			Keys.BatteryPercent, SettingGroup.Power, 0x0603, null, Codecs.Level0to100, push: 0x0683,
			timeout: TimeSpan.FromSeconds(9), samples: [70, 30, 100, 0]),
		Bind.Create(
			Keys.ChargingState, SettingGroup.Power, 0x0602, null, Codecs.ByteEnum<ChargingState>(), push: 0x0682,
			samples: [ChargingState.None, ChargingState.Charging, ChargingState.Full]),
		Bind.Create(
			Keys.AutoOff, SettingGroup.Power, 0x0601, 0x0600, AutoOffFormat,
			selector: Bind.ZeroSelector,
			samples: [AutoOffTimer.Minutes60, AutoOffTimer.Never, AutoOffTimer.Minutes15, AutoOffTimer.Minutes30]),
		Bind.Create(Keys.BatteryProtection, SettingGroup.Power, 0x060B, 0x060A, Codecs.Bool, samples: [false, true]),
		Bind.Create(
			Keys.LocalName, SettingGroup.Device, 0x2802, 0x2801, PayloadFormat.Of(Codecs.CString),
			setFormat: LocalNameSetFormat, safety: SafetyClass.Gated, samples: ["M5 Sim"]),
	];

	private static bool DecodeAutoOff(ReadOnlySpan<byte> payload, int index, out AutoOffTimer value)
	{
		value = AutoOffTimer.Never;
		var body = payload.Length == 3 && payload[0] == 0 ? payload[1..] : payload;
		if (body.Length != 2)
		{
			return false;
		}

		var candidate = (AutoOffTimer)BinaryPrimitives.ReadUInt16BigEndian(body);
		if (!Enum.IsDefined(candidate))
		{
			return false;
		}

		value = candidate;
		return true;
	}

	private static bool EncodeAutoOff(AutoOffTimer value, int index, out byte[] payload, out string? error)
	{
		var seconds = (int)value;
		payload = [0x00, (byte)(seconds >> 8), (byte)(seconds & 0xFF)];
		error = null;
		return true;
	}

	private static bool DecodeLocalName(ReadOnlySpan<byte> payload, int index, out string value) =>
		Codecs.CString.TryDecode(payload, out value);

	private static bool EncodeLocalName(string value, int index, out byte[] payload, out string? error)
	{
		payload = [];
		if (value.Contains('\0', StringComparison.Ordinal))
		{
			error = "Name must not contain NUL.";
			return false;
		}

		var bytes = Encoding.UTF8.GetBytes(value);
		if (bytes.Length is 0 or > MaxLocalNameBytes)
		{
			error = $"Name must be 1..{MaxLocalNameBytes} UTF-8 bytes.";
			return false;
		}

		payload = bytes;
		error = null;
		return true;
	}
}
