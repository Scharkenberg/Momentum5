using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Momentum5.App.Notifications;
using Momentum5.App.Services;
using Momentum5.App.ViewModels;
using Momentum5.Client;
using Windows.Devices.Geolocation;

namespace Momentum5.App.Zones;

public interface IGeoSource
{
	/// <summary>Current position, or null when location is denied or unavailable.</summary>
	Task<(double Latitude, double Longitude)?> GetAsync(CancellationToken ct);
}

public sealed class GeolocatorSource : IGeoSource
{
	public async Task<(double Latitude, double Longitude)?> GetAsync(CancellationToken ct)
	{
		var access = await Geolocator.RequestAccessAsync();
		if (access != GeolocationAccessStatus.Allowed)
		{
			return null;
		}

		var locator = new Geolocator { DesiredAccuracyInMeters = 100 };
		var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(20)).AsTask(ct);
		var p = position.Coordinate.Point.Position;
		return (p.Latitude, p.Longitude);
	}
}

/// <summary>Opt-in location zones (U-21). Inert unless <c>ZonesEnabled</c> is set and Windows grants location.</summary>
public sealed class ZoneService : IDisposable
{
	private readonly AppServices _services;
	private readonly ToastService? _toasts;
	private readonly IGeoSource _geo;
	private readonly DispatcherQueueTimer? _timer;
	private string? _currentZone;
	private int _busy;

	public ZoneService(AppServices services, ToastService? toasts, IGeoSource? geo = null, bool useTimer = true)
	{
		_services = services;
		_toasts = toasts;
		_geo = geo ?? new GeolocatorSource();
		if (useTimer)
		{
			_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
			_timer.Interval = TimeSpan.FromMinutes(1);
			_timer.Tick += async (_, _) => await PollAsync().ConfigureAwait(true);
		}

		services.SettingsChanged += OnSettingsChanged;
		ApplyEnabled();
	}

	public string? CurrentZone => _currentZone;

	public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
	{
		const double R = 6_371_000;
		static double Rad(double d) => d * Math.PI / 180;
		var dLat = Rad(lat2 - lat1);
		var dLon = Rad(lon2 - lon1);
		var a = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2));
		return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
	}

	public async Task<(double Latitude, double Longitude)?> CurrentPositionAsync(CancellationToken ct = default)
	{
		try
		{
			return await _geo.GetAsync(ct).ConfigureAwait(true);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_services.ReportError($"Location unavailable: {ex.Message}");
			return null;
		}
	}

	private void OnSettingsChanged(object? sender, EventArgs e) => ApplyEnabled();

	private void ApplyEnabled()
	{
		if (_services.Current.ZonesEnabled && _timer is not null)
		{
			if (!_timer.IsRunning)
			{
				_timer.Start();
				_ = PollAsync();
			}
		}
		else
		{
			_timer?.Stop();
			_currentZone = null;
		}
	}

	private async Task PollAsync()
	{
		if (!_services.Current.ZonesEnabled || Interlocked.Exchange(ref _busy, 1) == 1)
		{
			return;
		}

		try
		{
			if (await CurrentPositionAsync().ConfigureAwait(true) is { } p)
			{
				await EvaluateAsync(p.Latitude, p.Longitude).ConfigureAwait(true);
			}
		}
		finally
		{
			Interlocked.Exchange(ref _busy, 0);
		}
	}

	/// <summary>Applies the binding of the nearest containing zone when it differs from the current one.</summary>
	public async Task EvaluateAsync(double latitude, double longitude)
	{
		if (!_services.Current.ZonesEnabled)
		{
			return;
		}

		var zone = _services.Current.Zones
			.Select(z => (Zone: z, Distance: DistanceMeters(latitude, longitude, z.Latitude, z.Longitude)))
			.Where(x => x.Distance <= x.Zone.RadiusMeters)
			.OrderBy(x => x.Distance)
			.Select(x => x.Zone)
			.FirstOrDefault();
		if (zone?.Name == _currentZone)
		{
			return;
		}

		_currentZone = zone?.Name;
		if (zone is null)
		{
			return;
		}

		await ApplyAsync(zone).ConfigureAwait(true);
		_toasts?.Show($"Zone: {zone.Name}", Describe(zone));
	}

	private async Task ApplyAsync(LocationZone zone)
	{
		switch (zone.Mode)
		{
			case "Anc":
				await QuickActions.SetAsync(_services, QuickAction.Anc, true).ConfigureAwait(true);
				break;
			case "Transparency":
				await QuickActions.SetAsync(_services, QuickAction.Transparency, true).ConfigureAwait(true);
				break;
			case "Off":
				await QuickActions.SetAsync(_services, QuickAction.Anc, false).ConfigureAwait(true);
				await QuickActions.SetAsync(_services, QuickAction.Transparency, false).ConfigureAwait(true);
				break;
		}

		if (zone.Preset is { Length: > 0 } name)
		{
			var presets = new PresetsViewModel(_services);
			if (presets.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } item)
			{
				await presets.ApplyAsync(item.Preset).ConfigureAwait(true);
			}
		}
	}

	private static string Describe(LocationZone z) =>
		string.Join(", ", new[] { z.Mode is { } m ? $"mode {m}" : null, z.Preset is { } p ? $"preset {p}" : null }.Where(x => x is not null)) is { Length: > 0 } s ? s : "no changes bound";

	public void Dispose()
	{
		_services.SettingsChanged -= OnSettingsChanged;
		_timer?.Stop();
	}
}
