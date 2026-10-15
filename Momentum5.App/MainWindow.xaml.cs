using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Momentum5.Core.Gatt;
using Momentum5.Transport.Ble;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Momentum5.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
	private readonly IGattExplorer _transport = new GattExplorer();

	private bool _isBusy;
	private string _statusText = "Ready";
	private DeviceViewModel? _selectedDevice;
	private string _deviceSummaryText = "No scan yet.";
	private string _selectedDeviceDetails = "No device selected.";
	private string _serviceCountText = "Services: 0";
	private string _notificationCountText = "Notifications: 0";
	private string _logSummaryText = "0 lines";

	private readonly Dictionary<(Guid ServiceUuid, Guid CharacteristicUuid), List<string>> _notificationHistory = [];

	private string _rawLogText = string.Empty;

	public string RawLogText
	{
		get => _rawLogText;
		private set
		{
			if (_rawLogText != value)
			{
				_rawLogText = value;
				OnPropertyChanged();
			}
		}
	}

	private string _targetAddressText = "Target: none";

	public string TargetAddressText
	{
		get => _targetAddressText;
		private set
		{
			if (_targetAddressText != value)
			{
				_targetAddressText = value;
				OnPropertyChanged();
			}
		}
	}

	private int _notificationCount;

	public ObservableCollection<DeviceViewModel> Devices { get; } = new();
	public ObservableCollection<ServiceViewModel> Services { get; } = new();
	public ObservableCollection<NoteViewModel> Notes { get; } = new();
	public ObservableCollection<LogViewModel> Logs { get; } = new();

	public event PropertyChangedEventHandler? PropertyChanged;

	public MainWindow()
	{
		InitializeComponent();
		AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960));

		_transport.LogLine += Transport_LogLine;
		_transport.DeviceSeen += Transport_DeviceSeen;
		_transport.NotificationReceived += Transport_NotificationReceived;

		SetStatus("Ready");
		UpdateDeviceSummary("No scan yet.");
		UpdateSelectedDeviceDetails("No device selected.");
		UpdateServiceCount(0);
		UpdateNotificationCount(0);
		UpdateLogSummary();
		RefreshTargetUi();
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (_isBusy != value)
			{
				_isBusy = value;
				OnPropertyChanged();
			}
		}
	}

	public string StatusText
	{
		get => _statusText;
		private set
		{
			if (_statusText != value)
			{
				_statusText = value;
				OnPropertyChanged();
			}
		}
	}

	public DeviceViewModel? SelectedDevice
	{
		get => _selectedDevice;
		set
		{
			if (_selectedDevice != value)
			{
				_selectedDevice = value;
				OnPropertyChanged();
				RefreshSelectedDeviceDetails();
				RefreshTargetUi();
			}
		}
	}

	public string DeviceSummaryText
	{
		get => _deviceSummaryText;
		private set
		{
			if (_deviceSummaryText != value)
			{
				_deviceSummaryText = value;
				OnPropertyChanged();
			}
		}
	}

	public string SelectedDeviceDetails
	{
		get => _selectedDeviceDetails;
		private set
		{
			if (_selectedDeviceDetails != value)
			{
				_selectedDeviceDetails = value;
				OnPropertyChanged();
			}
		}
	}

	public string ServiceCountText
	{
		get => _serviceCountText;
		private set
		{
			if (_serviceCountText != value)
			{
				_serviceCountText = value;
				OnPropertyChanged();
			}
		}
	}

	public string NotificationCountText
	{
		get => _notificationCountText;
		private set
		{
			if (_notificationCountText != value)
			{
				_notificationCountText = value;
				OnPropertyChanged();
			}
		}
	}

	public string LogSummaryText
	{
		get => _logSummaryText;
		private set
		{
			if (_logSummaryText != value)
			{
				_logSummaryText = value;
				OnPropertyChanged();
			}
		}
	}

	private async void Window_Activated(object sender, WindowActivatedEventArgs args)
	{
		await Task.Yield();
		AddNote("Window ready. Scan to begin discovery.");
	}

	private void RefreshTargetUi()
	{
		if (TryGetTargetAddress(out var address, out var sourceLabel, out var errorText))
		{
			TargetAddressText = $"Target: 0x{address:X} ({sourceLabel})";
		}
		else
		{
			TargetAddressText = errorText ?? "Target: none";
		}
	}

	private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		RefreshTargetUi();
	}

	private async void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key != VirtualKey.Enter)
		{
			return;
		}

		e.Handled = true;

		if (!TryGetTargetAddress(out var address, out var sourceLabel, out var errorText))
		{
			TargetAddressText = errorText;
			AddNote(errorText);
			return;
		}

		await RunBusyAsync("Connecting...", async ct =>
		{
			AddNote($"Connecting to {sourceLabel}.");
			await _transport.ConnectAsync(address, ct);

			if (SelectedDevice is not null)
			{
				SetStatus($"Connected: {SelectedDevice.DisplayName}");
				UpdateSelectedDeviceDetails(SelectedDevice.ConnectionSummary);
			}
			else
			{
				SetStatus($"Connected: 0x{address:X}");
				UpdateSelectedDeviceDetails($"Manual target\nAddress: 0x{address:X}");
			}
		});
	}

	private bool TryGetTargetAddress(out ulong address, out string sourceLabel, out string? errorText)
	{
		sourceLabel = string.Empty;
		errorText = null;

		var text = AddressBox.Text?.Trim();
		if (!string.IsNullOrWhiteSpace(text))
		{
			if (TryParseBluetoothAddress(text, out address))
			{
				sourceLabel = $"manual address {text}";
				return true;
			}

			address = 0;
			errorText = $"Target: invalid address '{text}'";
			return false;
		}

		if (SelectedDevice is not null)
		{
			address = SelectedDevice.BluetoothAddress;
			sourceLabel = SelectedDevice.DisplayName;
			return true;
		}

		address = 0;
		errorText = "Target: none";
		return false;
	}

	private static bool TryParseBluetoothAddress(string input, out ulong address)
	{
		address = 0;

		// Remove common separators and prefixes.
		var cleaned = input.Trim();

		if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned[2..];
		}

		cleaned = Regex.Replace(cleaned, @"[^0-9a-fA-F]", "");

		// Bluetooth address should be 12 hex digits, but allow shorter values if user typed them.
		if (cleaned.Length == 0 || cleaned.Length > 16)
		{
			return false;
		}

		return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
	}

	/*	App-specific scan code that works kinda wonkily with the Windows Bluetooth stack, so it's commented out for now.
	 *	private async void Scan_Click(object sender, RoutedEventArgs e)
		{
			await RunBusyAsync("Scanning...", async ct =>
			{
				Devices.Clear();
				Services.Clear();
				SelectedDevice = null;
				UpdateServiceCount(0);
				UpdateSelectedDeviceDetails("No device selected.");

				AddNote("Scanning for Bluetooth LE devices.");

				var results = await _transport.ScanAsync(TimeSpan.FromSeconds(6), ct);

				foreach (var item in results
							 .OrderByDescending(d => d.Rssi ?? int.MinValue)
							 .ThenBy(d => d.Name ?? string.Empty))
				{
					Devices.Add(new DeviceViewModel(item));
				}

				UpdateDeviceSummary($"{Devices.Count} device(s) discovered.");
				SetStatus($"Scan complete: {Devices.Count} device(s).");

				AutoPickMomentumDevice();
			});
		} */

	private async void Scan_Click(object sender, RoutedEventArgs e)
	{
		await RunBusyAsync("Enumerating...", async ct =>
		{
			Devices.Clear();

			var devices = await GattExplorer.GetConnectedKnownLeDevicesAsync();

			foreach (var d in devices)
			{
				Devices.Add(new DeviceViewModel(new BleDeviceSnapshot(
					d.Id,
					0,
					string.IsNullOrWhiteSpace(d.Name) ? null : d.Name,
					null,
					d.IsPaired,
					d.IsConnected)));
			}

			UpdateDeviceSummary($"{Devices.Count} connected device(s) found.");
			SetStatus($"Found {Devices.Count} connected device(s).");
		});
	}

	private async void Probe_Click(object sender, RoutedEventArgs e)
	{
		await RunBusyAsync("Probing...", async ct =>
		{
			Devices.Clear();
			Services.Clear();
			SelectedDevice = null;
			UpdateServiceCount(0);
			UpdateSelectedDeviceDetails("No device selected.");

			AddNote("Probe sequence started.");
			var results = await _transport.ScanAsync(TimeSpan.FromSeconds(6), ct);

			foreach (var item in results
						 .OrderByDescending(d => d.Rssi ?? int.MinValue)
						 .ThenBy(d => d.Name ?? string.Empty))
			{
				Devices.Add(new DeviceViewModel(item));
			}

			AutoPickMomentumDevice();

			if (SelectedDevice is null)
			{
				throw new InvalidOperationException("No likely MOMENTUM device found.");
			}

			await ConnectSelectedAsync(ct);
			await EnumerateSelectedAsync(ct);

			AddNote("Probe sequence complete.");
			SetStatus("Probe complete.");
		});
	}

	private async void Connect_Click(object sender, RoutedEventArgs e)
	{
		await RunBusyAsync("Connecting...", async ct => await ConnectSelectedAsync(ct));
	}

	private async void Enumerate_Click(object sender, RoutedEventArgs e)
	{
		await RunBusyAsync("Enumerating...", async ct => await EnumerateSelectedAsync(ct));
	}

	private async void Disconnect_Click(object sender, RoutedEventArgs e)
	{
		await RunBusyAsync("Disconnecting...", async ct =>
		{
			await _transport.DisconnectAsync(ct);
			Services.Clear();
			UpdateServiceCount(0);
			AddNote("Disconnected.");
			SetStatus("Disconnected");
		});
	}

	private void Clear_Click(object sender, RoutedEventArgs e)
	{
		Devices.Clear();
		Services.Clear();
		Notes.Clear();
		Logs.Clear();
		SelectedDevice = null;
		UpdateDeviceSummary("Cleared.");
		UpdateSelectedDeviceDetails("No device selected.");
		UpdateServiceCount(0);
		UpdateNotificationCount(0);
		UpdateLogSummary();
		SetStatus("Cleared");
	}

	private void DevicesList_ItemClick(object sender, ItemClickEventArgs e)
	{
		if (e.ClickedItem is DeviceViewModel device)
		{
			SelectedDevice = device;
		}
	}

	private async Task ConnectSelectedAsync(CancellationToken ct)
	{
		var manualText = AddressBox.Text?.Trim();

		if (!string.IsNullOrWhiteSpace(manualText))
		{
			if (!TryParseBluetoothAddress(manualText, out var address))
			{
				throw new InvalidOperationException($"Invalid Bluetooth address: '{manualText}'");
			}

			AddNote($"Connecting to manual address {manualText}.");
			await _transport.ConnectAsync(address, ct);
		}
		else if (SelectedDevice is not null && !string.IsNullOrWhiteSpace(SelectedDevice.DeviceId))
		{
			AddNote($"Connecting to {SelectedDevice.DisplayName}.");
			await _transport.ConnectByIdAsync(SelectedDevice.DeviceId, ct);
		}
		else if (SelectedDevice is not null)
		{
			AddNote($"Connecting to {SelectedDevice.DisplayName} by address.");
			await _transport.ConnectAsync(SelectedDevice.BluetoothAddress, ct);
		}
		else
		{
			throw new InvalidOperationException("Select a device or enter a Bluetooth address first.");
		}

		if (SelectedDevice is not null)
		{
			SetStatus($"Connected: {SelectedDevice.DisplayName}");
			UpdateSelectedDeviceDetails(SelectedDevice.ConnectionSummary);
		}
	}

	private async Task EnumerateSelectedAsync(CancellationToken ct)
	{
		if (!TryGetTargetAddress(out _, out _, out _))
		{
			throw new InvalidOperationException("Select a device or enter a Bluetooth address first.");
		}

		AddNote("Enumerating GATT services.");
		var services = await _transport.EnumerateAsync(ct);

		Services.Clear();

		foreach (var service in services.OrderBy(s => s.ServiceUuid))
		{
			var vm = new ServiceViewModel(service);

			foreach (var characteristic in service.Characteristics.OrderBy(c => c.CharacteristicUuid))
			{
				vm.Characteristics.Add(new CharacteristicViewModel(characteristic));
			}

			Services.Add(vm);
		}

		UpdateServiceCount(Services.Count);
		AddNote($"Enumerated {Services.Count} service(s).");

		// Subscribe to all notify-capable characteristics so the log becomes useful immediately.
		foreach (var service in services)
		{
			foreach (var characteristic in service.Characteristics.Where(c => c.CanNotify))
			{
				try
				{
					await _transport.SubscribeAsync(service.ServiceUuid, characteristic.CharacteristicUuid, ct);
					AddNote($"Subscribed: {characteristic.CharacteristicUuid}");
				}
				catch (Exception ex)
				{
					AddLog($"Subscribe failed for {characteristic.CharacteristicUuid}: {ex.Message}");
				}
			}
		}
	}

	private void AutoPickMomentumDevice()
	{
		var preferred =
			Devices.FirstOrDefault(d => d.DisplayName.Contains("MOMENTUM 5", StringComparison.OrdinalIgnoreCase))
			?? Devices.FirstOrDefault(d => d.DisplayName.Contains("MOMENTUM", StringComparison.OrdinalIgnoreCase))
			?? Devices.FirstOrDefault();

		if (preferred is not null)
		{
			SelectedDevice = preferred;
			AddNote($"Selected: {preferred.DisplayName}");
		}
	}

	private void RefreshSelectedDeviceDetails()
	{
		if (SelectedDevice is null)
		{
			UpdateSelectedDeviceDetails("No device selected.");
			return;
		}

		UpdateSelectedDeviceDetails(SelectedDevice.ConnectionSummary);
	}

	private async Task RunBusyAsync(string status, Func<CancellationToken, Task> action)
	{
		if (IsBusy)
		{
			return;
		}

		IsBusy = true;
		SetStatus(status);

		using var cts = new CancellationTokenSource();
		try
		{
			await action(cts.Token);
		}
		catch (Exception ex)
		{
			AddLog(ex.ToString());
			AddNote($"Error: {ex.Message}");
			SetStatus("Error");
		}
		finally
		{
			IsBusy = false;
			UpdateLogSummary();
		}
	}

	private void Transport_DeviceSeen(object? sender, BleDeviceSnapshot e)
	{
		Enqueue(() =>
		{
			if (Devices.All(d => d.BluetoothAddress != e.BluetoothAddress))
			{
				Devices.Add(new DeviceViewModel(e));
				UpdateDeviceSummary($"{Devices.Count} device(s) discovered.");
			}

			AddLog($"Seen: {e.Name ?? "<unnamed>"} 0x{e.BluetoothAddress:X} RSSI={e.Rssi?.ToString() ?? "?"}");
		});
	}

	private void Transport_NotificationReceived(object? sender, (Guid ServiceUuid, Guid CharacteristicUuid, byte[] Value) e)
	{
		var hex = BitConverter.ToString(e.Value);
		var stamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff");

		Enqueue(() =>
		{
			var key = (e.ServiceUuid, e.CharacteristicUuid);

			if (!_notificationHistory.TryGetValue(key, out var list))
			{
				list = new List<string>();
				_notificationHistory[key] = list;
			}

			list.Add($"{stamp} {hex}");
			if (list.Count > 25)
			{
				list.RemoveAt(0);
			}

			_notificationCount++;
			UpdateNotificationCount(_notificationCount);

			AddNote($"Notify {e.ServiceUuid}/{e.CharacteristicUuid}");
			AddLog($"Notify {e.ServiceUuid}/{e.CharacteristicUuid}: {hex}");
		});
	}

	private void Transport_LogLine(object? sender, string e)
	{
		Enqueue(() => AddLog(e));
	}

	private void AddNote(string message)
	{
		Notes.Add(new NoteViewModel(DateTimeOffset.Now, message));
		if (Notes.Count > 200)
		{
			Notes.RemoveAt(0);
		}
	}

	private void AddLog(string message)
	{
		var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}";
		RawLogText += line + Environment.NewLine;

		if (Logs.Count > 800)
		{
			Logs.RemoveAt(0);
		}

		Logs.Add(new LogViewModel(DateTimeOffset.Now, message));
		if (Logs.Count > 800)
		{
			Logs.RemoveAt(0);
		}

		UpdateLogSummary();
	}

	private string BuildGattDumpText()
	{
		var sb = new StringBuilder();

		sb.AppendLine("Momentum5 GATT dump");
		sb.AppendLine($"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
		sb.AppendLine();

		if (SelectedDevice is not null)
		{
			sb.AppendLine("Target");
			sb.AppendLine($"  Name: {SelectedDevice.DisplayName}");
			sb.AppendLine($"  Address: 0x{SelectedDevice.BluetoothAddress:X}");
			sb.AppendLine($"  RSSI: {SelectedDevice.Rssi?.ToString() ?? "?"}");
			sb.AppendLine($"  Paired: {SelectedDevice.IsPaired}");
			sb.AppendLine($"  Connected: {SelectedDevice.IsConnected}");
			sb.AppendLine();
		}

		foreach (var service in Services)
		{
			sb.AppendLine($"Service {service.ServiceUuid}");

			foreach (var ch in service.Characteristics)
			{
				sb.AppendLine($"  Characteristic {ch.CharacteristicUuid}");
				sb.AppendLine($"    Properties: {ch.Properties}");
				sb.AppendLine($"    Read: {ch.CanRead}");
				sb.AppendLine($"    Write: {ch.CanWrite}");
				sb.AppendLine($"    Notify: {ch.CanNotify}");
				sb.AppendLine($"    ValueLength: {ch.ValueLength?.ToString() ?? "?"}");
			}

			sb.AppendLine();
		}

		return sb.ToString();
	}

	private async Task<string> BuildGattStateDumpTextAsync()
	{
		var sb = new StringBuilder();

		sb.AppendLine("Momentum5 GATT state dump");
		sb.AppendLine($"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
		sb.AppendLine();

		if (SelectedDevice is not null)
		{
			sb.AppendLine("Target");
			sb.AppendLine($"  Name: {SelectedDevice.DisplayName}");
			sb.AppendLine($"  Address: 0x{SelectedDevice.BluetoothAddress:X}");
			sb.AppendLine($"  RSSI: {SelectedDevice.Rssi?.ToString() ?? "?"}");
			sb.AppendLine($"  Paired: {SelectedDevice.IsPaired}");
			sb.AppendLine($"  Connected: {SelectedDevice.IsConnected}");
			sb.AppendLine();
		}

		foreach (var service in Services)
		{
			sb.AppendLine($"Service {service.ServiceUuid}");

			foreach (var ch in service.Characteristics)
			{
				sb.AppendLine($"  Characteristic {ch.CharacteristicUuid}");
				sb.AppendLine($"    Properties: {ch.Properties}");
				sb.AppendLine($"    Read: {ch.CanRead}");
				sb.AppendLine($"    Write: {ch.CanWrite}");
				sb.AppendLine($"    Notify: {ch.CanNotify}");
				sb.AppendLine($"    ValueLength: {ch.ValueLength?.ToString() ?? "?"}");

				if (ch.CanRead)
				{
					try
					{
						var value = await _transport.ReadAsync(service.ServiceUuid, ch.CharacteristicUuid, CancellationToken.None);
						sb.AppendLine($"    Value: {FormatBytes(value)}");
					}
					catch (Exception ex)
					{
						sb.AppendLine($"    Value: <read failed: {ex.Message}>");
					}
				}
				else
				{
					sb.AppendLine("    Value: <not readable>");
				}

				if (_notificationHistory.TryGetValue((service.ServiceUuid, ch.CharacteristicUuid), out var notes) && notes.Count > 0)
				{
					sb.AppendLine("    Notifications:");
					foreach (var note in notes)
					{
						sb.AppendLine($"      {note}");
					}
				}
			}

			sb.AppendLine();
		}

		return sb.ToString();
	}

	private static string FormatBytes(byte[]? data)
	{
		if (data is null || data.Length == 0)
		{
			return "<empty>";
		}

		return BitConverter.ToString(data);
	}

	private async void SaveGattDump_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (Services.Count == 0)
			{
				AddNote("No GATT data to save.");
				return;
			}

			var picker = new FileSavePicker
			{
				SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
				SuggestedFileName = $"Momentum5_GATT_{DateTime.Now:yyyyMMdd_HHmmss}"
			};
			picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });

			InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

			StorageFile? file = await picker.PickSaveFileAsync();
			if (file is null)
			{
				AddNote("Save cancelled.");
				return;
			}

			await FileIO.WriteTextAsync(file, await BuildGattStateDumpTextAsync());
			AddNote($"Saved GATT dump: {file.Name}");
			SetStatus("Dump saved");
		}
		catch (Exception ex)
		{
			AddLog($"Save GATT dump failed: {ex.Message}");
			SetStatus("Save failed");
		}
	}

	private async void CaptureGattDump_Click(object sender, RoutedEventArgs e)
	{
		await RunBusyAsync("Capturing...", async ct =>
		{
			_notificationHistory.Clear();
			_notificationCount = 0;
			UpdateNotificationCount(0);

			AddNote("Capture started. Waiting 15 seconds...");
			SetStatus("Capturing 15s...");

			await Task.Delay(TimeSpan.FromSeconds(15), ct);

			var picker = new FileSavePicker
			{
				SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
				SuggestedFileName = $"Momentum5_GATT_{DateTime.Now:yyyyMMdd_HHmmss}_15s"
			};
			picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });

			InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

			StorageFile? file = await picker.PickSaveFileAsync();
			if (file is null)
			{
				AddNote("Save cancelled.");
				return;
			}

			await FileIO.WriteTextAsync(file, await BuildGattStateDumpTextAsync());
			AddNote($"Saved 15s capture: {file.Name}");
			SetStatus("Capture saved");
		});
	}

	private void UpdateDeviceSummary(string text) => DeviceSummaryText = text;
	private void UpdateSelectedDeviceDetails(string text) => SelectedDeviceDetails = text;
	private void UpdateServiceCount(int count) => ServiceCountText = $"Services: {count}";
	private void UpdateNotificationCount(int count) => NotificationCountText = $"Notifications: {count}";
	private void UpdateLogSummary() => LogSummaryText = $"{Logs.Count} lines";

	private void SetStatus(string text) => StatusText = text;

	private void Enqueue(Action action)
	{
		if (action is null)
		{
			return;
		}

		var queue = DispatcherQueue;
		if (queue is null)
		{
			return;
		}

		if (queue.HasThreadAccess)
		{
			action();
			return;
		}

		_ = queue.TryEnqueue(() =>
		{
			action();
		});
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	private async void Window_Closed(object sender, WindowEventArgs args)
	{
		_transport.LogLine -= Transport_LogLine;
		_transport.DeviceSeen -= Transport_DeviceSeen;
		_transport.NotificationReceived -= Transport_NotificationReceived;
		await _transport.DisconnectAsync(CancellationToken.None);
		await _transport.DisposeAsync();
	}
}

