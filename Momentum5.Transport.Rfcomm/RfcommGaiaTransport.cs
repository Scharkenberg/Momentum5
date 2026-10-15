using Momentum5.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace Momentum5.Transport.Rfcomm;

/// <summary>GAIA over Windows RFCOMM (StreamSocket). Sequence per docs/plan/ref/06.</summary>
public sealed class RfcommGaiaTransport : ILinkTransport
{
	private const int ElementNotFoundHr = unchecked((int)0x80070490);
	private const int AccessDeniedHr = unchecked((int)0x80070005);
	private const int TimeoutHr = unchecked((int)0x8007274C);
	private const int DeviceNotConnectedHr = unchecked((int)0x8007048F);
	private const int DeviceUnavailableHr = unchecked((int)0x800710DF);

	private const int MaxServicesTried = 2;

	private readonly Func<Func<Task>, Task>? _uiMarshal;
	private readonly TimeSpan _connectTimeout;

	/// <param name="uiMarshal">Runs the given action on the UI thread (needed for RequestAccessAsync in packaged apps). Null = run inline.</param>
	/// <param name="connectTimeout">Per-socket connect timeout; default 10 s.</param>
	public RfcommGaiaTransport(Func<Func<Task>, Task>? uiMarshal = null, TimeSpan? connectTimeout = null)
	{
		_uiMarshal = uiMarshal;
		_connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
	}

	/// <summary>Optional diagnostic sink (service index, SDP result, access status).</summary>
	public Action<string>? Diagnostics { get; init; }

	private void Log(string message) => Diagnostics?.Invoke(message);

	public string Id => "rfcomm-gaia";

	public LinkFraming Framing => LinkFraming.ByteStream;

	public async ValueTask<ILinkConnection> ConnectAsync(DeviceIdentity target, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(target);
		BluetoothDevice? device = null;
		try
		{
			device = await OpenDeviceAsync(target, ct).ConfigureAwait(false);
			var services = await ResolveServicesAsync(device, ct).ConfigureAwait(false);
			return await ConnectToAnyAsync(device, services, ct).ConfigureAwait(false);
		}
		catch (LinkException)
		{
			device?.Dispose();
			throw;
		}
		catch (OperationCanceledException)
		{
			device?.Dispose();
			throw;
		}
		catch (Exception ex)
		{
			device?.Dispose();
			throw Wrap(ex, "RFCOMM connect failed");
		}
	}

	private static async Task<BluetoothDevice> OpenDeviceAsync(DeviceIdentity target, CancellationToken ct)
	{
		var device = await BluetoothDevice.FromBluetoothAddressAsync(target.Address).AsTask(ct).ConfigureAwait(false);
		if (device is null && !string.IsNullOrEmpty(target.DeviceId))
		{
			device = await BluetoothDevice.FromIdAsync(target.DeviceId).AsTask(ct).ConfigureAwait(false);
		}

		return device ?? throw new LinkException(BtError.ElementNotFound, ElementNotFoundHr,
			BtErrors.UserText(BtError.ElementNotFound));
	}

	private async Task<IReadOnlyList<RfcommDeviceService>> ResolveServicesAsync(BluetoothDevice device, CancellationToken ct)
	{
		var id = RfcommServiceId.FromUuid(GaiaUuids.RfcommService);
		var result = await device.GetRfcommServicesForIdAsync(id, BluetoothCacheMode.Cached).AsTask(ct).ConfigureAwait(false);
		Log($"SDP cached: error={result.Error} services={result.Services.Count}");
		if (result.Services.Count == 0 || result.Error is BluetoothError.DeviceNotConnected)
		{
			result = await device.GetRfcommServicesForIdAsync(id, BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
		}

		Log($"SDP final: error={result.Error} services={result.Services.Count}");
		if (result.Error != BluetoothError.Success)
		{
			throw result.Error switch
			{
				BluetoothError.DeviceNotConnected => new LinkException(BtError.DeviceNotConnected, DeviceNotConnectedHr,
					BtErrors.UserText(BtError.DeviceNotConnected)),
				BluetoothError.RadioNotAvailable or BluetoothError.DisabledByUser or BluetoothError.DisabledByPolicy =>
					new LinkException(BtError.DeviceUnavailable, DeviceUnavailableHr, BtErrors.UserText(BtError.DeviceUnavailable)),
				BluetoothError.ConsentRequired => new LinkException(BtError.AccessDenied, AccessDeniedHr,
					BtErrors.UserText(BtError.AccessDenied)),
				_ => new LinkException(BtError.ElementNotFound, ElementNotFoundHr,
					$"{BtErrors.UserText(BtError.ElementNotFound)} ({result.Error})"),
			};
		}

		if (result.Services.Count == 0)
		{
			throw new LinkException(BtError.ElementNotFound, ElementNotFoundHr, BtErrors.UserText(BtError.ElementNotFound));
		}

		return result.Services;
	}

	private async Task<ILinkConnection> ConnectToAnyAsync(
		BluetoothDevice device, IReadOnlyList<RfcommDeviceService> services, CancellationToken ct)
	{
		LinkException? last = null;
		var count = Math.Min(services.Count, MaxServicesTried);
		for (var i = 0; i < count; i++)
		{
			ct.ThrowIfCancellationRequested();
			var service = services[i];
			StreamSocket? socket = null;
			try
			{
				Log($"service #{i}: channel={service.ConnectionServiceName}");
				if (!await RequestAccessAsync(service, ct).ConfigureAwait(false))
				{
					last = new LinkException(BtError.AccessDenied, AccessDeniedHr, BtErrors.UserText(BtError.AccessDenied));
					continue;
				}

				socket = new StreamSocket();
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeout.CancelAfter(_connectTimeout);
				try
				{
					await socket.ConnectAsync(
						service.ConnectionHostName,
						service.ConnectionServiceName,
						SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(timeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!ct.IsCancellationRequested)
				{
					throw new LinkException(BtError.Timeout, TimeoutHr, BtErrors.UserText(BtError.Timeout));
				}

				Log($"connected via service #{i}");
				return new RfcommConnection(socket, device);
			}
			catch (OperationCanceledException)
			{
				socket?.Dispose();
				throw;
			}
			catch (LinkException ex)
			{
				socket?.Dispose();
				last = ex;
			}
			catch (Exception ex)
			{
				socket?.Dispose();
				last = Wrap(ex, $"RFCOMM service #{i} connect failed");
			}
		}

		throw last ?? new LinkException(BtError.ElementNotFound, ElementNotFoundHr, BtErrors.UserText(BtError.ElementNotFound));
	}

	private async Task<bool> RequestAccessAsync(RfcommDeviceService service, CancellationToken ct)
	{
		var status = DeviceAccessStatus.Unspecified;
		async Task Request() => status = await service.RequestAccessAsync().AsTask(ct).ConfigureAwait(true);

		if (_uiMarshal is null)
		{
			await Request().ConfigureAwait(false);
		}
		else
		{
			await _uiMarshal(Request).ConfigureAwait(false);
		}

		Log($"RequestAccessAsync: {status} (uiMarshal={_uiMarshal is not null})");
		return status is not (DeviceAccessStatus.DeniedByUser or DeviceAccessStatus.DeniedBySystem);
	}

	internal static LinkException Wrap(Exception ex, string context)
	{
		var error = BtErrors.FromHResult(ex.HResult);
		return new LinkException(error, ex.HResult, $"{context}: {BtErrors.UserText(error)}", ex);
	}
}
