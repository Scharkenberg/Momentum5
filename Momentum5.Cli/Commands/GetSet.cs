using System.CommandLine;
using System.Globalization;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Cli;

/// <summary>get / set / eq / keys, generated from the <see cref="GaiaCatalog"/> descriptors plus a few friendly aliases.</summary>
internal static class GetSet
{
	private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
	{
		["anc"] = nameof(Keys.AncEnabled),
		["transparency"] = nameof(Keys.TransparencyStatus),
		["bass"] = nameof(Keys.BassBoost),
		["immersive"] = nameof(Keys.ImmersiveAudio),
		["headtracking"] = nameof(Keys.HeadTracking),
		["touch"] = nameof(Keys.TouchControlsEnabled),
		["wear"] = nameof(Keys.WearDetection),
		["battery"] = nameof(Keys.BatteryPercent),
		["name"] = nameof(Keys.LocalName),
		["sidetone"] = nameof(Keys.ComfortCall),
		["prompt"] = nameof(Keys.PromptMode),
		["soundmode"] = nameof(Keys.SoundMode),
		["autooff"] = nameof(Keys.AutoOff),
	};

	private static readonly Dictionary<string, string> AncSub = new(StringComparer.OrdinalIgnoreCase)
	{
		["adaptive"] = nameof(Keys.AncAdaptive),
		["antiwind"] = nameof(Keys.AncAntiWind),
		["comfort"] = nameof(Keys.AncComfort),
	};

	private static readonly Dictionary<string, string> AntiWindNames = new(StringComparer.OrdinalIgnoreCase)
	{
		["off"] = "0",
		["max"] = "1",
		["auto"] = "2",
	};