public sealed class DeviceViewModel
{
	public DeviceViewModel(BleDeviceSnapshot snapshot)
	{
		DeviceId = snapshot.DeviceId;
		BluetoothAddress = snapshot.BluetoothAddress;
		Name = snapshot.Name;
		Rssi = snapshot.Rssi;
		IsPaired = snapshot.IsPaired;
		IsConnected = snapshot.IsConnected;
	}

	public string DeviceId { get; }
	public ulong BluetoothAddress { get; }
	public string? Name { get; }
	public short? Rssi { get; }
	public bool IsPaired { get; }
	public bool IsConnected { get; }

	public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Device 0x{BluetoothAddress:X}" : Name!;
	public string SecondaryLine =>
		$"0x{BluetoothAddress:X}  •  {(IsPaired ? "paired" : "unpaired")}  •  {(IsConnected ? "connected" : "disconnected")}";

	public string ConnectionSummary =>
		$"{DisplayName}\nId: {DeviceId}\nAddress: 0x{BluetoothAddress:X}\nPaired: {IsPaired}\nConnected: {IsConnected}";
}

public sealed class ServiceViewModel
{
	public ServiceViewModel(GattServiceSnapshot snapshot)
	{
		ServiceUuid = snapshot.ServiceUuid;
	}

	public Guid ServiceUuid { get; }
	public ObservableCollection<CharacteristicViewModel> Characteristics { get; } = new();

