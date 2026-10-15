using System.Collections.Concurrent;
using Momentum5.Core.Transport;
using Windows.Devices.Bluetooth.Advertisement;

namespace Momentum5.Transport.Ble;

/// <summary>
/// Advisory "headset is nearby" signal from active BLE scanning. Matches <see cref="TargetAddress"/> or a local name starting with
/// <c>LE-MOMENTUM 5</c> / containing <c>MOMENTUM</c>; at most one <see cref="Sighted"/> per address per <see cref="Throttle"/>.
/// Every Start creates a fresh watcher and every Stop releases it.
/// </summary>
public sealed class AdvertisementPresence(TimeProvider? time = null) : IAdvertisementSource, IDisposable
{
	public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(5);

	private readonly TimeProvider _time = time ?? TimeProvider.System;
	private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastSeen = new();
	private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastRaised = new();
	private readonly Lock _gate = new();
	private BluetoothLEAdvertisementWatcher? _watcher;

	public event EventHandler<AdvertisementSighting>? Sighted;

	public ulong? TargetAddress { get; set; }

	/// <summary>Last advertisement time per address (unthrottled).</summary>
	public IReadOnlyDictionary<ulong, DateTimeOffset> LastSeen => _lastSeen;

	public void StartWatching()
	{
		lock (_gate)
		{
			if (_watcher is not null)
			{
				return;
			}

			var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
			watcher.Received += OnReceived;
			watcher.Start();
			_watcher = watcher;
		}
	}

	public void StopWatching()
	{
		BluetoothLEAdvertisementWatcher? watcher;
		lock (_gate)
		{
			watcher = _watcher;
			_watcher = null;
		}

		if (watcher is null)
		{
			return;
		}

		watcher.Received -= OnReceived;
		if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started)
		{
			watcher.Stop();
		}
	}

	public void Dispose() => StopWatching();

	private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
	{
		var name = args.Advertisement.LocalName;
		var address = args.BluetoothAddress;
		var relevant = (TargetAddress is { } target && target == address)
			|| name.StartsWith("LE-MOMENTUM 5", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("MOMENTUM", StringComparison.OrdinalIgnoreCase);
		if (!relevant)
		{
			return;
		}

		var now = _time.GetUtcNow();
		_lastSeen[address] = now;
		if (_lastRaised.TryGetValue(address, out var last) && now - last < Throttle)
		{
			return;
		}

		_lastRaised[address] = now;
		Sighted?.Invoke(this, new AdvertisementSighting(address, string.IsNullOrEmpty(name) ? null : name, args.RawSignalStrengthInDBm, now));
	}
}
