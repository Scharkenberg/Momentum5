using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Transport.Rfcomm;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Momentum5.Cli;

internal static class Probe
{
	private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

	public static async Task<int> RunAsync(string? address, string name, CancellationToken ct)
	{
		ulong? wanted = null;
		if (address is not null)
		{
			if (!TryParseAddress(address, out var parsed))
			{
				Console.Error.WriteLine($"Invalid --address '{address}'.");
				return 2;
			}

			wanted = parsed;
		}

		try
		{
			var target = await FindAsync(wanted, name, ct).ConfigureAwait(false);
			if (target is null)
			{
				Console.Error.WriteLine("No matching paired device.");
				return 1;
			}

			Console.WriteLine($"Target: {target.Name} {FormatAddress(target.Address)} connected={target.IsConnected}");

			var transport = new RfcommGaiaTransport { Diagnostics = m => Console.Error.WriteLine($"[rfcomm] {m}") };
			await using var link = await transport.ConnectAsync(target, ct).ConfigureAwait(false);
			var deframer = new GaiaDeframer();
			var buffer = new byte[1024];

			var model = await QueryAsync(link, deframer, buffer, 0x1206, ct).ConfigureAwait(false);
			Console.WriteLine($"Model:    {Encoding.UTF8.GetString(model).TrimEnd('\0')}");

			var fw = await QueryAsync(link, deframer, buffer, 0x1201, ct).ConfigureAwait(false);
			Console.WriteLine(fw.Length == 6
				? $"Firmware: {BinaryPrimitives.ReadUInt16BigEndian(fw)}.{BinaryPrimitives.ReadUInt16BigEndian(fw.AsSpan(2))}.{BinaryPrimitives.ReadUInt16BigEndian(fw.AsSpan(4))}"
				: $"Firmware: unexpected length {fw.Length}");

			var battery = await QueryAsync(link, deframer, buffer, 0x0603, ct).ConfigureAwait(false);
			Console.WriteLine(battery.Length >= 1 ? $"Battery:  {battery[0]}%" : "Battery:  empty payload");
			return 0;
		}
		catch (LinkException ex)
		{
			Console.Error.WriteLine($"Link error {ex.Error} (0x{ex.HResult:X8}): {ex.Message}");
			return 1;
		}
		catch (GaiaProtocolException ex)
		{
			Console.Error.WriteLine($"Device error reply for {ex.Command:X4}: reason 0x{ex.Reason:X2} ({GaiaProtocolException.Describe(ex.Reason)})");
			return 1;
		}
		catch (TimeoutException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
		catch (OperationCanceledException)
		{
			Console.Error.WriteLine("Cancelled.");
			return 1;
		}
	}

	private static async Task<byte[]> QueryAsync(ILinkConnection link, GaiaDeframer deframer, byte[] buffer, ushort command, CancellationToken ct)
	{
		var tx = new GaiaFrame(GaiaVendor.Sennheiser, command, default).Encode();
		Console.WriteLine($"TX {Hex(tx)}");
		await link.WriteAsync(tx, ct).ConfigureAwait(false);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(ReplyTimeout);
		try
		{
			while (true)
			{
				while (deframer.TryRead(out var frame))
				{
					Console.WriteLine($"RX {Hex(frame.Encode())}");
					var (baseCommand, kind) = frame.Classify();
					if (baseCommand != command)
					{
						continue;
					}

					if (kind == GaiaMessageKind.Response)
					{
						return frame.Payload.ToArray();
					}

					if (kind == GaiaMessageKind.Error)
					{
						throw GaiaProtocolException.FromFrame(frame)!;
					}
				}

				var n = await link.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
				if (n == 0)
				{
					throw new LinkException(BtError.ConnectionReset, unchecked((int)0x80072746), "Link closed while waiting for a reply.");
				}

				deframer.Append(buffer.AsSpan(0, n));
			}
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new TimeoutException($"No reply to {command:X4} within {ReplyTimeout.TotalSeconds:0} s.");
		}
	}

	private static async Task<DeviceIdentity?> FindAsync(ulong? address, string name, CancellationToken ct)
	{
		var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
		var infos = await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);
		var all = new List<DeviceIdentity>();
		foreach (var info in infos)
		{
			using var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
			if (device is null)
			{
				continue;
			}

			all.Add(new DeviceIdentity(
				device.BluetoothAddress, string.IsNullOrEmpty(device.Name) ? info.Name : device.Name, info.Id, true,
				device.ConnectionStatus == BluetoothConnectionStatus.Connected));
		}

		Console.WriteLine($"Paired devices ({all.Count}):");
		foreach (var d in all)
		{
			Console.WriteLine($"  {d.Name} | {FormatAddress(d.Address)} | connected={d.IsConnected}");
		}

		if (address is { } wanted)
		{
			return all.FirstOrDefault(d => d.Address == wanted);
		}

		var byName = all.Where(d => d.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
			.OrderByDescending(d => d.IsConnected).FirstOrDefault();
		if (byName is not null)
		{
			return byName;
		}

		var connected = all.Where(d => d.IsConnected).ToList();
		if (connected.Count == 1)
		{
			Console.WriteLine($"No name match for '{name}'; using the only connected device.");
			return connected[0];
		}

		Console.Error.WriteLine("Pick one from the list above with --address.");
		return null;
	}

	private static bool TryParseAddress(string text, out ulong address)
	{
		var hex = text.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
		address = 0;
		return hex.Length is > 0 and <= 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
	}

	private static string FormatAddress(ulong address) =>
		string.Join(':', address.ToString("X12", CultureInfo.InvariantCulture).Chunk(2).Select(c => new string(c)));

	private static string Hex(byte[] bytes) =>
		string.Join(' ', Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)));
}
