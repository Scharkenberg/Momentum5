namespace Momentum5.Core.Transport;

public enum LinkFraming
{
	ByteStream,
	Datagram,
}

public sealed record DeviceIdentity(ulong Address, string? Name, string? DeviceId, bool IsPaired, bool IsConnected);

public sealed record AdvertisementSighting(ulong Address, string? Name, short Rssi, DateTimeOffset Timestamp);

public sealed class LinkException : Exception
{
	public BtError Error { get; }

	public LinkException(BtError error, int hresult, string message, Exception? inner = null)
		: base(message, inner)
	{
		Error = error;
		HResult = hresult;
	}
}
