using System.Globalization;
using System.Text;

namespace Momentum5.Transport.Ble;

/// <summary>Readable names and value decoding for well-known GATT UUIDs.</summary>
public static class GattNames
{
	public static readonly Guid GaiaService = new("00001100-d102-11e1-9b23-00025b00a5a5");
	public static readonly Guid GaiaCommand = new("00001101-d102-11e1-9b23-00025b00a5a5");
	public static readonly Guid GaiaResponse = new("00001102-d102-11e1-9b23-00025b00a5a5");
	public static readonly Guid GaiaData = new("00001103-d102-11e1-9b23-00025b00a5a5");

	private static readonly Dictionary<ushort, string> Services = new()
	{
		[0x1800] = "Generic Access",
		[0x1801] = "Generic Attribute",
		[0x180A] = "Device Information",
		[0x180F] = "Battery",
		[0xFDCE] = "0xFDCE (vendor/member service)",
		[0xFE2C] = "Google Fast Pair",
		[0xFD92] = "0xFD92 (Qualcomm member UUID; eb20-eb22 vendor channel)",
		[0xFCFE] = "0xFCFE (member UUID, vendor channel 6333133b-...)",
	};

	private static readonly Dictionary<ushort, string> Characteristics = new()
	{
		[0x2A00] = "Device Name",
		[0x2A01] = "Appearance",
		[0x2A04] = "Preferred Connection Parameters",
		[0x2A05] = "Service Changed",
		[0x2A19] = "Battery Level",
		[0x2A23] = "System ID",
		[0x2A24] = "Model Number",
		[0x2A25] = "Serial Number",
		[0x2A26] = "Firmware Revision",
		[0x2A27] = "Hardware Revision",
		[0x2A28] = "Software Revision",
		[0x2A29] = "Manufacturer Name",
		[0x2A50] = "PnP ID",
	};

	public static bool TryShort(Guid uuid, out ushort value)
	{
		value = 0;
		var s = uuid.ToString();
		if (!s.StartsWith("0000", StringComparison.Ordinal) || !s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal))
		{
			return false;
		}

		return ushort.TryParse(s.AsSpan(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
	}

	public static string Service(Guid uuid) => Describe(uuid, Services, uuid == GaiaService ? "GAIA service (Qualcomm)" : null);

	public static string Characteristic(Guid uuid)
	{
		var special = uuid == GaiaCommand ? "GAIA command endpoint"
			: uuid == GaiaResponse ? "GAIA response endpoint"
			: uuid == GaiaData ? "GAIA data endpoint"
			: null;
		return Describe(uuid, Characteristics, special);
	}

	/// <summary>Decoded value for known characteristics, otherwise printable ASCII if it looks like text, otherwise empty.</summary>
	public static string DecodeValue(Guid characteristic, byte[] value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (TryShort(characteristic, out var id))
		{
			switch (id)
			{
				case 0x2A00 or 0x2A24 or 0x2A25 or 0x2A26 or 0x2A27 or 0x2A28 or 0x2A29:
					return Encoding.UTF8.GetString(value).TrimEnd('\0');
				case 0x2A19 when value.Length >= 1:
					return string.Create(CultureInfo.InvariantCulture, $"{value[0]} %");
				case 0x2A01 when value.Length >= 2:
					return string.Create(CultureInfo.InvariantCulture, $"0x{BitConverter.ToUInt16(value, 0):X4}");
				case 0x2A50 when value.Length >= 7:
					return string.Create(
						CultureInfo.InvariantCulture,
						$"vendor-id-source {value[0]}, vid 0x{BitConverter.ToUInt16(value, 1):X4}, pid 0x{BitConverter.ToUInt16(value, 3):X4}, version 0x{BitConverter.ToUInt16(value, 5):X4}");
			}
		}

		return value.Length > 0 && value.All(b => b is >= 0x20 and < 0x7F) ? Encoding.ASCII.GetString(value) : string.Empty;
	}

	public static string Hex(ReadOnlySpan<byte> value) => value.IsEmpty ? string.Empty : Convert.ToHexString(value);

	private static string Describe(Guid uuid, Dictionary<ushort, string> table, string? special)
	{
		if (special is not null)
		{
			return special;
		}

		return TryShort(uuid, out var id)
			? table.TryGetValue(id, out var name) ? $"{name} (0x{id:X4})" : $"0x{id:X4}"
			: uuid.ToString();
	}
}
