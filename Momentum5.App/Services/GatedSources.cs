using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Momentum5.Core.Transport;

namespace Momentum5.App.Services;

/// <summary>Wake-source decorators: forward events only while auto-connect is on (the "Auto-connect" switch).</summary>
internal sealed class GatedPresence : IDevicePresence
{
	private readonly IDevicePresence _inner;

	public GatedPresence(IDevicePresence inner, Func<bool> enabled)
	{
		_inner = inner;
		inner.Changed += (_, device) =>
		{
			if (enabled())
			{
				Changed?.Invoke(this, device);
			}
		};
	}

	public event EventHandler<DeviceIdentity>? Changed;

	public ValueTask<IReadOnlyList<DeviceIdentity>> GetPairedAsync(CancellationToken ct) => _inner.GetPairedAsync(ct);
}

internal sealed class GatedRadio : IRadioMonitor
{
	private readonly IRadioMonitor _inner;

	public GatedRadio(IRadioMonitor inner, Func<bool> enabled)
	{
		_inner = inner;
		inner.Changed += (_, on) =>
		{
			if (enabled())
			{
				Changed?.Invoke(this, on);
			}
		};
	}

	public event EventHandler<bool>? Changed;

	public ValueTask<bool> IsOnAsync(CancellationToken ct) => _inner.IsOnAsync(ct);
}

internal sealed class GatedAdvertisements : IAdvertisementSource
{
	private readonly IAdvertisementSource _inner;

	public GatedAdvertisements(IAdvertisementSource inner, Func<bool> enabled)
	{
		_inner = inner;
		inner.Sighted += (_, sighting) =>
		{
			if (enabled())
			{
				Sighted?.Invoke(this, sighting);
			}
		};
	}

	public event EventHandler<AdvertisementSighting>? Sighted;

	public void StartWatching() => _inner.StartWatching();

	public void StopWatching() => _inner.StopWatching();
}