	public string UuidText => ServiceUuid.ToString();
	public string Summary => $"Service {ServiceUuid}";
}

public sealed class CharacteristicViewModel
{
	public CharacteristicViewModel(GattCharacteristicSnapshot snapshot)
	{
		ServiceUuid = snapshot.ServiceUuid;
		CharacteristicUuid = snapshot.CharacteristicUuid;
		Properties = snapshot.Properties;
		CanRead = snapshot.CanRead;
		CanWrite = snapshot.CanWrite;
		CanNotify = snapshot.CanNotify;
		ValueLength = snapshot.ValueLength;
	}

	public Guid ServiceUuid { get; }
	public Guid CharacteristicUuid { get; }
	public string Properties { get; }
	public bool CanRead { get; }
	public bool CanWrite { get; }
	public bool CanNotify { get; }
	public int? ValueLength { get; }

	public string Summary => $"Characteristic {CharacteristicUuid}";
	public string DetailLine => $"{Properties}  •  R:{CanRead} W:{CanWrite} N:{CanNotify}  •  Len:{ValueLength?.ToString() ?? "?"}";
}

public sealed class NoteViewModel
{
	public NoteViewModel(DateTimeOffset timestamp, string message)
	{
		Timestamp = timestamp;
		Message = message;
	}

	public DateTimeOffset Timestamp { get; }
	public string Message { get; }
	public string TimestampText => Timestamp.ToString("HH:mm:ss");
}

public sealed class LogViewModel
{
	public LogViewModel(DateTimeOffset timestamp, string message)
	{
		Timestamp = timestamp;
		Message = message;
	}

	public DateTimeOffset Timestamp { get; }
	public string Message { get; }
	public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");
}