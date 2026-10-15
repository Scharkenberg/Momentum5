// Momentum5.Transport.Ble/GattExplorer.cs
using Momentum5.Core.Gatt;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Momentum5.Transport.Ble;

public sealed class GattExplorer : IGattExplorer
{
	private BluetoothLEDevice? _device;
	private readonly Dictionary<(Guid Service, Guid Characteristic), GattCharacteristic> _subscriptions = new();

	public event EventHandler<string>? LogLine;
	public event EventHandler<BleDeviceSnapshot>? DeviceSeen;
	public event EventHandler<(Guid ServiceUuid, Guid CharacteristicUuid, byte[] Value)>? NotificationReceived;

	private static ulong ParseBluetoothAddress(string? addressText)
	{
		if (string.IsNullOrWhiteSpace(addressText))
		{
			return 0;
		}

		var cleaned = Regex.Replace(addressText, @"[^0-9a-fA-F]", "");

		return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
			? address
			: 0;
	}

	public async Task<IReadOnlyList<BleDeviceSnapshot>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken)
	{
		string[] requestedProperties =
		{
		"System.Devices.Aep.DeviceAddress",
		"System.Devices.Aep.IsConnected",
		"System.Devices.Aep.IsPaired"
		};

		var devices = await DeviceInformation.FindAllAsync(
			BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
			requestedProperties,
			DeviceInformationKind.AssociationEndpoint);

		var results = new List<BleDeviceSnapshot>();

		foreach (var d in devices)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var addressText =
				d.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addrObj) ? addrObj as string : null;

			var isConnected =
				d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connObj) &&
				connObj is bool b &&
				b;

			var isPaired = d.Pairing?.IsPaired ?? false;

			var snapshot = new BleDeviceSnapshot(
				d.Id,
				ParseBluetoothAddress(addressText),
				string.IsNullOrWhiteSpace(d.Name) ? null : d.Name,
				null,
				isPaired,
				isConnected);

			results.Add(snapshot);
			DeviceSeen?.Invoke(this, snapshot);
			LogLine?.Invoke(this, $"Known LE device: {snapshot.Name ?? "<unnamed>"} id={snapshot.DeviceId}");
		}

		return results;
	}

	public async Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
	{
		await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

		_device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
		if (_device is null)
		{
			throw new InvalidOperationException($"Unable to connect to 0x{bluetoothAddress:X}.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		LogLine?.Invoke(this, $"Connected to {_device.Name ?? "<unnamed>"}");
	}

	public async Task ConnectByIdAsync(string deviceId, CancellationToken cancellationToken)
	{
		await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

		_device = await BluetoothLEDevice.FromIdAsync(deviceId);
		if (_device is null)
		{
			throw new InvalidOperationException($"Unable to connect to device id {deviceId}.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		LogLine?.Invoke(this, $"Connected to {_device.Name ?? "<unnamed>"} via DeviceInformation.Id");
	}

	public Task DisconnectAsync(CancellationToken cancellationToken)
	{
		foreach (var characteristic in _subscriptions.Values)
		{
			characteristic.ValueChanged -= OnValueChanged;
		}

		_subscriptions.Clear();
		_device?.Dispose();
		_device = null;
		LogLine?.Invoke(this, "Disconnected");
		return Task.CompletedTask;
	}

	public async Task<IReadOnlyList<GattServiceSnapshot>> EnumerateAsync(CancellationToken cancellationToken)
	{
		var device = RequireDevice();

		// Give the link a moment to settle after connect.
		await Task.Delay(500, cancellationToken);

		var servicesResult = await GetGattServicesWithRetryAsync(device, cancellationToken);
		var services = new List<GattServiceSnapshot>();

		foreach (var service in servicesResult.Services)
		{
			cancellationToken.ThrowIfCancellationRequested();

			using (service)
			{
				var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
				var chars = new List<GattCharacteristicSnapshot>();

				foreach (var ch in charsResult.Characteristics)
				{
					chars.Add(new GattCharacteristicSnapshot(
						service.Uuid,
						ch.Uuid,
						ch.CharacteristicProperties.ToString(),
						ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read),
						ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
						ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse),
						ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
						ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate),
						null));
				}

				services.Add(new GattServiceSnapshot(service.Uuid, chars));
			}
		}

		return services;
	}

	private async Task<GattDeviceServicesResult> GetGattServicesWithRetryAsync(
		BluetoothLEDevice device,
		CancellationToken cancellationToken)
	{
		const int maxAttempts = 4;

		for (var attempt = 1; attempt <= maxAttempts; attempt++)
		{
			var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);

			if (result.Status == GattCommunicationStatus.Success)
			{
				return result;
			}

			if (result.Status != GattCommunicationStatus.Unreachable || attempt == maxAttempts)
			{
				throw new InvalidOperationException($"GetGattServicesAsync failed: {result.Status}");
			}

			LogLine?.Invoke(this, $"GetGattServicesAsync unreachable, retry {attempt}/{maxAttempts}...");
			await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
		}

		throw new InvalidOperationException("GetGattServicesAsync failed after retries.");
	}

	public async Task<byte[]?> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
	{
		var ch = await GetCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);
		var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);

		if (read.Status != GattCommunicationStatus.Success)
		{
			throw new InvalidOperationException($"Read failed: {read.Status}");
		}

		return ToArray(read.Value);
	}

	public async Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken)
	{
		var ch = await GetCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);
		using var writer = new DataWriter();
		writer.WriteBytes(data);

		var option = withResponse
			? GattWriteOption.WriteWithResponse
			: GattWriteOption.WriteWithoutResponse;

		var status = await ch.WriteValueAsync(writer.DetachBuffer(), option);
		if (status != GattCommunicationStatus.Success)
		{
			throw new InvalidOperationException($"Write failed: {status}");
		}
	}

	public async Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
	{
		var ch = await GetCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);

		var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
			GattClientCharacteristicConfigurationDescriptorValue.Notify);

		if (status != GattCommunicationStatus.Success)
		{
			throw new InvalidOperationException($"Subscribe failed: {status}");
		}

		ch.ValueChanged -= OnValueChanged;
		ch.ValueChanged += OnValueChanged;
		_subscriptions[(serviceUuid, characteristicUuid)] = ch;
	}

	public async Task UnsubscribeAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
	{
		var key = (serviceUuid, characteristicUuid);
		if (_subscriptions.TryGetValue(key, out var ch))
		{
			ch.ValueChanged -= OnValueChanged;
			_subscriptions.Remove(key);

			await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
				GattClientCharacteristicConfigurationDescriptorValue.None);
		}
	}

	public ValueTask DisposeAsync()
	{
		_device?.Dispose();
		_device = null;
		return ValueTask.CompletedTask;
	}

	private BluetoothLEDevice RequireDevice()
		=> _device ?? throw new InvalidOperationException("Not connected.");


	public static async Task<IReadOnlyList<(string Id, string? Name, string Address, bool IsConnected, bool IsPaired)>> GetConnectedKnownLeDevicesAsync()
	{
		string[] requestedProperties =
		{
		"System.Devices.Aep.DeviceAddress",
		"System.Devices.Aep.IsConnected",
		"System.Devices.Aep.IsPaired"
	};

		var devices = await DeviceInformation.FindAllAsync(
			BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
			requestedProperties,
			DeviceInformationKind.AssociationEndpoint);

		var result = new List<(string Id, string? Name, string Address, bool IsConnected, bool IsPaired)>();

		foreach (var d in devices)
		{
			var address =
				d.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addrObj) && addrObj is string s
					? s
					: string.Empty;

			var isConnected =
				d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connObj) && connObj is bool b && b;

			var isPaired =
				d.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var pairedObj) && pairedObj is bool p && p;

			result.Add((d.Id, d.Name, address, isConnected, isPaired));
		}

		return result;
	}

	private async Task<GattCharacteristic> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken)
	{
		var device = RequireDevice();

		var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
		var service = servicesResult.Services.FirstOrDefault(s => s.Uuid == serviceUuid)
			?? throw new InvalidOperationException($"Service not found: {serviceUuid}");

		var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
		var ch = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == characteristicUuid)
			?? throw new InvalidOperationException($"Characteristic not found: {characteristicUuid}");

		cancellationToken.ThrowIfCancellationRequested();
		return ch;
	}

	private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
	{
		var value = ToArray(args.CharacteristicValue);
		NotificationReceived?.Invoke(this, (sender.Service.Uuid, sender.Uuid, value));
		LogLine?.Invoke(this, $"Notify {sender.Service.Uuid}/{sender.Uuid}: {BitConverter.ToString(value)}");
	}

	private static byte[] ToArray(IBuffer buffer)
	{
		var reader = DataReader.FromBuffer(buffer);
		var bytes = new byte[buffer.Length];
		reader.ReadBytes(bytes);
		return bytes;
	}
}