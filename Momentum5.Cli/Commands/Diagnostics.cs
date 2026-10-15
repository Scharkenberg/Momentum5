using System.Collections.Concurrent;
using System.CommandLine;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Globalization;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Transport.Ble;
using Momentum5.Transport.Rfcomm;
using Momentum5.Transport.Rfcomm.Hfp;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration.Pnp;

namespace Momentum5.Cli;

/// <summary>Hardware checks for the presence transports (T-05/T-06/T-07), independent of any GAIA session.</summary>
internal static class Diagnostics
{
	public static Command Presence(GlobalOptions g)
	{
		var command = new Command("presence", "Print classic connect/disconnect, BLE adverts, radio state and HFP battery until Ctrl+C. Power-cycle the headset while it runs.");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var target = await DeviceFinder.FindAsync(s.Address, s.Name, ct).ConfigureAwait(false)
				?? throw new CliException(1, "No matching paired device. Use --address (see 'm5ctl list').");
			var address = target.Address;
			Log($"watching {target.Name} {Output.FormatAddress(address)}");

			using var radio = new BluetoothRadioMonitor();
			radio.Changed += (_, on) => Log($"radio: {(on ? "on" : "off")}");
			Log($"radio: {(await radio.IsOnAsync(ct).ConfigureAwait(false) ? "on" : "off")}");

			using var classic = new ClassicPresence { TargetAddress = address };
			bool? lastConnected = null;
			classic.Changed += (_, d) =>
			{
				if (d.Address == address && lastConnected != d.IsConnected)
				{
					lastConnected = d.IsConnected;
					Log($"classic: {d.Name} connected={d.IsConnected}");
				}
			};
			classic.Start();
			await classic.WatchTargetAsync(address, ct).ConfigureAwait(false);

			using var adverts = new AdvertisementPresence { TargetAddress = address };
			adverts.Sighted += (_, a) => Log($"ble: {Output.FormatAddress(a.Address)} \"{a.Name}\" rssi {a.Rssi}");
			adverts.StartWatching();

			var hfp = new HfpBatteryReader();
			int? last = -1;
			try
			{
				while (true)
				{
					var now = await hfp.TryReadPercentAsync(address, ct).ConfigureAwait(false);
					if (now != last)
					{
						Log(now is { } n ? $"hfp battery: {n} %" : "hfp battery: unavailable");
						last = now;
					}

					await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				return 0;
			}
		}));
		return command;
	}

	public static Command BatteryCheck(GlobalOptions g)
	{
		var command = new Command("batterycheck", "Compare the GAIA battery (0603) with the HFP battery fallback; passes within 1 %.");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			var binding = GaiaCatalog.Bindings.First(b => b.Name == nameof(Keys.BatteryPercent));
			var gaia = Convert.ToInt32(await session.Service.GetValueAsync(binding, -1, ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
			var hfp = await new HfpBatteryReader().TryReadPercentAsync(session.Supervisor.Target!.Address, ct).ConfigureAwait(false);
			Console.WriteLine($"GAIA 0603: {gaia} %");
			if (hfp is not { } h)
			{
				Console.WriteLine("HFP: unavailable (the hands-free profile must be connected to Windows).");
				return 1;
			}

			Console.WriteLine($"HFP: {h} %");
			var pass = Math.Abs(h - gaia) <= 1;
			Console.WriteLine(pass ? "PASS" : "FAIL");
			return pass ? 0 : 1;
		}));
		return command;
	}

	public static Command Ble(GlobalOptions g)
	{
		var seconds = new Option<int>("--seconds") { Description = "Scan duration.", DefaultValueFactory = _ => 20 };
		var command = new Command("ble", "Raw active BLE scan: print every distinct advertiser (name, manufacturer data, services). For finding how the M5 advertises.") { seconds };
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var secs = result.GetValue(seconds);
			var seen = new ConcurrentDictionary<ulong, string>();
			var total = new ConcurrentDictionary<ulong, byte>();
			var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
			watcher.Stopped += (_, e) => Log($"watcher stopped: {e.Error}");
			watcher.Received += (_, a) =>
			{
				var adv = a.Advertisement;
				var mfg = string.Join(',', adv.ManufacturerData.Select(m => $"{m.CompanyId:X4}:{Convert.ToHexString(m.Data.ToArray())}"));
				var uuids = string.Join(',', adv.ServiceUuids);
				var line = $"{Output.FormatAddress(a.BluetoothAddress)} {a.AdvertisementType} \"{adv.LocalName}\" mfg[{mfg}] svc[{uuids}]";
				total[a.BluetoothAddress] = 0;
				if (seen.TryGetValue(a.BluetoothAddress, out var old) && old == line)
				{
					return;
				}

				seen[a.BluetoothAddress] = line;
				var probable = adv.LocalName.Contains("MOMENTUM", StringComparison.OrdinalIgnoreCase)
					|| adv.LocalName.Contains("Sennheiser", StringComparison.OrdinalIgnoreCase)
					|| adv.LocalName.Contains("Vibe", StringComparison.OrdinalIgnoreCase)
					|| mfg.Contains("0494:", StringComparison.Ordinal)
					|| mfg.Contains("0006:0301", StringComparison.Ordinal);
				Log($"{(probable ? "*" : " ")} rssi {a.RawSignalStrengthInDBm,4} {line}");
			};
			watcher.Start();
			Log($"scanning {secs} s, status {watcher.Status} (* = probably the headset)");
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(secs), ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}

			watcher.Stop();
			Log($"{total.Count} distinct advertisers");
			if (total.IsEmpty)
			{
				Console.Error.WriteLine("No advertisements at all: BLE scanning is blocked or unsupported by this radio.");
				return 1;
			}

			return 0;
		}));
		return command;
	}

	public static Command HfpDump(GlobalOptions g)
	{
		var command = new Command("hfpdump", "List PnP devnodes that contain the headset address or carry the battery property.");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var target = await DeviceFinder.FindAsync(s.Address, s.Name, ct).ConfigureAwait(false)
				?? throw new CliException(1, "No matching paired device. Use --address (see 'm5ctl list').");
			var mac = target.Address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
			var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device, [HfpBatteryReader.BatteryProperty]).AsTask(ct).ConfigureAwait(false);
			var any = false;
			foreach (var node in nodes)
			{
				node.Properties.TryGetValue(HfpBatteryReader.BatteryProperty, out var value);
				if (node.Id.Contains(mac, StringComparison.OrdinalIgnoreCase) || value is not null)
				{
					any = true;
					Console.WriteLine($"{(node.Id.Contains(mac, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {node.Id} battery={value ?? "(none)"}");
				}
			}

			if (!any)
			{
				Console.Error.WriteLine("No devnode found.");
			}

			return any ? 0 : 1;
		}));
		return command;
	}

	private static void Log(string message) =>
		Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
}
