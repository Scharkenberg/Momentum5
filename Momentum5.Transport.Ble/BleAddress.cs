using System.Globalization;
using System.Text.RegularExpressions;

namespace Momentum5.Transport.Ble;

/// <summary>Bluetooth address helpers. Never returns a fake 0 address: callers get false/0 only when nothing parseable exists.</summary>
public static partial class BleAddress
{
	public static string Format(ulong address)
	{
		var hex = address.ToString("X12", CultureInfo.InvariantCulture);
		return $"{hex[..2]}:{hex[2..4]}:{hex[4..6]}:{hex[6..8]}:{hex[8..10]}:{hex[10..12]}";
	}

	/// <summary>Accepts <c>AA:BB:CC:DD:EE:FF</c>, <c>AA-BB-...</c>, <c>AABBCCDDEEFF</c> and <c>0xAABBCCDDEEFF</c>; rejects 0 and more than 12 digits.</summary>
	public static bool TryParse(string? text, out ulong address)
	{
		address = 0;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var cleaned = text.Trim();
		if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned[2..];
		}

		cleaned = NonHex().Replace(cleaned, string.Empty);
		return cleaned.Length is > 0 and <= 12
			&& ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
			&& address != 0;
	}

	/// <summary>Device interface ids end with the remote address, e.g. <c>BluetoothLE#BluetoothLE00:11:22:33:44:55-66:c6:71:65:36:f0</c>; 0 when none.</summary>
	public static ulong FromDeviceId(string? deviceId)
	{
		if (string.IsNullOrEmpty(deviceId))
		{
			return 0;
		}

		var matches = MacAtEnd().Matches(deviceId);
		return matches.Count > 0 && TryParse(matches[^1].Value, out var address) ? address : 0;
	}

	[GeneratedRegex("[^0-9a-fA-F]")]
	private static partial Regex NonHex();

	[GeneratedRegex(@"[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\s*$")]
	private static partial Regex MacAtEnd();
}
