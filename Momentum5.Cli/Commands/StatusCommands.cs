using System.CommandLine;
using System.Globalization;
using Momentum5.Client;
using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Cli;

internal sealed class GlobalOptions
{
	public Option<bool> Json { get; } = new("--json") { Description = "Machine-readable output.", Recursive = true };

	public Option<bool> Simulate { get; } = new("--simulate") { Description = "Use the built-in fake M5 instead of hardware.", Recursive = true };

	public Option<string?> Address { get; } = new("--address")
	{
		Description = "Bluetooth address (AA:BB:CC:DD:EE:FF). Overrides --name.",
		Recursive = true,
	};

	public Option<string> Name { get; } = new("--name")
	{
		Description = "Substring of the paired device name.",
		DefaultValueFactory = _ => "MOMENTUM",
		Recursive = true,
	};

	public IEnumerable<Option> All => [Json, Simulate, Address, Name];

	public CliSettings Read(ParseResult result) =>
		new(result.GetValue(Json), result.GetValue(Simulate), result.GetValue(Address), result.GetValue(Name)!);
}

internal static class StatusCommands
{
	public static Command List(GlobalOptions g)
	{
		var command = new Command("list", "List paired Bluetooth devices (headset names are user-editable, so all are shown).");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			IReadOnlyList<Momentum5.Core.Transport.DeviceIdentity> devices = s.Simulate
				? [new(0xA1B2C3D4E5F6, "M5 Simulator", null, true, true)]
				: await new Momentum5.Transport.Rfcomm.ClassicPresence().GetPairedAsync(ct).ConfigureAwait(false);
			if (s.Json)
			{
				Console.WriteLine(Output.Json(w =>
				{
					w.WriteStartObject();
					w.WriteNumber("schema", 1);
					w.WriteStartArray("devices");
					foreach (var d in devices)
					{
						w.WriteStartObject();
						w.WriteString("name", d.Name);
						w.WriteString("address", Output.FormatAddress(d.Address));
						w.WriteBoolean("connected", d.IsConnected);
						w.WriteEndObject();
					}

					w.WriteEndArray();
					w.WriteEndObject();
				}));
			}
			else
			{
				foreach (var d in devices)
				{
					Console.WriteLine($"{d.Name} | {Output.FormatAddress(d.Address)} | connected={d.IsConnected}");
				}
			}

			return 0;
		}));
		return command;
	}

	public static Command Status(GlobalOptions g)
	{
		var command = new Command("status", "Connect, refresh everything and print the full snapshot.");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			await session.Service.RefreshAllAsync(ct).ConfigureAwait(false);
			var snapshot = session.Store.Current;
			var info = session.Active.Info;
			var values = snapshot.Entries
				.Select(e => (Key: Output.Key(e.Key.Name, e.Key.Index), e.Value))
				.OrderBy(e => e.Key, StringComparer.Ordinal)
				.ToList();
			if (s.Json)
			{
				Console.WriteLine(Output.Json(
					w =>
					{
						w.WriteStartObject();
						w.WriteNumber("schema", 1);
						w.WriteStartObject("session");
						w.WriteString("model", info.Model);
						w.WriteString("firmware", info.Firmware);
						w.WriteString("serial", info.Serial);
						w.WriteString("trust", info.Trust.ToString());
						w.WriteString("transport", info.TransportId);
						w.WriteStartArray("features");
						foreach (var f in info.Features)
						{
							w.WriteStringValue(f);
						}

						w.WriteEndArray();
						w.WriteEndObject();
						w.WriteStartObject("values");
						foreach (var (key, value) in values)
						{
							w.WritePropertyName(key);
							Output.WriteJson(w, value);
						}

						w.WriteEndObject();
						w.WriteEndObject();
					},
					indented: true));
			}
			else
			{
				Console.WriteLine($"{info.Model} | firmware {info.Firmware} | trust {info.Trust} | {info.TransportId}");
				foreach (var (key, value) in values)
				{
					Console.WriteLine($"{key} = {Output.Text(value)}");
				}
			}

			return 0;
		}));
		return command;
	}

	public static Command Watch(GlobalOptions g)
	{
		var command = new Command("watch", "Print live push notifications until Ctrl+C (the simulator ticks the battery).");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			var hub = session.Active.Hub;
			hub.SettingChanged += (_, n) => Console.WriteLine(s.Json
				? Output.Json(w =>
				{
					w.WriteStartObject();
					w.WriteString("event", "changed");
					w.WriteString("key", n.Name);
					w.WriteNumber("index", n.Index);
					w.WritePropertyName("value");
					Output.WriteJson(w, n.Value);
					w.WriteEndObject();
				})
				: $"{Output.Key(n.Name, n.Index)} = {Output.Text(n.Value)}");
			hub.UnknownReceived += (_, n) => Console.WriteLine(s.Json
				? Output.Json(w =>
				{
					w.WriteStartObject();
					w.WriteString("event", "unknown");
					w.WriteString("vendor", n.Vendor.ToString());
					w.WriteString("command", n.Command.ToString("X4", CultureInfo.InvariantCulture));
					w.WriteString("payload", Convert.ToHexString(n.Payload.Span));
					w.WriteEndObject();
				})
				: $"? {n.Vendor}:{n.Command:X4} {GaiaFormatter.Hex(n.Payload.Span)}");

			var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			session.Supervisor.StateChanged += (_, state) =>
			{
				if (state.Phase is not (SessionPhase.Ready or SessionPhase.ReadOnly))
				{
					lost.TrySetResult();
				}
			};

			var tick = s.Simulate && session.Fake is not null ? SimulatorTickAsync(session, ct) : Task.CompletedTask;
			await Task.WhenAny(lost.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
			try
			{
				await tick.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}

			if (ct.IsCancellationRequested)
			{
				return 0;
			}

			throw new CliException(1, "The headset session ended.");
		}));
		return command;
	}

	private static async Task SimulatorTickAsync(CliSession session, CancellationToken ct)
	{
		var level = 70;
		while (!ct.IsCancellationRequested)
		{
			await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
			level = level <= 1 ? 100 : level - 1;
			session.Fake!.Notify(Keys.BatteryPercent, level);
		}
	}
}
