using System.CommandLine;
using Momentum5.Cli;

var g = new GlobalOptions();
var probe = new Command("probe", "Connect over RFCOMM and read model, firmware and battery (hardware spike G1).");
probe.SetAction((result, ct) => Probe.RunAsync(result.GetValue(g.Address), result.GetValue(g.Name)!, ct));

var root = new RootCommand("m5ctl - MOMENTUM 5 Wireless control")
{
	probe,
	StatusCommands.List(g),
	StatusCommands.Status(g),
	StatusCommands.Watch(g),
	GetSet.Get(g),
	GetSet.Set(g),
	GetSet.Eq(g),
	GetSet.KeysCommand(g),
	Matrix.Build(g),
	SelfTest.Build(g),
	Diagnostics.Presence(g),
	Diagnostics.BatteryCheck(g),
	Diagnostics.Ble(g),
	Diagnostics.HfpDump(g),
	Gatt.Build(g),
	Multipoint.Build(g),
};
foreach (var option in g.All)
{
	root.Options.Add(option);
}

var parse = root.Parse(args);
if (parse.Errors.Count > 0)
{
	foreach (var error in parse.Errors)
	{
		Console.Error.WriteLine(error.Message);
	}

	return 2;
}

return await parse.InvokeAsync();
