using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Momentum5.App.Services;
using Momentum5.Transport.Ble;
using Windows.Devices.Bluetooth;

namespace Momentum5.App.ViewModels;

public sealed class CandidateItem(BleCandidate candidate)
{
	public BleCandidate Candidate { get; } = candidate;

	public string Title => string.IsNullOrWhiteSpace(Candidate.Name) ? "(no name)" : Candidate.Name;

	public string AddressText => Candidate.Address != 0 ? BleAddress.Format(Candidate.Address) : "address unknown (connects by device id)";

	public string Detail =>
		string.Join("  ", new[]
		{
			Candidate.IsLikelyHeadset ? "LIKELY HEADSET" : null,
			Candidate.Rssi is { } r ? $"{r} dBm" : null,
			Candidate.IsPaired ? "paired" : null,
			Candidate.IsConnected ? "connected" : null,
			Candidate.SwiftPairClassicAddress is { } c ? $"Swift Pair → {AppServices.FormatAddress(c)}" : null,
			Candidate.Reason.Length > 0 ? Candidate.Reason : null,
		}.Where(x => x is not null));
}

public sealed class StepItem(GattStep step)
{
	public string Icon { get; } = step.Status switch
	{
		GattStepStatus.Pass => "✔",
		GattStepStatus.Warn => "⚠",
		GattStepStatus.Fail => "✖",
		GattStepStatus.Skip => "–",
		_ => "·",
	};

	public string Title { get; } = step.Name;

	public string Detail { get; } = step.Detail + (step.Ms > 0 ? $"  ({step.Ms} ms)" : string.Empty);
}

public sealed partial class CharItem(GattLabViewModel owner, GattServiceInfo service, GattCharInfo characteristic) : ObservableObject
{
	[ObservableProperty]
	public partial string Value { get; set; } = characteristic.ValueHex is { } h ? h : string.Empty;

	public GattServiceInfo Service { get; } = service;

	public GattCharInfo Characteristic { get; } = characteristic;

	public string ServiceName => $"{Service.Name} ({Service.Uuid})";

	public string Title => $"{(string.IsNullOrEmpty(Characteristic.Name) ? Characteristic.Uuid.ToString() : Characteristic.Name)}  [{Characteristic.Properties}]";

	public Visibility CanRead => Characteristic.CanRead ? Visibility.Visible : Visibility.Collapsed;

	public Visibility CanNotify => Characteristic.CanNotify ? Visibility.Visible : Visibility.Collapsed;

	public IAsyncRelayCommand ReadCommand => field ??= new AsyncRelayCommand(() => owner.ReadAsync(this));

	public IAsyncRelayCommand SubscribeCommand => field ??= new AsyncRelayCommand(() => owner.SubscribeAsync(this));
}

/// <summary>GATT lab: scan, connect, browse, plus the automated diagnostic run. All operations are single-flight and cancellable.</summary>
public sealed partial class GattLabViewModel : ObservableObject, IDisposable
{
	private readonly IUiDispatcher _ui;
	private readonly Lock _gate = new();
	private CancellationTokenSource? _cts;
	private GattConnection? _conn;
	private GattReport? _report;

