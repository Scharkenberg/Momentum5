using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Momentum5.Transport.Ble;

public sealed class GattOperationException(string message) : Exception(message);

/// <summary>
/// One BLE link. Fixes the flaky connect/disconnect of the old explorer: operations are serialized, a <see cref="GattSession"/> keeps the link
/// while open, discovered services stay referenced until <see cref="DisposeAsync"/> (undisposed services keep Windows from dropping the link),
/// and disposal unsubscribes, releases every WinRT object and waits briefly so an immediate reconnect starts clean.
/// </summary>
public sealed class GattConnection : IAsyncDisposable
{
	private readonly BluetoothLEDevice _device;
	private readonly GattSession? _session;
	private readonly Action<string>? _log;
	private readonly SemaphoreSlim _io = new(1, 1);
	private readonly List<GattDeviceService> _services = [];
	private readonly Dictionary<(Guid Service, Guid Characteristic), GattCharacteristic> _chars = [];
	private readonly HashSet<(Guid Service, Guid Characteristic)> _subscribed = [];
	private bool _disposed;

	private GattConnection(BluetoothLEDevice device, GattSession? session, Action<string>? log)
	{
		_device = device;
		_session = session;
		_log = log;
		_device.ConnectionStatusChanged += OnConnectionStatusChanged;
		if (_session is not null)
		{
			_session.SessionStatusChanged += OnSessionStatusChanged;
		}
	}

	public event EventHandler<bool>? LinkChanged;

	public event EventHandler<GattNotification>? Notification;

	public string? Name => string.IsNullOrWhiteSpace(_device.Name) ? null : _device.Name;

	public ulong Address => _device.BluetoothAddress;

	public string DeviceId => _device.DeviceId;

	public bool IsPaired => _device.DeviceInformation.Pairing.IsPaired;

	public bool IsLinkUp => _device.ConnectionStatus == BluetoothConnectionStatus.Connected || _session?.SessionStatus == GattSessionStatus.Active;

	public ushort? MaxPduSize => _session?.MaxPduSize;

	public static async Task<GattConnection> OpenAsync(GattTarget target, Action<string>? log, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(target);
		BluetoothLEDevice? device;
		if (target.Address is { } address and not 0)
		{
			device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false);
		}
		else if (!string.IsNullOrEmpty(target.DeviceId))
		{
			device = await BluetoothLEDevice.FromIdAsync(target.DeviceId).AsTask(ct).ConfigureAwait(false);
		}
		else
		{
			throw new ArgumentException("A Bluetooth address or device id is required.", nameof(target));
		}

		if (device is null)
		{
			throw new GattOperationException("Windows could not create a device object for this target (not advertising and not known to Windows).");
		}

		GattSession? session = null;
		try
		{
			session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
			session.MaintainConnection = true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			log?.Invoke($"GattSession unavailable ({ex.Message}); continuing without keep-alive.");
		}

