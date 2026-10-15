// Momentum5.Core/IBluetoothTransport.cs
using System.Collections.ObjectModel;

namespace Momentum5.Core.Transport;

public sealed record BleDeviceSnapshot(
	string DeviceId,
	ulong BluetoothAddress,
	string? Name,
	short? Rssi,
	bool IsPaired,
	bool IsConnected);

public sealed record GattCharacteristicSnapshot(
	Guid ServiceUuid,
	Guid CharacteristicUuid,
	string Properties,
	bool CanRead,
	bool CanWrite,
	bool CanNotify,
	int? ValueLength);

public sealed record GattServiceSnapshot(
	Guid ServiceUuid,
	IReadOnlyList<GattCharacteristicSnapshot> Characteristics);

public interface IBluetoothTransport : IAsyncDisposable
{
	event EventHandler<string>? LogLine;
	event EventHandler<BleDeviceSnapshot>? DeviceSeen;
	event EventHandler<(Guid ServiceUuid, Guid CharacteristicUuid, byte[] Value)>? NotificationReceived;

	Task<IReadOnlyList<BleDeviceSnapshot>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken);
	Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken);
	Task ConnectByIdAsync(string deviceId, CancellationToken cancellationToken);
	Task DisconnectAsync(CancellationToken cancellationToken);

	Task<IReadOnlyList<GattServiceSnapshot>> EnumerateAsync(CancellationToken cancellationToken);
	Task<byte[]?> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken);
	Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken);

	Task SubscribeAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken);
	Task UnsubscribeAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken);
}