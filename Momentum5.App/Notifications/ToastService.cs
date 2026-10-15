using System;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.Notifications;

/// <summary>
/// Opt-in toasts (all flags default off). Every trigger is edge-detected against the last seen state, so pushes that repeat a
/// value never produce a second toast. Clicking a toast focuses the app.
/// </summary>
public sealed class ToastService : IDisposable
{
	public const int LowBatteryPercent = 15;

	private readonly AppServices _services;
	private readonly Action _focus;
	private bool _registered;
	private bool _lowBatteryShown;
	private bool _readOnlyShown;
	private ChargingState? _lastCharging;
	private SessionPhase _lastPhase = SessionPhase.Idle;

	public ToastService(AppServices services, Action focus)
	{
		_services = services;
		_focus = focus;
		try
		{
			AppNotificationManager.Default.NotificationInvoked += OnInvoked;
			AppNotificationManager.Default.Register();
			_registered = true;
		}
		catch (Exception ex)
		{
			// Unpackaged builds cannot register a toast COM server; stay quiet instead of showing an error on every start.
			System.Diagnostics.Debug.WriteLine($"Toasts unavailable: {ex.Message}");
		}

		services.Store.Changed += OnState;
		services.Supervisor.StateChanged += OnSession;
	}

	private NotificationFlags Flags => _services.Current.Notifications;

	/// <summary>Raised instead of showing when toasts are unavailable (for tests and the zone service).</summary>
	public Action<string, string>? Sink { get; set; }

	public void Show(string title, string body)
	{
		if (Sink is { } sink)
		{
			sink(title, body);
			return;
		}

		if (!_registered)
		{
			return;
		}

		var toast = new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification();
		AppNotificationManager.Default.Show(toast);
	}

	private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => _services.Ui.Post(_focus);

	private void OnState(object? sender, HeadsetSnapshot snapshot) => _services.Ui.Post(() => Evaluate(snapshot));

	internal void Evaluate(HeadsetSnapshot snapshot)
	{
		if (snapshot.TryGet(Keys.BatteryPercent, out var percent))
		{
			if (percent < LowBatteryPercent && !_lowBatteryShown)
			{
				_lowBatteryShown = true;
				if (Flags.BatteryLow)
				{
					Show("Headset battery low", $"{percent} % left.");
				}
			}
			else if (percent >= LowBatteryPercent + 5)
			{
				_lowBatteryShown = false;
			}
		}

		if (snapshot.TryGet(Keys.ChargingState, out var charging))
		{
			if (_lastCharging is { } last && last != charging && Flags.Charging)
			{
				Show("Charging changed", charging switch { ChargingState.Charging => "Charging.", ChargingState.Full => "Fully charged.", _ => "Not charging." });
			}

			_lastCharging = charging;
		}

		if (snapshot.Session is { Trust: TrustTier.Unknown } && !_readOnlyShown)
		{
			_readOnlyShown = true;
			if (Flags.FirmwareChanged)
			{
				Show("Read-only mode", "This firmware is not recognised, so settings are read-only.");
			}
		}
	}

	private void OnSession(object? sender, SessionState state) => _services.Ui.Post(() =>
	{
		var previous = _lastPhase;
		_lastPhase = state.Phase;
		if (state.Phase == previous)
		{
			return;
		}

		if (state.Phase == SessionPhase.Ready)
		{
			_lowBatteryShown = false;
			_readOnlyShown = false;
			_lastCharging = null;
			if (Flags.Connected)
			{
				Show("Headset connected", _services.Supervisor.Target?.Name ?? "Connected.");
			}
		}
		else if (previous == SessionPhase.Ready && Flags.Disconnected)
		{
			Show("Headset disconnected", state.Detail is { Length: > 0 } d ? d : "The connection was lost.");
		}
	});

	public void Dispose()
	{
		_services.Store.Changed -= OnState;
		_services.Supervisor.StateChanged -= OnSession;
		if (_registered)
		{
			AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
			AppNotificationManager.Default.Unregister();
		}
	}
}