	public static Command Get(GlobalOptions g)
	{
		var args = new Argument<string[]>("key") { Description = "Setting name or alias, plus an index for indexed settings (e.g. 'anc', 'eqband 3', 'anc antiwind').", Arity = ArgumentArity.OneOrMore };
		var command = new Command("get", "Read one setting.") { args };
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var (binding, index, rest) = Resolve(result.GetValue(args)!, read: true);
			if (rest.Count > 0)
			{
				throw new CliException(2, $"Unexpected extra arguments: {string.Join(' ', rest)}");
			}

			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			var value = await session.Service.GetValueAsync(binding, index, ct).ConfigureAwait(false);
			Print(s, binding, index, value, verified: null);
			return 0;
		}));
		return command;
	}

	public static Command Set(GlobalOptions g)
	{
		var args = new Argument<string[]>("key-and-value") { Description = "Setting, optional index, then the value (e.g. 'anc on', 'anc antiwind auto', 'bass off').", Arity = ArgumentArity.OneOrMore };
		var command = new Command("set", "Write one setting and verify it by reading it back.") { args };
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var (binding, index, rest) = Resolve(result.GetValue(args)!, read: false);
			if (rest.Count == 0)
			{
				throw new CliException(2, $"Missing value for {binding.Name}.");
			}

			var value = ParseValue(binding, string.Join(' ', rest));
			await WriteAsync(s, binding, index, value, ct).ConfigureAwait(false);
			return 0;
		}));
		return command;
	}

	public static Command Eq(GlobalOptions g)
	{
		var eq = new Command("eq", "Equalizer: get, band <i> <dB>, set <g0,g1,...>.");

		var get = new Command("get", "Print the 8 band gains.");
		get.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			var binding = GaiaCatalog.Registry.ByName(Keys.EqAllGains.Name)!;
			Print(s, binding, -1, await session.Service.GetValueAsync(binding, -1, ct).ConfigureAwait(false), verified: null);
			return 0;
		}));

		var band = new Argument<int>("band") { Description = "Band index (0-7)." };
		var gain = new Argument<double>("db") { Description = "Gain in dB." };
		var bandCommand = new Command("band", "Set one band.") { band, gain };
		bandCommand.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var binding = GaiaCatalog.Registry.ByName(Keys.EqBand(0).Name)!;
			await WriteAsync(g.Read(result), binding, result.GetValue(band), result.GetValue(gain), ct).ConfigureAwait(false);
			return 0;
		}));

		var gains = new Argument<string>("gains") { Description = "Comma-separated gains, one per band." };
		var setCommand = new Command("set", "Set all bands (serial writes, rolled back on failure).") { gains };
		setCommand.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var parts = result.GetValue(gains)!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			var list = new List<double>();
			foreach (var part in parts)
			{
				if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
				{
					throw new CliException(2, $"'{part}' is not a number.");
				}

				list.Add(d);
			}

			await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
			var outcome = await session.Service.SetEqAsync(list, ct).ConfigureAwait(false);
			var table = GaiaCatalog.Registry.ByName(Keys.EqAllGains.Name)!;
			Print(s, table, -1, outcome.Actual, outcome.Verified);
			return 0;
		}));

		eq.Subcommands.Add(get);
		eq.Subcommands.Add(bandCommand);
		eq.Subcommands.Add(setCommand);
		return eq;
	}

	public static Command KeysCommand(GlobalOptions g)
	{
		var command = new Command("keys", "List every known setting with type, access, safety class and accepted values.");
		command.SetAction(result =>
		{
			var json = result.GetValue(g.Json);
			if (json)
			{
				Console.WriteLine(Output.Json(
					w =>
					{
						w.WriteStartObject();
						w.WriteNumber("schema", 1);
						w.WriteStartArray("keys");
						foreach (var b in GaiaCatalog.Bindings)
						{
							w.WriteStartObject();
							w.WriteString("name", b.Name);
							w.WriteString("type", TypeName(b.ValueType));
							w.WriteString("access", Access(b));
							w.WriteString("safety", b.Safety.ToString());
							w.WriteBoolean("indexed", b.Indexed);
							w.WriteString("group", b.Group.ToString());
							if (b.ValueType.IsEnum)
							{
								w.WriteStartArray("values");
								foreach (var n in Enum.GetNames(b.ValueType))
								{
									w.WriteStringValue(n);
								}

								w.WriteEndArray();
							}

							w.WriteEndObject();
						}

						w.WriteEndArray();
						w.WriteEndObject();
					},
					indented: true));
				return;
			}

			foreach (var b in GaiaCatalog.Bindings)
			{
				var values = b.ValueType.IsEnum ? " {" + string.Join('|', Enum.GetNames(b.ValueType)) + "}" : string.Empty;
				Console.WriteLine($"{b.Name}{(b.Indexed ? "[i]" : string.Empty)} : {TypeName(b.ValueType)} ({Access(b)}, {b.Safety}){values}");
			}
		});
		return command;
	}

	public static async Task WriteAsync(CliSettings s, ISettingBinding binding, int index, object value, CancellationToken ct)
	{
		if (binding.SetCmd is null)
		{
			throw new CliException(2, $"{binding.Name} is read-only.");
		}

		if (binding.Safety >= SafetyClass.Gated)
		{
			throw new CliException(3, $"Refused: {binding.Name} is {binding.Safety}; it is not available from the CLI.");
		}

		if (!binding.TryEncodeSet(value, index, out _, out var failure))
		{
			throw new CliException(2, $"Invalid value for {binding.Name}: {failure}");
		}

		await using var session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
		var (verified, actual) = await session.Service.SetValueAsync(binding, index, value, ct).ConfigureAwait(false);
		Print(s, binding, index, actual, verified);
	}

	private static (ISettingBinding Binding, int Index, List<string> Tail) Resolve(string[] tokens, bool read)
	{
		var first = tokens[0];
		var rest = tokens.Skip(1).ToList();
		var index = -1;

		var open = first.IndexOf('[', StringComparison.Ordinal);
		if (open > 0 && first.EndsWith(']') && int.TryParse(first[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
		{
			index = parsed;
			first = first[..open];
		}

		string name;
		if (string.Equals(first, "anc", StringComparison.OrdinalIgnoreCase) && rest.Count > 0 && AncSub.TryGetValue(rest[0], out var sub))
		{
			name = sub;
			rest.RemoveAt(0);
		}
		else
		{
			name = Aliases.TryGetValue(first, out var alias) ? alias : first;
		}

		var binding = GaiaCatalog.Bindings.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
			?? throw new CliException(2, $"Unknown setting '{first}'. Run 'm5ctl keys'.");

		if (binding.Indexed && index < 0)
		{
			if (rest.Count == 0 || !int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out index))
			{
				throw new CliException(2, $"{binding.Name} needs an index, e.g. '{binding.Name} 3'.");
			}

			rest.RemoveAt(0);
		}

		if (read && binding.GetCmd is null)
		{
			throw new CliException(2, $"{binding.Name} cannot be read.");
		}

		return (binding, index, rest);
	}

	private static object ParseValue(ISettingBinding binding, string text)
	{
		var type = binding.ValueType;
		if (binding.Name == nameof(Keys.AncAntiWind) && AntiWindNames.TryGetValue(text, out var number))
		{
			text = number;
		}

		if (type == typeof(bool))
		{
			return text.ToUpperInvariant() switch
			{
				"ON" or "TRUE" or "1" or "YES" => true,
				"OFF" or "FALSE" or "0" or "NO" => false,
				_ => throw new CliException(2, $"'{text}' is not on/off."),
			};
		}

		if (type.IsEnum)
		{
			if (Enum.TryParse(type, text, true, out var e) && Enum.IsDefined(type, e!))
			{
				return e!;
			}

			throw new CliException(2, $"'{text}' is not one of: {string.Join(", ", Enum.GetNames(type))}.");
		}

		try
		{
			if (type == typeof(string))
			{
				return text;
			}

			if (type == typeof(double))
			{
				return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
			}

			if (type == typeof(byte[]))
			{
				return Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));
			}

			if (type == typeof(IReadOnlyList<double>))
			{
				return text.Split(',', StringSplitOptions.TrimEntries).Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
			}

			if (type is { IsPrimitive: true })
			{
				return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
			}
		}
		catch (Exception ex) when (ex is FormatException or OverflowException)
		{
			throw new CliException(2, $"'{text}' is not a valid {TypeName(type)}.");
		}

		throw new CliException(2, $"{binding.Name} ({TypeName(type)}) cannot be set from the CLI.");
	}

	private static void Print(CliSettings s, ISettingBinding binding, int index, object value, bool? verified)
	{
		if (s.Json)
		{
			Console.WriteLine(Output.Json(w =>
			{
				w.WriteStartObject();
				w.WriteString("key", binding.Name);
				if (index >= 0)
				{
					w.WriteNumber("index", index);
				}

				w.WritePropertyName("value");
				Output.WriteJson(w, value);
				if (verified is { } v)
				{
					w.WriteBoolean("verified", v);
				}

				w.WriteEndObject();
			}));
			return;
		}

		Console.WriteLine($"{Output.Key(binding.Name, index)} = {Output.Text(value)}");
		if (verified == false)
		{
			Console.Error.WriteLine("Note: the headset reports a different value than written (read-back shown).");
		}
	}

	private static string Access(ISettingBinding b) => (b.GetCmd is not null, b.SetCmd is not null) switch
	{
		(true, true) => "rw",
		(true, false) => "ro",
		(false, true) => "wo",
		_ => "push",
	};

	private static string TypeName(Type t) => t.IsEnum ? t.Name : t == typeof(IReadOnlyList<double>) ? "double[]" : t.Name.ToLowerInvariant();
}
