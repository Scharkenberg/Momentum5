namespace Momentum5.Core.Transport;

public interface IDevicePresence
{
	event EventHandler<DeviceIdentity>? Changed;

	ValueTask<IReadOnlyList<DeviceIdentity>> GetPairedAsync(CancellationToken ct);
}

public interface IRadioMonitor
{
	event EventHandler<bool>? Changed;

	ValueTask<bool> IsOnAsync(CancellationToken ct);
}

public interface IAdvertisementSource
{
	event EventHandler<AdvertisementSighting>? Sighted;

	void StartWatching();

	void StopWatching();
}

public interface IBatteryFallback
{
	ValueTask<int?> TryReadPercentAsync(ulong address, CancellationToken ct);
}
