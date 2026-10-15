using System.Globalization;
using Momentum5.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Momentum5.Transport.Rfcomm;

/// <summary>
/// Paired classic devices via a <see cref="DeviceWatcher"/> (AEP properties). <see cref="Changed"/> fires for devices that match
/// <see cref="TargetAddress"/> or <see cref="NameFilter"/> when they appear, disappear or flip connected state.
/// Names are user-editable, so always set <see cref="TargetAddress"/> once the user picked a device.
/// </summary>
public sealed class ClassicPresence : IDevicePresence, IDisposable
{
	private const string PropConnected = "System.Devices.Aep.IsConnected";
	private const string PropAddress = "System.Devices.Aep.DeviceAddress";
	private static readonly string[] Props = [PropConnected, PropAddress];

	private readonly Lock _gate = new();
	private readonly Dictionary<string, DeviceIdentity> _known = [];
	private DeviceWatcher? _watcher;
	private BluetoothDevice? _target;

	public event EventHandler<DeviceIdentity>? Changed;

	public string NameFilter { get; init; } = "MOMENTUM";

	public ulong? TargetAddress { get; set; }

	public async ValueTask<IReadOnlyList<DeviceIdentity>> GetPairedAsync(CancellationToken ct)
	{
		var infos = await DeviceInformation.FindAllAsync(
			BluetoothDevice.GetDeviceSelectorFromPairingState(true), Props, DeviceInformationKind.AssociationEndpoint)
			.AsTask(ct).ConfigureAwait(false);
		return [.. infos.Select(i => ToIdentity(i.Id, i.Name, i.Properties, null)).Where(d => d.Address != 0)];
	}

	public void Start()
	{
		lock (_gate)
		{
			if (_watcher is not null)
			{
				return;
			}

			var watcher = DeviceInformation.CreateWatcher(
				BluetoothDevice.GetDeviceSelectorFromPairingState(true), Props, DeviceInformationKind.AssociationEndpoint);
			watcher.Added += OnAdded;
			watcher.Updated += OnUpdated;
			watcher.Removed += OnRemoved;
			watcher.Start();
			_watcher = watcher;
		}
	}

	public void Stop()
	{
		DeviceWatcher? watcher;
		lock (_gate)
		{
			watcher = _watcher;
			_watcher = null;
		}

		if (watcher is null)
		{
			return;
		}

		watcher.Added -= OnAdded;
		watcher.Updated -= OnUpdated;
		watcher.Removed -= OnRemoved;
		if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
		{
			watcher.Stop();
		}
	}

	/// <summary>Also raises <see cref="Changed"/> from <c>BluetoothDevice.ConnectionStatusChanged</c> of the target (may lag or be wrong; the connect attempt is the truth).</summary>
	public async Task WatchTargetAsync(ulong address, CancellationToken ct)
	{
		TargetAddress = address;
		var device = await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false);
		if (device is null)
		{
			return;
		}

		BluetoothDevice? old;
		lock (_gate)
		{
			old = _target;
			_target = device;
		}

		if (old is not null)
		{
			old.ConnectionStatusChanged -= OnTargetStatus;
			old.Dispose();
		}

		device.ConnectionStatusChanged += OnTargetStatus;
	}

	public void Dispose()
	{
		Stop();
		BluetoothDevice? target;
		lock (_gate)
		{
			target = _target;
			_target = null;
		}

		if (target is not null)
		{
			target.ConnectionStatusChanged -= OnTargetStatus;
			target.Dispose();
		}
	}

	private static DeviceIdentity ToIdentity(string id, string? name, IReadOnlyDictionary<string, object> props, DeviceIdentity? previous)
	{
		var address = props.TryGetValue(PropAddress, out var a) && a is string text && TryParseAddress(text, out var parsed)
			? parsed
			: previous?.Address ?? AddressFromId(id);
		var connected = props.TryGetValue(PropConnected, out var c) && c is bool b ? b : previous?.IsConnected ?? false;
		return new DeviceIdentity(address, string.IsNullOrEmpty(name) ? previous?.Name : name, id, true, connected);
	}

	private static ulong AddressFromId(string id)
	{
		var tail = id[(id.LastIndexOf('-') + 1)..];
		return TryParseAddress(tail, out var address) ? address : 0;
	}

	private static bool TryParseAddress(string text, out ulong address)
	{
		var hex = text.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
		address = 0;
		return hex.Length is > 0 and <= 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
	}

	private bool IsRelevant(DeviceIdentity device) =>
		(TargetAddress is { } target && device.Address == target)
		|| (NameFilter.Length > 0 && device.Name?.Contains(NameFilter, StringComparison.OrdinalIgnoreCase) == true);

	private void OnAdded(DeviceWatcher sender, DeviceInformation info)
	{
		var identity = ToIdentity(info.Id, info.Name, info.Properties, null);
		lock (_gate)
		{
			_known[info.Id] = identity;
		}

		if (identity.Address != 0 && IsRelevant(identity))
		{
			Changed?.Invoke(this, identity);
		}
	}

	private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
	{
		DeviceIdentity? before;
		lock (_gate)
		{
			_known.TryGetValue(update.Id, out before);
		}

		if (before is null)
		{
			return;
		}

		var after = ToIdentity(update.Id, before.Name, update.Properties, before);
		lock (_gate)
		{
			_known[update.Id] = after;
		}

		if (after != before && IsRelevant(after))
		{
			Changed?.Invoke(this, after);
		}
	}

	private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
	{
		DeviceIdentity? before;
		lock (_gate)
		{
			_known.Remove(update.Id, out before);
		}

		if (before is not null && IsRelevant(before))
		{
			Changed?.Invoke(this, before with { IsPaired = false, IsConnected = false });
		}
	}

	private void OnTargetStatus(BluetoothDevice sender, object args) =>
		Changed?.Invoke(this, new DeviceIdentity(
			sender.BluetoothAddress, sender.Name, sender.DeviceId, true, sender.ConnectionStatus == BluetoothConnectionStatus.Connected));
}
