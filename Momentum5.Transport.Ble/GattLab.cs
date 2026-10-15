using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Momentum5.Transport.Ble;

/// <summary>Automated BLE/GATT discovery and diagnostics. Read-only apart from the optional GAIA "get API version" probe.</summary>
public static class GattLab
{
	private sealed class Agg
	{
		public string? Name;
		public short Last;
		public short Max = short.MinValue;
		public int Count;
		public ulong? Swift;
		public readonly HashSet<string> Mfg = [];
		public readonly HashSet<string> Uuids = [];
		public readonly HashSet<string> Types = [];
	}

	public static async Task<IReadOnlyList<BleCandidate>> ScanAsync(TimeSpan duration, ulong? classicHint, CancellationToken ct)
	{
		var seen = new Dictionary<ulong, Agg>();
		var gate = new Lock();
		var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
		watcher.Received += (_, a) =>
		{
			lock (gate)
			{
				if (!seen.TryGetValue(a.BluetoothAddress, out var g))
				{
					seen[a.BluetoothAddress] = g = new Agg();
				}

				g.Count++;
				g.Types.Add(a.AdvertisementType.ToString());
				g.Last = a.RawSignalStrengthInDBm;
				g.Max = Math.Max(g.Max, a.RawSignalStrengthInDBm);
				if (!string.IsNullOrWhiteSpace(a.Advertisement.LocalName))
				{
					g.Name = a.Advertisement.LocalName;
				}

				foreach (var m in a.Advertisement.ManufacturerData)
				{
					var data = new byte[m.Data.Length];
					DataReader.FromBuffer(m.Data).ReadBytes(data);
					g.Mfg.Add($"{m.CompanyId:X4}:{Convert.ToHexString(data)}");
				}

				foreach (var u in a.Advertisement.ServiceUuids)
				{
					g.Uuids.Add(u.ToString());
				}

				if (AdvertisementPresence.TrySwiftPair(a.Advertisement, out var classic))
				{
					g.Swift = classic;
				}
			}
		};
		watcher.Start();
		try
		{
			var known = KnownDevicesAsync(ct);
			try
			{
				await Task.Delay(duration, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}

			watcher.Stop();
			var devices = await known.ConfigureAwait(false);
			return Merge(seen, devices, classicHint);
		}
		finally
		{
			try
			{
				watcher.Stop();
			}
			catch (Exception)
			{
			}
		}
	}

	private sealed record Known(ulong Address, string DeviceId, string Name, bool Paired, bool Connected);

	private static async Task<List<Known>> KnownDevicesAsync(CancellationToken ct)
	{
		var result = new List<Known>();
		string[] props = ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired"];
		foreach (var selector in new[]
		{
			BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
			BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
		})
		{
			try
			{
				var found = await DeviceInformation.FindAllAsync(selector, props).AsTask(ct).ConfigureAwait(false);
				foreach (var d in found)
				{
					ulong address = 0;
					if (d.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var p) && p is string s)
					{
						_ = BleAddress.TryParse(s, out address);
					}

					if (address == 0)
					{
						address = BleAddress.FromDeviceId(d.Id);
					}

					var connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
					var paired = d.Pairing.IsPaired;
					if (result.FindIndex(k => k.DeviceId == d.Id) is var i and >= 0)
					{
						result[i] = result[i] with { Connected = result[i].Connected || connected };
					}
					else
					{
						result.Add(new Known(address, d.Id, d.Name, paired, connected));
					}
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Debug.WriteLine($"GattLab known-device query failed: {ex.Message}");
			}
		}

		return result;
	}

	private static bool NameLooksLikeHeadset(string? name) =>
		name is not null && (name.Contains("MOMENTUM", StringComparison.OrdinalIgnoreCase) || name.Contains("Sennheiser", StringComparison.OrdinalIgnoreCase) || name.Contains("M5AEBT", StringComparison.OrdinalIgnoreCase));

	private static List<BleCandidate> Merge(Dictionary<ulong, Agg> seen, List<Known> known, ulong? hint)
	{
		var list = new List<BleCandidate>();
		var used = new HashSet<ulong>();
		foreach (var (address, g) in seen)
		{
			var k = known.FirstOrDefault(x => x.Address == address);
			if (k is not null)
			{
				used.Add(address);
			}

			var name = g.Name ?? k?.Name;
			string reason = string.Empty;
			var likely = false;
			if (g.Swift is { } sw && hint is { } h && sw == h)
			{
				likely = true;
				reason = "Swift Pair beacon carries your headset's classic address";
			}
			else if (g.Swift is not null && g.Mfg.Any(m => m.StartsWith("0006:", StringComparison.Ordinal)) && hint is null)
			{
				likely = true;
				reason = "Swift Pair beacon (pairing mode)";
			}
			else if (NameLooksLikeHeadset(name))
			{
				likely = true;
				reason = "name matches";
			}

			list.Add(new BleCandidate(address, k?.DeviceId, name, g.Last, g.Count, k?.Paired ?? false, k?.Connected ?? false, [.. g.Mfg], [.. g.Uuids], g.Swift, likely, reason, string.Join('/', g.Types)));
		}

		foreach (var k in known.Where(x => !used.Contains(x.Address) || x.Address == 0))
		{
			var likely = NameLooksLikeHeadset(k.Name);
			list.Add(new BleCandidate(k.Address, k.DeviceId, k.Name, null, 0, k.Paired, k.Connected, [], [], null, likely, likely ? "known to Windows, name matches" : string.Empty));
		}

		return [.. list.OrderByDescending(c => c.IsLikelyHeadset).ThenByDescending(c => c.IsConnected).ThenByDescending(c => c.Rssi ?? short.MinValue)];
	}

	/// <summary>Picks the most plausible target, or null when ambiguous.</summary>
	public static BleCandidate? Pick(IReadOnlyList<BleCandidate> candidates)
	{
		var likely = candidates.Where(c => c.IsLikelyHeadset).ToList();
		return likely.Count >= 1 ? likely[0] : null;
	}

	public static async Task<GattReport> DiagnoseAsync(
		GattTarget target,
		GattDiagOptions options,
		IProgress<GattStep>? progress,
		Action<GattNotification>? onNotification,
		CancellationToken ct)
	{
		var report = new GattReport { Target = target.ToString(), StartedAt = DateTimeOffset.Now };
		var total = Stopwatch.StartNew();

		void Add(string name, GattStepStatus status, string detail, long ms)
		{
			var step = new GattStep(name, status, detail, ms);
			report.Steps.Add(step);
			progress?.Report(step);
		}

		async Task<T?> Timed<T>(string name, Func<Task<(GattStepStatus Status, string Detail, T? Value)>> body)
		{
			var sw = Stopwatch.StartNew();
			try
			{
				var (status, detail, value) = await body().ConfigureAwait(false);
				Add(name, status, detail, sw.ElapsedMilliseconds);
				return value;
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				Add(name, GattStepStatus.Skip, "cancelled", sw.ElapsedMilliseconds);
				throw;
			}
			catch (Exception ex)
			{
				Add(name, GattStepStatus.Fail, ex.Message, sw.ElapsedMilliseconds);
				return default;
			}
		}

		try
		{
			GattConnection? conn = null;
			try
			{
				conn = await Timed("open device", async () =>
				{
					using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
					linked.CancelAfter(options.ConnectTimeout);
					var c = await GattConnection.OpenAsync(target, null, linked.Token).ConfigureAwait(false);
					return (GattStepStatus.Pass, $"{c.Name ?? "(no name)"} {BleAddress.Format(c.Address)} paired={c.IsPaired} link={(c.IsLinkUp ? "up" : "down")}", c);
				}).ConfigureAwait(false);
				if (conn is null)
				{
					Add("remaining steps", GattStepStatus.Skip, "no device object", 0);
					return Finish(report, total);
				}

				conn.Notification += (_, n) =>
				{
					report.Notifications.Add(new GattNotificationRecord { Service = n.Service, Characteristic = n.Characteristic, Hex = GattNames.Hex(n.Value), At = n.At });
					onNotification?.Invoke(n);
				};

				var services = await Timed("discover (uncached, connects)", async () =>
				{
					var s = await conn.DiscoverAsync(BluetoothCacheMode.Uncached, ct).ConfigureAwait(false);
					var chars = s.Sum(x => x.Characteristics.Count);
					var errs = s.Count(x => x.Error is not null);
					var status = s.Count == 0 ? GattStepStatus.Fail : errs > 0 ? GattStepStatus.Warn : GattStepStatus.Pass;
					return (status, $"{s.Count} services, {chars} characteristics, MaxPduSize={conn.MaxPduSize?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}{(errs > 0 ? $", {errs} service(s) with errors" : string.Empty)}", s);
				}).ConfigureAwait(false);

				if (services is null || services.Count == 0)
				{
					services = await Timed("discover (cached fallback)", async () =>
					{
						var s = await conn.DiscoverAsync(BluetoothCacheMode.Cached, ct).ConfigureAwait(false);
						return (s.Count == 0 ? GattStepStatus.Fail : GattStepStatus.Warn, $"{s.Count} services from the Windows cache (live discovery failed: device likely not connectable over LE right now)", s);
					}).ConfigureAwait(false);
				}

				if (services is null || services.Count == 0)
				{
					Add("remaining steps", GattStepStatus.Skip, "no services discovered. Check the advertisement type in 'gatt scan': ADV_NONCONN/ADV_SCAN means the beacon is not connectable.", 0);
					return Finish(report, total);
				}

				report.Services = [.. services];

				await Timed<object>("discover (cached) consistency", async () =>
				{
					var cached = await conn.DiscoverAsync(BluetoothCacheMode.Cached, ct).ConfigureAwait(false);
					var same = cached.Count == services.Count && cached.Sum(x => x.Characteristics.Count) == services.Sum(x => x.Characteristics.Count);
					return (same ? GattStepStatus.Pass : GattStepStatus.Warn, $"{cached.Count} services cached vs {services.Count} live", null);
				}).ConfigureAwait(false);

				string[] expected = ["1800", "180A", "180F"];
				var present = services.Select(s => s.Uuid).ToHashSet();
				foreach (var e in expected)
				{
					var guid = Guid.Parse($"0000{e}-0000-1000-8000-00805f9b34fb");
					Add($"service {e} {GattNames.Service(guid)}", present.Contains(guid) ? GattStepStatus.Pass : GattStepStatus.Info, present.Contains(guid) ? "present" : "absent", 0);
				}

				Add("GAIA GATT service", present.Contains(GattNames.GaiaService) ? GattStepStatus.Pass : GattStepStatus.Info, present.Contains(GattNames.GaiaService) ? "present (GAIA over GATT possible, see T-08)" : "absent", 0);
				foreach (var sv in services)
				{
					Add($"service {sv.Name}", GattStepStatus.Info, string.Join("; ", sv.Characteristics.Select(c => $"{ShortId(c.Uuid)}[{c.Properties}]")), 0);
				}

				await ReadAllAsync(report, conn, services, Add, ct).ConfigureAwait(false);
				await ListenAsync(conn, services, options, Add, ct).ConfigureAwait(false);
				if (options.GaiaProbe)
				{
					await GaiaProbeAsync(conn, services, Add, ct).ConfigureAwait(false);
				}
			}
			finally
			{
				if (conn is not null)
				{
					await conn.DisposeAsync().ConfigureAwait(false);
				}
			}

			if (options.Cycles > 0 && report.Services.Count > 0)
			{
				await CyclesAsync(target, options, Add, ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			Add("run", GattStepStatus.Skip, "cancelled", 0);
		}

		return Finish(report, total);
	}

	private static GattReport Finish(GattReport report, Stopwatch total)
	{
		report.DurationMs = total.ElapsedMilliseconds;
		return report;
	}

	private static string ShortId(Guid g) => GattNames.TryShort(g, out var v) ? $"{v:X4}" : g.ToString()[..8];

	private static async Task ReadAllAsync(GattReport report, GattConnection conn, IReadOnlyList<GattServiceInfo> services, Action<string, GattStepStatus, string, long> add, CancellationToken ct)
	{
		var sw = Stopwatch.StartNew();
		int ok = 0, failed = 0, tried = 0, streak = 0;
		var aborted = false;
		foreach (var s in services)
		{
			if (aborted)
			{
				break;
			}

			foreach (var c in s.Characteristics.Where(x => x.CanRead))
			{
				if (streak >= 2)
				{
					aborted = true;
					break;
				}

				tried++;
				try
				{
					var v = await conn.ReadAsync(s.Uuid, c.Uuid, ct).ConfigureAwait(false);
					c.ValueHex = GattNames.Hex(v);
					c.ValueText = GattNames.DecodeValue(c.Uuid, v);
					ok++;
					streak = 0;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					c.Error = ex.Message;
					failed++;
					streak++;
				}
			}
		}

		var status = tried == 0 ? GattStepStatus.Info : failed == 0 ? GattStepStatus.Pass : ok == 0 ? GattStepStatus.Fail : GattStepStatus.Warn;
		add("read all readable characteristics", status, $"{ok}/{tried} ok{(failed > 0 ? $", {failed} failed (often 'requires authentication')" : string.Empty)}{(aborted ? "; stopped after 2 consecutive timeouts: no live LE link" : string.Empty)}", sw.ElapsedMilliseconds);
		foreach (var c in services.SelectMany(x => x.Characteristics).Where(x => x.ValueHex is not null && x.Uuid != Guid.Empty))
		{
			var text = c.ValueText is { Length: > 0 } t ? $" = {t}" : string.Empty;
			add($"  {ShortId(c.Uuid)} {GattNames.Characteristic(c.Uuid)}", GattStepStatus.Info, $"{c.ValueHex}{text}", 0);
		}
	}

	private static async Task ListenAsync(GattConnection conn, IReadOnlyList<GattServiceInfo> services, GattDiagOptions options, Action<string, GattStepStatus, string, long> add, CancellationToken ct)
	{
		if (options.Listen <= TimeSpan.Zero)
		{
			add("notifications", GattStepStatus.Skip, "disabled", 0);
			return;
		}

		var sw = Stopwatch.StartNew();
		var subscribed = new List<(Guid S, Guid C)>();
		var failed = 0;
		foreach (var s in services)
		{
			foreach (var c in s.Characteristics.Where(x => x.CanNotify))
			{
				if (failed >= 2 && subscribed.Count == 0)
				{
					break;
				}

				try
				{
					await conn.SubscribeAsync(s.Uuid, c.Uuid, ct).ConfigureAwait(false);
					subscribed.Add((s.Uuid, c.Uuid));
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					failed++;
					c.Error = ex.Message;
				}
			}
		}

		if (subscribed.Count == 0)
		{
			add("notifications", failed > 0 ? GattStepStatus.Warn : GattStepStatus.Info, failed > 0 ? $"{failed} subscribe attempts failed" : "no notify/indicate characteristics", sw.ElapsedMilliseconds);
			return;
		}

		try
		{
			await Task.Delay(options.Listen, ct).ConfigureAwait(false);
		}
		finally
		{
			foreach (var (s, c) in subscribed)
			{
				try
				{
					await conn.UnsubscribeAsync(s, c, CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception)
				{
				}
			}
		}

		add("notifications", failed == 0 ? GattStepStatus.Pass : GattStepStatus.Warn, $"subscribed {subscribed.Count}, failed {failed}, listened {options.Listen.TotalSeconds:0.#} s (see report for pushes)", sw.ElapsedMilliseconds);
	}

	private static async Task GaiaProbeAsync(GattConnection conn, IReadOnlyList<GattServiceInfo> services, Action<string, GattStepStatus, string, long> add, CancellationToken ct)
	{
		const string name = "GAIA-over-GATT probe (Q:0000 get API version)";
		var svc = services.FirstOrDefault(s => s.Uuid == GattNames.GaiaService);
		if (svc is null)
		{
			add(name, GattStepStatus.Skip, "no GAIA service", 0);
			return;
		}

		var sw = Stopwatch.StartNew();
		var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
		void Handler(object? _, GattNotification n)
		{
			if (n.Characteristic == GattNames.GaiaResponse)
			{
				tcs.TrySetResult(n.Value);
			}
		}

		conn.Notification += Handler;
		try
		{
			await conn.SubscribeAsync(GattNames.GaiaService, GattNames.GaiaResponse, ct).ConfigureAwait(false);
			await conn.WriteAsync(GattNames.GaiaService, GattNames.GaiaCommand, [0x00, 0x1D, 0x00, 0x00], ct).ConfigureAwait(false);
			var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(3), ct)).ConfigureAwait(false);
			if (done == tcs.Task)
			{
				var v = tcs.Task.Result;
				var good = v.Length >= 4 && v[0] == 0x00 && v[1] == 0x1D && v[2] == 0x01 && v[3] == 0x00;
				add(name, good ? GattStepStatus.Pass : GattStepStatus.Warn, $"response {GattNames.Hex(v)}{(good ? " (expected header)" : " (unexpected header)")}", sw.ElapsedMilliseconds);
			}
			else
			{
				add(name, GattStepStatus.Warn, "no response within 3 s", sw.ElapsedMilliseconds);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			add(name, GattStepStatus.Fail, ex.Message, sw.ElapsedMilliseconds);
		}
		finally
		{
			conn.Notification -= Handler;
			try
			{
				await conn.UnsubscribeAsync(GattNames.GaiaService, GattNames.GaiaResponse, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception)
			{
			}
		}
	}

	private static async Task CyclesAsync(GattTarget target, GattDiagOptions options, Action<string, GattStepStatus, string, long> add, CancellationToken ct)
	{
		var times = new List<long>();
		var failures = new List<string>();
		for (var i = 1; i <= options.Cycles; i++)
		{
			var sw = Stopwatch.StartNew();
			try
			{
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
				linked.CancelAfter(options.ConnectTimeout);
				await using var c = await GattConnection.OpenAsync(target, null, linked.Token).ConfigureAwait(false);
				var s = await c.DiscoverAsync(BluetoothCacheMode.Uncached, linked.Token).ConfigureAwait(false);
				if (s.Count == 0)
				{
					failures.Add($"#{i}: no services");
				}
				else
				{
					times.Add(sw.ElapsedMilliseconds);
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				failures.Add($"#{i}: {ex.Message}");
			}
		}

		var status = failures.Count == 0 ? GattStepStatus.Pass : times.Count == 0 ? GattStepStatus.Fail : GattStepStatus.Warn;
		var timing = times.Count > 0 ? $", connect+discover min/avg/max {times.Min()}/{(long)times.Average()}/{times.Max()} ms" : string.Empty;
		add($"connect/disconnect x{options.Cycles}", status, $"{times.Count}/{options.Cycles} ok{timing}{(failures.Count > 0 ? "; " + string.Join("; ", failures) : string.Empty)}", 0);
	}
}
