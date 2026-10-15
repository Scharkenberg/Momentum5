using System.Globalization;
using System.Text;
using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>One entry of the headset's paired-device table. <see cref="Flags"/> are the three undecoded bytes of the 150E record.</summary>
public sealed record MultipointDevice(ulong Address, byte[] Flags, string Name)
{
	public string FlagsHex => Convert.ToHexString(Flags);

	/// <summary>First flag byte: 02 = connected, 01 = paired only (observed on FW 6.21.5).</summary>
	public bool IsConnected => Flags.Length > 0 && Flags[0] == 2;
}

public sealed record MultipointTable(int? MaxConnections, ulong? Current, byte[] StatusRaw, IReadOnlyList<MultipointDevice> Devices, string Trace);

/// <summary>
/// Reads the M5 "by MAC" connection table: 1409 max, 1413 current, 140D list, 140E per device, 1414 status. Read-only; the
/// connect/disconnect commands of this family have not been captured yet, so none are sent.
/// </summary>
public static class MultipointReader
{
	private const GaiaVendor S = GaiaVendor.Sennheiser;

	public static async Task<MultipointTable> ReadAsync(ActiveSession session, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(session);
		var trace = new StringBuilder();
		var options = new CommandOptions { Timeout = TimeSpan.FromSeconds(6) };

		async Task<byte[]> Ask(ushort cmd, byte[]? payload = null)
		{
			var frame = await session.Pipeline.ExecuteAsync(S, cmd, payload ?? [], options, ct).ConfigureAwait(false);
			var bytes = frame.Payload.ToArray();
			trace.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{cmd:X4} {(payload is null ? string.Empty : Convert.ToHexString(payload))} -> {Convert.ToHexString(bytes)}"));
			return bytes;
		}

		int? max = null;
		try
		{
			var m = await Ask(0x1409).ConfigureAwait(false);
			max = m.Length > 0 ? m[^1] : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			trace.AppendLine($"1409 failed: {ex.Message}");
		}

		ulong? current = null;
		try
		{
			var c = await Ask(0x1413).ConfigureAwait(false);
			current = c.Length >= 6 ? ToAddress(c.AsSpan(c.Length - 6, 6)) : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			trace.AppendLine($"1413 failed: {ex.Message}");
		}

		var list = await Ask(0x140D).ConfigureAwait(false);
		var devices = new List<MultipointDevice>();
		foreach (var mac in SplitAddresses(list))
		{
			var record = await Ask(0x140E, ToBytes(mac)).ConfigureAwait(false);
			if (record.Length >= 9)
			{
				var name = Encoding.UTF8.GetString(record.AsSpan(9)).TrimEnd('\0');
				devices.Add(new MultipointDevice(ToAddress(record.AsSpan(0, 6)), record[6..9], name));
			}
			else
			{
				devices.Add(new MultipointDevice(mac, [], string.Empty));
			}
		}

		byte[] status = [];
		try
		{
			status = await Ask(0x1414).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			trace.AppendLine($"1414 failed: {ex.Message}");
		}

		return new MultipointTable(max, current, status, devices, trace.ToString());
	}

	/// <summary>The list payload is 1 + 6n bytes (a leading count/continuation byte) or plain 6n bytes.</summary>
	internal static IEnumerable<ulong> SplitAddresses(byte[] payload)
	{
		var offset = payload.Length % 6 == 1 ? 1 : 0;
		for (var i = offset; i + 6 <= payload.Length; i += 6)
		{
			var mac = ToAddress(payload.AsSpan(i, 6));
			if (mac != 0)
			{
				yield return mac;
			}
		}
	}

	internal static ulong ToAddress(ReadOnlySpan<byte> six)
	{
		ulong v = 0;
		foreach (var b in six)
		{
			v = (v << 8) | b;
		}

		return v;
	}

	internal static byte[] ToBytes(ulong address)
	{
		var result = new byte[6];
		for (var i = 5; i >= 0; i--)
		{
			result[i] = (byte)(address & 0xFF);
			address >>= 8;
		}

		return result;
	}
}