	public GattLabViewModel(AppServices services)
	{
		ArgumentNullException.ThrowIfNull(services);
		_ui = services.Ui;
		ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
		ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsBusy && !IsConnected && ResolveTarget() is not null);
		DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !IsBusy && IsConnected);
		DiscoverCommand = new AsyncRelayCommand(DiscoverAsync, () => !IsBusy && IsConnected);
		DiagnoseCommand = new AsyncRelayCommand(DiagnoseAsync, () => !IsBusy);
		CancelCommand = new RelayCommand(Cancel, () => IsBusy);
		CopyReportCommand = new RelayCommand(() => ReportRequested?.Invoke(this, GetReportJson() ?? string.Empty), () => _report is not null);
		var hint = TryClassicHint();
		_classicHint = hint;
	}

	private readonly ulong? _classicHint;

	public event EventHandler<string>? ReportRequested;

	public ObservableCollection<CandidateItem> Candidates { get; } = [];

	public ObservableCollection<StepItem> Steps { get; } = [];

	public ObservableCollection<CharItem> Characteristics { get; } = [];

	public ObservableCollection<string> Notifications { get; } = [];

	public IAsyncRelayCommand ScanCommand { get; }

	public IAsyncRelayCommand ConnectCommand { get; }

	public IAsyncRelayCommand DisconnectCommand { get; }

	public IAsyncRelayCommand DiscoverCommand { get; }

	public IAsyncRelayCommand DiagnoseCommand { get; }

	public IRelayCommand CancelCommand { get; }

	public IRelayCommand CopyReportCommand { get; }

	[ObservableProperty]
	public partial bool IsBusy { get; set; }

	[ObservableProperty]
	public partial bool IsConnected { get; set; }

	[ObservableProperty]
	public partial string Status { get; set; } = "Idle. The M5 only advertises in pairing mode: hold the power switch until it pairs, then scan.";

	[ObservableProperty]
	public partial CandidateItem? SelectedCandidate { get; set; }

	[ObservableProperty]
	public partial string ManualAddress { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string ManualAddressHint { get; set; } = string.Empty;

	[ObservableProperty]
	public partial int Cycles { get; set; } = 3;

	[ObservableProperty]
	public partial int ListenSeconds { get; set; } = 5;

	[ObservableProperty]
	public partial bool GaiaProbe { get; set; }

	[ObservableProperty]
	public partial string ConnectedTitle { get; set; } = "Not connected";

	partial void OnIsBusyChanged(bool value) => RefreshCommands();

	partial void OnIsConnectedChanged(bool value) => RefreshCommands();

	partial void OnSelectedCandidateChanged(CandidateItem? value) => RefreshCommands();

	partial void OnManualAddressChanged(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			ManualAddressHint = string.Empty;
		}
		else
		{
			ManualAddressHint = BleAddress.TryParse(value, out var a) ? $"→ {BleAddress.Format(a)}" : "not a valid 48-bit address (e.g. 66:C6:71:65:36:F0)";
		}

		RefreshCommands();
	}

	private void RefreshCommands()
	{
		ScanCommand.NotifyCanExecuteChanged();
		ConnectCommand.NotifyCanExecuteChanged();
		DisconnectCommand.NotifyCanExecuteChanged();
		DiscoverCommand.NotifyCanExecuteChanged();
		DiagnoseCommand.NotifyCanExecuteChanged();
		CancelCommand.NotifyCanExecuteChanged();
		CopyReportCommand.NotifyCanExecuteChanged();
	}

	private static ulong? TryClassicHint()
	{
		try
		{
			var path = System.IO.Path.Combine(Momentum5.Client.ClientPaths.AppData, "last-device.txt");
			if (System.IO.File.Exists(path) && BleAddress.TryParse(System.IO.File.ReadAllText(path).Trim(), out var a))
			{
				return a;
			}
		}
		catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
		{
		}

		return null;
	}

	private GattTarget? ResolveTarget()
	{
		if (BleAddress.TryParse(ManualAddress, out var manual))
		{
			return new GattTarget(manual, null, "manual");
		}

		return SelectedCandidate?.Candidate.ToTarget();
	}

	private async Task RunAsync(string label, Func<CancellationToken, Task> body)
	{
		CancellationTokenSource cts;
		lock (_gate)
		{
			if (IsBusy)
			{
				return;
			}

			_cts = cts = new CancellationTokenSource();
			IsBusy = true;
		}

		Status = label + "…";
		try
		{
			await body(cts.Token);
		}
		catch (OperationCanceledException)
		{
			Status = "Cancelled.";
		}
		catch (Exception ex)
		{
			Status = $"{label} failed: {ex.Message}";
		}
		finally
		{
			lock (_gate)
			{
				_cts = null;
				IsBusy = false;
			}

			cts.Dispose();
		}
	}

	private void Cancel() => _cts?.Cancel();

	private Task ScanAsync() => RunAsync("Scanning (8 s)", async ct =>
	{
		var list = await GattLab.ScanAsync(TimeSpan.FromSeconds(8), _classicHint, ct);
		Candidates.Clear();
		foreach (var c in list)
		{
			Candidates.Add(new CandidateItem(c));
		}

		SelectedCandidate = Candidates.FirstOrDefault(c => c.Candidate.IsLikelyHeadset) ?? Candidates.FirstOrDefault();
		Status = list.Count == 0
			? "No BLE devices seen. Is Bluetooth on?"
			: list.Any(c => c.IsLikelyHeadset) ? $"{list.Count} device(s); headset candidate selected." : $"{list.Count} device(s), none looks like the headset (pairing mode?).";
	});

	private Task ConnectAsync() => RunAsync("Connecting", async ct =>
	{
		var target = ResolveTarget() ?? throw new InvalidOperationException("Pick a device or enter a valid address.");
		await CloseConnectionAsync();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));
		var conn = await GattConnection.OpenAsync(target, null, timeout.Token);
		conn.Notification += OnNotification;
		conn.LinkChanged += OnLinkChanged;
		_conn = conn;
		IsConnected = true;
		ConnectedTitle = $"{conn.Name ?? "(no name)"}  {BleAddress.Format(conn.Address)}";
		Status = "Device object open; discovering (this is what makes Windows connect)…";
		await DiscoverCoreAsync(conn, timeout.Token);
	});

	private Task DisconnectAsync() => RunAsync("Disconnecting", async _ =>
	{
		await CloseConnectionAsync();
		Status = "Disconnected (services released).";
	});

	private Task DiscoverAsync() => RunAsync("Discovering", async ct => await DiscoverCoreAsync(_conn ?? throw new InvalidOperationException("Not connected."), ct));

	private async Task DiscoverCoreAsync(GattConnection conn, CancellationToken ct)
	{
		var services = await conn.DiscoverAsync(BluetoothCacheMode.Uncached, ct);
		Characteristics.Clear();
		foreach (var s in services)
		{
			foreach (var c in s.Characteristics)
			{
				Characteristics.Add(new CharItem(this, s, c));
			}
		}

		Status = services.Count == 0 ? "Connected but no services found." : $"{services.Count} services, {Characteristics.Count} characteristics.";
	}

	internal async Task ReadAsync(CharItem item)
	{
		if (_conn is null)
		{
			return;
		}

		try
		{
			var v = await _conn.ReadAsync(item.Service.Uuid, item.Characteristic.Uuid, CancellationToken.None);
			var text = GattNames.DecodeValue(item.Characteristic.Uuid, v);
			item.Value = GattNames.Hex(v) + (text.Length > 0 ? $"  = {text}" : string.Empty);
		}
		catch (Exception ex)
		{
			item.Value = "error: " + ex.Message;
		}
	}

	internal async Task SubscribeAsync(CharItem item)
	{
		if (_conn is null)
		{
			return;
		}

		try
		{
			await _conn.SubscribeAsync(item.Service.Uuid, item.Characteristic.Uuid, CancellationToken.None);
			item.Value = "subscribed (pushes appear in the log below)";
		}
		catch (Exception ex)
		{
			item.Value = "error: " + ex.Message;
		}
	}

	private Task DiagnoseAsync() => RunAsync("Running diagnostics", async ct =>
	{
		var target = ResolveTarget() ?? throw new InvalidOperationException("Pick a device or enter a valid address first.");
		await CloseConnectionAsync();
		Steps.Clear();
		Notifications.Clear();
		var options = new GattDiagOptions
		{
			Cycles = Math.Clamp(Cycles, 0, 20),
			Listen = TimeSpan.FromSeconds(Math.Clamp(ListenSeconds, 0, 60)),
			GaiaProbe = GaiaProbe,
		};
		var report = await Task.Run(() => GattLab.DiagnoseAsync(
			target,
			options,
			new InlineProgress<GattStep>(step => _ui.Post(() => Steps.Add(new StepItem(step)))),
			n => _ui.Post(() => AddNotification(n)),
			ct), CancellationToken.None);
		_report = report;
		Characteristics.Clear();
		foreach (var s in report.Services)
		{
			foreach (var c in s.Characteristics)
			{
				Characteristics.Add(new CharItem(this, s, c) { Value = c.ValueHex is null ? string.Empty : $"{c.ValueHex}{(string.IsNullOrEmpty(c.ValueText) ? string.Empty : "  = " + c.ValueText)}" });
			}
		}

		Status = $"Diagnostics finished: {report.Summary}.";
		RefreshCommands();
	});

	public string? GetReportJson() =>
		_report is null ? null : JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true });

	private void OnNotification(object? sender, GattNotification n) => _ui.Post(() => AddNotification(n));

	private void AddNotification(GattNotification n)
	{
		Notifications.Insert(0, $"{n.At:HH:mm:ss.fff}  {GattNames.Characteristic(n.Characteristic)}  {GattNames.Hex(n.Value)}");
		while (Notifications.Count > 500)
		{
			Notifications.RemoveAt(Notifications.Count - 1);
		}
	}

	private void OnLinkChanged(object? sender, bool up) => _ui.Post(() =>
	{
		if (_conn is not null && sender == _conn)
		{
			Status = up ? "Link up." : "Link down (device disconnected or out of range).";
		}
	});

	private async Task CloseConnectionAsync()
	{
		var conn = _conn;
		_conn = null;
		if (conn is not null)
		{
			conn.Notification -= OnNotification;
			conn.LinkChanged -= OnLinkChanged;
			await conn.DisposeAsync();
		}

		IsConnected = false;
		ConnectedTitle = "Not connected";
		Characteristics.Clear();
	}

	public void Dispose()
	{
		_cts?.Cancel();
		var conn = _conn;
		_conn = null;
		if (conn is not null)
		{
			conn.Notification -= OnNotification;
			conn.LinkChanged -= OnLinkChanged;
			_ = conn.DisposeAsync().AsTask();
		}
	}
}
