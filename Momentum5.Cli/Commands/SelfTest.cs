using System.CommandLine;
using System.Diagnostics;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Cli;

/// <summary>Gate G2: connect, identity, battery, refresh-all, three safe writes (verified, push-checked, restored).</summary>
internal static class SelfTest
{
	private static readonly TimeSpan PushWait = TimeSpan.FromSeconds(2);

	private sealed record Step(string Name, bool Ok, string Detail, long Ms);

	public static Command Build(GlobalOptions g)
	{
		var command = new Command("selftest", "Scripted end-to-end check (gate G2): connect, identity, battery, refresh, 3 safe writes with restore, push check.");
		command.SetAction((result, ct) => Runner.RunAsync(async () =>
		{
			var s = g.Read(result);
			var steps = new List<Step>();
			var total = Stopwatch.StartNew();
			var pushes = 0;
			CliSession? session = null;

			async Task<bool> Run(string name, Func<Task<string>> body)
			{
				var sw = Stopwatch.StartNew();
				try
				{
					var detail = await body().ConfigureAwait(false);
					steps.Add(new Step(name, true, detail, sw.ElapsedMilliseconds));
					return true;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					steps.Add(new Step(name, false, ex.Message, sw.ElapsedMilliseconds));
					return false;
				}
			}

			try
			{
				if (await Run("connect", async () =>
				{
					session = await CliSession.OpenAsync(s, ct).ConfigureAwait(false);
					var hub = session.Active.Hub;
					return $"{session.Supervisor.State.Phase}; {hub.Registered.Count} notification registrations: {string.Join(' ', hub.Registered.Select(e => $"{(e.Vendor == GaiaVendor.Sennheiser ? 'S' : 'Q')}:{e.Feature:X2}"))}";
				}).ConfigureAwait(false))
				{
					var svc = session!.Service;
					var info = session.Active.Info;

					await Run("identity", () => Task.FromResult(!info.Model.StartsWith("M5AEBT", StringComparison.Ordinal)
						? throw new InvalidOperationException($"Unexpected model '{info.Model}'.")
						: $"{info.Model} fw {info.Firmware} serial {info.Serial} trust {info.Trust} features {info.Features?.Count ?? 0}")).ConfigureAwait(false);

					await Run("battery", async () =>
					{
						var v = await svc.GetValueAsync(Binding(nameof(Keys.BatteryPercent)), -1, ct).ConfigureAwait(false);
						return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) is >= 0 and <= 100
							? $"{v} %"
							: throw new InvalidOperationException($"Battery out of range: {v}.");
					}).ConfigureAwait(false);

					await Run("refresh-all", async () =>
					{
						await svc.RefreshAllAsync(ct).ConfigureAwait(false);
						var n = session.Store.Current.Count;
						return n >= 30 ? $"{n} values" : throw new InvalidOperationException($"Only {n} values read.");
					}).ConfigureAwait(false);

					async Task<string> WriteCheck(string key, Func<object, object> pick)
					{
						var b = Binding(key);
						var original = await svc.GetValueAsync(b, -1, ct).ConfigureAwait(false);
						var target = pick(original);
						var pushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
						var hub = session.Active.Hub;
						void OnChanged(object? sender, SettingNotification n)
						{
							if (n.Name == key)
							{
								pushed.TrySetResult(true);
							}
						}

						hub.SettingChanged += OnChanged;
						try
						{
							var sw = Stopwatch.StartNew();
							var (verified, actual) = await svc.SetValueAsync(b, -1, target, ct).ConfigureAwait(false);
							var gotPush = await Task.WhenAny(pushed.Task, Task.Delay(PushWait, ct)).ConfigureAwait(false) == pushed.Task;
							if (gotPush)
							{
								pushes++;
							}

							if (!verified || !Equals(actual, target))
							{
								throw new InvalidOperationException($"wrote {Output.Text(target)}, read back {Output.Text(actual)}");
							}

							return $"{Output.Text(original)} -> {Output.Text(actual)} verified, push {(gotPush ? "yes" : "no")}, {sw.ElapsedMilliseconds} ms";
						}
						finally
						{
							hub.SettingChanged -= OnChanged;
							try
							{
								var (_, back) = await svc.SetValueAsync(b, -1, original, ct).ConfigureAwait(false);
								if (!Equals(back, original))
								{
									await Console.Error.WriteLineAsync($"WARNING: {key} not restored (is {Output.Text(back)}, was {Output.Text(original)}).").ConfigureAwait(false);
								}
							}
							catch (Exception ex) when (ex is not OperationCanceledException)
							{
								await Console.Error.WriteLineAsync($"WARNING: restoring {key} failed: {ex.Message}. Original was {Output.Text(original)}.").ConfigureAwait(false);
							}
						}
					}

					await Run("write AncLevel", () => WriteCheck(nameof(Keys.AncLevel), o => (int)o == 50 ? 100 : 50)).ConfigureAwait(false);
					await Run("write PhoneCallTransparency", () => WriteCheck(nameof(Keys.PhoneCallTransparency), o => (byte)(((byte)o + 1) % 5))).ConfigureAwait(false);
					await Run("write PromptMode", () => WriteCheck(nameof(Keys.PromptMode), o => (PromptMode)o == PromptMode.TonesOnly ? PromptMode.Off : PromptMode.TonesOnly)).ConfigureAwait(false);
					steps.Add(new Step("push notifications", pushes > 0, $"{pushes}/3 writes echoed by an unsolicited push", 0));
				}
			}
			finally
			{
				if (session is not null)
				{
					await session.DisposeAsync().ConfigureAwait(false);
				}
			}

			total.Stop();
			steps.Add(new Step("time budget", total.ElapsedMilliseconds < 30_000, $"{total.ElapsedMilliseconds} ms (limit 30000)", total.ElapsedMilliseconds));
			var pass = steps.All(x => x.Ok);
			Print(s, steps, pass);
			return pass ? 0 : 1;
		}));
		return command;
	}

	private static ISettingBinding Binding(string name) =>
		GaiaCatalog.Bindings.First(b => b.Name == name);

	private static void Print(CliSettings s, List<Step> steps, bool pass)
	{
		if (s.Json)
		{
			Console.WriteLine(Output.Json(
				w =>
				{
					w.WriteStartObject();
					w.WriteNumber("schema", 1);
					w.WriteBoolean("pass", pass);
					w.WriteStartArray("steps");
					foreach (var x in steps)
					{
						w.WriteStartObject();
						w.WriteString("name", x.Name);
						w.WriteBoolean("ok", x.Ok);
						w.WriteString("detail", x.Detail);
						w.WriteNumber("ms", x.Ms);
						w.WriteEndObject();
					}

					w.WriteEndArray();
					w.WriteEndObject();
				},
				indented: true));
			return;
		}

		foreach (var x in steps)
		{
			Console.WriteLine($"{(x.Ok ? "PASS" : "FAIL")}  {x.Name,-28} {x.Detail}");
		}

		Console.WriteLine(pass ? "SELFTEST PASS" : "SELFTEST FAIL");
	}
}