		return new GattConnection(device, session, log);
	}

	/// <summary>Enumerates services, characteristics and descriptors (this is also what makes Windows connect). Retries while the device is unreachable.</summary>
	public async Task<IReadOnlyList<GattServiceInfo>> DiscoverAsync(BluetoothCacheMode cache, CancellationToken ct)
	{
		await _io.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			ReleaseServices();
			GattDeviceServicesResult? result = null;
			for (var attempt = 1; attempt <= 4; attempt++)
			{
				result = await _device.GetGattServicesAsync(cache).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
				if (result.Status == GattCommunicationStatus.Success)
				{
					break;
				}

				if (result.Status != GattCommunicationStatus.Unreachable || attempt == 4)
				{
					throw new GattOperationException($"Service discovery failed: {Describe(result.Status)}");
				}

				_log?.Invoke($"Device unreachable, retry {attempt}/3…");
				await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
			}

			var list = new List<GattServiceInfo>();
			foreach (var service in result!.Services)
			{
				_services.Add(service);
				var info = new GattServiceInfo { Uuid = service.Uuid, Name = GattNames.Service(service.Uuid) };
				var chars = await service.GetCharacteristicsAsync(cache).AsTask(ct).ConfigureAwait(false);
				if (chars.Status != GattCommunicationStatus.Success)
				{
					info.Error = $"Characteristics: {Describe(chars.Status)}";
				}
				else
				{
					foreach (var ch in chars.Characteristics)
					{
						_chars.TryAdd((service.Uuid, ch.Uuid), ch);
						var p = ch.CharacteristicProperties;
						var item = new GattCharInfo
						{
							Service = service.Uuid,
							Uuid = ch.Uuid,
							Name = GattNames.Characteristic(ch.Uuid),
							Properties = p.ToString(),
							CanRead = p.HasFlag(GattCharacteristicProperties.Read),
							CanWrite = p.HasFlag(GattCharacteristicProperties.Write) || p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse),
							CanNotify = p.HasFlag(GattCharacteristicProperties.Notify) || p.HasFlag(GattCharacteristicProperties.Indicate),
						};
						try
						{
							var descriptors = await ch.GetDescriptorsAsync(cache).AsTask(ct).ConfigureAwait(false);
							if (descriptors.Status == GattCommunicationStatus.Success)
							{
								item.Descriptors = [.. descriptors.Descriptors.Select(d => GattNames.TryShort(d.Uuid, out var s) ? $"0x{s:X4}" : d.Uuid.ToString())];
							}
						}
						catch (Exception ex) when (ex is not OperationCanceledException)
						{
							item.Error = $"Descriptors: {ex.Message}";
						}

						info.Characteristics.Add(item);
					}
				}

				list.Add(info);
			}

			return list;
		}
		finally
		{
			_io.Release();
		}
	}

	public async Task<byte[]> ReadAsync(Guid service, Guid characteristic, CancellationToken ct)
	{
		var ch = Find(service, characteristic);
		await _io.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
			return read.Status == GattCommunicationStatus.Success ? ToArray(read.Value) : throw new GattOperationException($"Read failed: {Describe(read.Status)}");
		}
		finally
		{
			_io.Release();
		}
	}

	public async Task WriteAsync(Guid service, Guid characteristic, byte[] data, CancellationToken ct)
	{
		var ch = Find(service, characteristic);
		var withResponse = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
		await _io.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var status = await ch.WriteValueAsync(data.AsBuffer(), withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse)
				.AsTask(ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
			if (status != GattCommunicationStatus.Success)
			{
				throw new GattOperationException($"Write failed: {Describe(status)}");
			}
		}
		finally
		{
			_io.Release();
		}
	}

	public async Task SubscribeAsync(Guid service, Guid characteristic, CancellationToken ct)
	{
		var ch = Find(service, characteristic);
		var value = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
			? GattClientCharacteristicConfigurationDescriptorValue.Notify
			: GattClientCharacteristicConfigurationDescriptorValue.Indicate;
		await _io.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (!_subscribed.Add((service, characteristic)))
			{
				return;
			}

			ch.ValueChanged += OnValueChanged;
			var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(value).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
			if (status != GattCommunicationStatus.Success)
			{
				ch.ValueChanged -= OnValueChanged;
				_subscribed.Remove((service, characteristic));
				throw new GattOperationException($"Subscribe failed: {Describe(status)}");
			}
		}
		finally
		{
			_io.Release();
		}
	}

	public async Task UnsubscribeAsync(Guid service, Guid characteristic, CancellationToken ct)
	{
		await _io.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			await UnsubscribeCoreAsync((service, characteristic), ct).ConfigureAwait(false);
		}
		finally
		{
			_io.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		foreach (var key in _subscribed.ToArray())
		{
			try
			{
				await UnsubscribeCoreAsync(key, timeout.Token).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_log?.Invoke($"Unsubscribe during close failed: {ex.Message}");
			}
		}

		ReleaseServices();
		_device.ConnectionStatusChanged -= OnConnectionStatusChanged;
		if (_session is not null)
		{
			_session.SessionStatusChanged -= OnSessionStatusChanged;
			_session.MaintainConnection = false;
			_session.Dispose();
		}

		_device.Dispose();

		// Windows releases the link when the last projection is finalized; force it, then give the radio a moment before any reconnect.
		GC.Collect();
		GC.WaitForPendingFinalizers();
		await Task.Delay(300).ConfigureAwait(false);
	}

	public static string Describe(GattCommunicationStatus status) => status switch
	{
		GattCommunicationStatus.Unreachable => "unreachable (device off, out of range, or busy with another host)",
		GattCommunicationStatus.AccessDenied => "access denied (pair the device in Windows first, or another app owns it)",
		GattCommunicationStatus.ProtocolError => "protocol error",
		_ => status.ToString(),
	};

	private async Task UnsubscribeCoreAsync((Guid Service, Guid Characteristic) key, CancellationToken ct)
	{
		if (!_subscribed.Remove(key) || !_chars.TryGetValue(key, out var ch))
		{
			return;
		}

		ch.ValueChanged -= OnValueChanged;
		await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(ct).ConfigureAwait(false);
	}

	private GattCharacteristic Find(Guid service, Guid characteristic) =>
		_disposed
			? throw new ObjectDisposedException(nameof(GattConnection))
			: _chars.TryGetValue((service, characteristic), out var ch)
				? ch
				: throw new GattOperationException($"Characteristic {characteristic} not found; run discovery first.");

	private void ReleaseServices()
	{
		foreach (var key in _subscribed.ToArray())
		{
			if (_chars.TryGetValue(key, out var ch))
			{
				ch.ValueChanged -= OnValueChanged;
			}
		}

		_subscribed.Clear();
		_chars.Clear();
		foreach (var service in _services)
		{
			service.Dispose();
		}

		_services.Clear();
	}

	private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
		Notification?.Invoke(this, new GattNotification(sender.Service.Uuid, sender.Uuid, ToArray(args.CharacteristicValue), DateTimeOffset.Now));

	private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) =>
		LinkChanged?.Invoke(this, sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

	private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args) =>
		LinkChanged?.Invoke(this, args.Status == GattSessionStatus.Active);

	private static byte[] ToArray(IBuffer buffer)
	{
		var bytes = new byte[buffer.Length];
		DataReader.FromBuffer(buffer).ReadBytes(bytes);
		return bytes;
	}
}
