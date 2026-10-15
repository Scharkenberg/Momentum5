using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Momentum5.Transport.Ble;

namespace Momentum5.Cli;

/// <summary>Automated BLE/GATT discovery and diagnostics (not project tests). The headset only advertises in pairing mode.</summary>
internal static class Gatt
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static Command Build(GlobalOptions g)
	{
		var seconds = new Option<int>("--seconds") { Description = "Scan duration.", DefaultValueFactory = _ => 8 };
		var le = new Option<string?>("--le") { Description = "BLE address (the rotating private address, e.g. 66:C6:71:65:36:F0). Default: auto-pick." };
		var cycles = new Option<int>("--cycles") { Description = "Connect/disconnect stability rounds (0 skips).", DefaultValueFactory = _ => 5 };
		var listen = new Option<int>("--listen") { Description = "Seconds to listen to notifications (0 skips).", DefaultValueFactory = _ => 5 };
		var gaia = new Option<bool>("--gaia-probe") { Description = "Send the read-only GAIA 'get API version' frame if a GAIA GATT service exists." };
		var outFile = new Option<string?>("--out") { Description = "Write the full JSON report to this file." };

		var scan = new Command("scan", "Scan for BLE devices and list candidates (merged with devices Windows already knows).") { seconds };
		scan.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var list = await GattLab.ScanAsync(TimeSpan.FromSeconds(result.GetValue(seconds)), ClassicHint(s), ct).ConfigureAwait(false);
			if (s.Json)
			{
				Console.WriteLine(JsonSerializer.Serialize(list, JsonOptions));
				return 0;
			}

			foreach (var c in list)
			{
				Console.WriteLine(Line(c));
			}

			Console.WriteLine($"{list.Count} device(s){(list.Any(c => c.IsLikelyHeadset) ? string.Empty : "; none looks like the headset - put it in pairing mode and rescan")}");
			return 0;
		}));

		var diag = new Command("diag", "Run every automated GATT check: discover, read, notify, optional GAIA probe, connect/disconnect cycles.") { seconds, le, cycles, listen, gaia, outFile };
		diag.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			GattTarget target;
			if (result.GetValue(le) is { } text)
			{
				if (!BleAddress.TryParse(text, out var addr))
				{
					throw new CliException(2, $"Invalid --le '{text}'.");
				}

				target = new GattTarget(addr, null, "manual");
			}
			else
			{
				Console.Error.WriteLine("scanning for the headset...");
				var list = await GattLab.ScanAsync(TimeSpan.FromSeconds(result.GetValue(seconds)), ClassicHint(s), ct).ConfigureAwait(false);
				var pick = GattLab.Pick(list) ?? throw new CliException(1, "No candidate found. Put the headset in pairing mode (it only advertises then), or pass --le <address>. Candidates:\n" + string.Join('\n', list.Select(Line)));
				Console.Error.WriteLine($"target: {Line(pick)}");
				target = pick.ToTarget();
			}

			var options = new GattDiagOptions
			{
				Cycles = Math.Max(0, result.GetValue(cycles)),
				Listen = TimeSpan.FromSeconds(Math.Max(0, result.GetValue(listen))),
				GaiaProbe = result.GetValue(gaia),
			};
			var report = await GattLab.DiagnoseAsync(
				target,
				options,
				new InlineProgress<GattStep>(step =>
				{
					if (!s.Json)
					{
						Console.WriteLine($"[{step.Status.ToString().ToUpperInvariant(),-4}] {step.Name}: {step.Detail}{(step.Ms > 0 ? $" ({step.Ms} ms)" : string.Empty)}");
					}
				}),
				n =>
				{
					if (!s.Json)
					{
						Console.WriteLine($"  push {GattNames.Characteristic(n.Characteristic)} {GattNames.Hex(n.Value)}");
					}
				},
				ct).ConfigureAwait(false);

			var json = JsonSerializer.Serialize(report, JsonOptions);
			if (result.GetValue(outFile) is { } path)
			{
				await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
				Console.Error.WriteLine($"report written to {path}");
			}

			if (s.Json)
			{
				Console.WriteLine(json);
			}
			else
			{
				Console.WriteLine($"=> {report.Summary} in {report.DurationMs / 1000.0:0.0} s");
			}

			return report.HasFailures ? 1 : 0;
		}));

		return new Command("gatt", "BLE/GATT discovery and diagnostics.") { scan, diag };
	}

	private static ulong? ClassicHint(CliSettings s)
	{
		if (s.Address is { } a && Output.TryParseAddress(a, out var wanted))
		{
			return wanted;
		}

		return DeviceFinder.TryReadLast(out var last) ? last : null;
	}

	private static string Line(BleCandidate c) =>
		string.Create(CultureInfo.InvariantCulture,
			$"{(c.IsLikelyHeadset ? "*" : " ")} {(c.Address != 0 ? BleAddress.Format(c.Address) : "address unknown   ")} {(c.Rssi is { } r ? $"{r,4} dBm" : "   - dBm")} x{c.Seen,-3} {c.AdvertisementTypes} \"{c.Name}\"{(c.IsPaired ? " paired" : string.Empty)}{(c.IsConnected ? " connected" : string.Empty)}{(c.SwiftPairClassicAddress is { } sp ? $" swiftpair->{Output.FormatAddress(sp)}" : string.Empty)}{(c.ManufacturerData.Count > 0 ? $" mfg[{string.Join(',', c.ManufacturerData)}]" : string.Empty)}{(c.Reason.Length > 0 ? $"  <- {c.Reason}" : string.Empty)}");
}
