namespace Momentum5.Core.Transport;

public interface ILinkTransport
{
	/// <summary>"rfcomm-gaia" | "winsock-rfcomm" | "ble-gatt-gaia" | "loopback"</summary>
	string Id { get; }

	LinkFraming Framing { get; }

	/// <exception cref="LinkException"/>
	ValueTask<ILinkConnection> ConnectAsync(DeviceIdentity target, CancellationToken ct);
}

public interface ILinkConnection : IAsyncDisposable
{
	string TransportId { get; }

	/// <summary>Returns 0 when the peer closed or the link was lost.</summary>
	ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

	/// <summary>ByteStream: any bytes. Datagram: exactly one message.</summary>
	ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

	/// <summary>Completes on link loss or dispose.</summary>
	Task Closed { get; }
}
