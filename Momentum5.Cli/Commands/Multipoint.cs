using System.CommandLine;
using System.Globalization;
using Momentum5.Client;

namespace Momentum5.Cli;

/// <summary>Reads the M5 by-MAC connection table and prints it with the raw frames (read-only).</summary>
internal static class Multipoint
{
	public static Command Build(GlobalOptions g)
	{
		var command = new Command("multipoint", "Read the headset's paired-device table (1409/1413/140D/140E/1414), read-only.");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			var table = await MultipointReader.ReadAsync(session.Active, ct).ConfigureAwait(false);
			Console.WriteLine(table.Trace);
			foreach (var d in table.Devices)
			{
				var marked = table.Current is { } c && c == d.Address ? " *" : string.Empty;
				Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{d.Address:X12}  flags {d.FlagsHex}  {d.Name}{marked}"));
			}

			return 0;
		}));
		return command;
	}
}
