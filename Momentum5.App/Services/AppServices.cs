using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Momentum5.Client;
using Momentum5.Core.Transport;
using Momentum5.Simulation;
using Momentum5.Transport.Ble;
using Momentum5.Transport.Rfcomm;

namespace Momentum5.App.Services;

/// <summary>Composition root (manual, no Generic Host): one supervised headset session shared by every page.</summary>
public sealed class AppServices : IAsyncDisposable
{
	private static readonly DeviceIdentity SimDevice = new(0xA1B2C3D4E5F6, "M5 Simulator", null, true, true);

	private readonly ClassicPresence? _presence;
	private readonly AdvertisementPresence? _adverts;
	private readonly BluetoothRadioMonitor? _radio;
	private readonly JsonlFrameTap _tap;
	private readonly IFrameTap _taps;
	private readonly AppSettingsStore _settingsStore = new();
	private readonly Lock _settingsGate = new();
	private volatile bool _wakeEnabled;
	private ActiveSession? _refreshedSession;

	public AppServices(IUiDispatcher ui, bool simulate)
	{
		Ui = ui;
		Simulated = simulate;
		Current = _settingsStore.Load();
		_wakeEnabled = Current.AutoConnect;
		Store = new HeadsetStateStore();
		_tap = new JsonlFrameTap();
		Trace = new InMemoryTap();
		_taps = new CompositeFrameTap(_tap, Trace);

		ILinkTransport transport;
		SupervisorOptions options;
		if (simulate)
		{
			var loopback = new LoopbackTransport();
			Fake = new FakeM5();
			Fake.Attach(loopback);
			transport = loopback;
			options = new SupervisorOptions { Tap = _taps, Log = Shell.AppLog.StaticWrite };
		}
		else
		{
			_presence = new ClassicPresence();
			_adverts = new AdvertisementPresence();
			_radio = new BluetoothRadioMonitor();
			_presence.Changed += (_, _) => PairedDevicesChanged?.Invoke(this, EventArgs.Empty);
			transport = new RfcommGaiaTransport(OnUiThreadAsync);
			options = new SupervisorOptions
			{
				Tap = _taps,
				Log = Shell.AppLog.StaticWrite,
				Radio = new GatedRadio(_radio, () => _wakeEnabled),
				Presence = new GatedPresence(_presence, () => _wakeEnabled),
				Advertisements = new GatedAdvertisements(_adverts, () => _wakeEnabled),
			};
		}

		Supervisor = new ConnectionSupervisor([transport], Store, options);
		Settings = new SettingService(Supervisor, Store);
		Supervisor.StateChanged += OnSupervisorState;
	}

	public IUiDispatcher Ui { get; }

	public bool Simulated { get; }

	public FakeM5? Fake { get; }

	public HeadsetStateStore Store { get; }

	public ConnectionSupervisor Supervisor { get; }

	public SettingService Settings { get; }

	public InMemoryTap Trace { get; }

	/// <summary>BLE advertisement watcher (null in the simulator).</summary>
	public AdvertisementPresence? Adverts => _adverts;

	public AppSettings Current { get; private set; }

	public string FrameLogDirectory => JsonlFrameTap.DefaultDirectory;

	public event EventHandler? SettingsChanged;

	public event EventHandler? PairedDevicesChanged;

	public event Action<string>? ErrorRaised;

	public void ReportError(string message) => Ui.Post(() => ErrorRaised?.Invoke(message));

	public async Task StartAsync(CancellationToken ct = default)
	{
		DeviceIdentity? target;
		if (Simulated)
		{
			target = SimDevice;
		}
		else
		{
			_presence!.Start();
			_adverts!.StartWatching();
			target = await PickTargetAsync(ct).ConfigureAwait(true);
		}

		if (target is not null)
		{
			await ApplyTargetAsync(target, persist: false, ct).ConfigureAwait(true);
		}

		if (Simulated || Current.AutoConnect)
		{
			Supervisor.Start();
		}
	}

	public async Task<IReadOnlyList<DeviceIdentity>> GetPairedAsync(CancellationToken ct = default)
	{
		if (Simulated)
		{
			return [SimDevice];
		}

		return await _presence!.GetPairedAsync(ct).ConfigureAwait(true);
	}

	/// <summary>Makes <paramref name="device"/> the headset to keep connected (does not connect by itself).</summary>
	public async Task SelectDeviceAsync(DeviceIdentity device, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(device);
		_wakeEnabled = Current.AutoConnect;
		await ApplyTargetAsync(device, persist: true, ct).ConfigureAwait(true);
	}

	public void Connect() => _ = ConnectCoreAsync();

	/// <summary>Drops the RFCOMM link and switches the Windows audio profiles off (the only way to really disconnect on Windows).</summary>
	public void Disconnect()
	{
		_wakeEnabled = false;
		Supervisor.PauseUntilWake();
		if (!Simulated && Supervisor.Target is { } t)
		{
			_ = Task.Run(() =>
			{
				if (!WindowsAudioLink.SetEnabled(t.Address, false, out var detail))
				{
					ReportError($"Windows audio disconnect failed: {detail}");
				}
			});
		}
	}

	private async Task ConnectCoreAsync()
	{
		if (!Simulated && Supervisor.Target is { } t)
		{
			await Task.Run(() =>
			{
				if (!WindowsAudioLink.SetEnabled(t.Address, true, out var detail))
				{
					ReportError($"Windows audio reconnect failed: {detail}");
				}
			}).ConfigureAwait(true);
		}

		_wakeEnabled = Current.AutoConnect;
		Supervisor.Start();

		// Windows needs a moment to bring the profiles back up; a connect attempt too early fails and used to need a second tap.
		await Task.Delay(Simulated ? 0 : 1500).ConfigureAwait(true);
		for (var attempt = 0; attempt < 5; attempt++)
		{
			Supervisor.Connect();
			for (var i = 0; i < 16; i++)
			{
				if (Supervisor.State.Phase is SessionPhase.Ready or SessionPhase.ReadOnly)
				{
					return;
				}

				await Task.Delay(250).ConfigureAwait(true);
			}
		}
	}

	/// <summary>Forgets only this app's association (last device); Windows pairing is untouched.</summary>
	public void ForgetAssociation()
	{
		_wakeEnabled = false;
		Supervisor.PauseUntilWake();
		UpdateSettings(s => s with { LastDeviceAddress = null });
	}

	public void SetAutoConnect(bool value)
	{
		_wakeEnabled = value;
		UpdateSettings(s => s with { AutoConnect = value });
	}

	/// <param name="notify">False for high-frequency bookkeeping (window placement) that nothing needs to react to.</param>
	public void UpdateSettings(Func<AppSettings, AppSettings> change, bool notify = true)
	{
		ArgumentNullException.ThrowIfNull(change);
		lock (_settingsGate)
		{
			Current = change(Current);
			_settingsStore.Save(Current);
		}

		if (notify)
		{
			Ui.Post(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
		}
	}

	public static string FormatAddress(ulong address)
	{
		var hex = address.ToString("X12", CultureInfo.InvariantCulture);
		return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
	}

	public async ValueTask DisposeAsync()
	{
		await Supervisor.DisposeAsync().ConfigureAwait(false);
		_presence?.Dispose();
		_adverts?.Dispose();
		_radio?.Dispose();
		await _tap.DisposeAsync().ConfigureAwait(false);
	}

	private async Task<DeviceIdentity?> PickTargetAsync(CancellationToken ct)
	{
		try
		{
			var paired = await _presence!.GetPairedAsync(ct).ConfigureAwait(true);
			if (Current.LastDeviceAddress is { } last && paired.FirstOrDefault(d => d.Address == last) is { } remembered)
			{
				return remembered;
			}

			// m5ctl remembers its last device in the same folder; share it so both tools agree.
			if (TryReadCliLast(out var cli) && paired.FirstOrDefault(d => d.Address == cli) is { } fromCli)
			{
				return fromCli;
			}

			// Users rename the headset (e.g. "Joe's Vibe Cans"), so the name is only a hint.
			if (paired.FirstOrDefault(d => d.Name?.Contains("MOMENTUM", StringComparison.OrdinalIgnoreCase) == true) is { } byName)
			{
				return byName;
			}

			var connected = paired.Where(d => d.IsConnected).ToList();
			return connected.Count == 1 ? connected[0] : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			ReportError($"Could not list paired devices: {ex.Message}");
			return null;
		}
	}

	private static bool TryReadCliLast(out ulong address)
	{
		address = 0;
		try
		{
			var path = System.IO.Path.Combine(ClientPaths.AppData, "last-device.txt");
			if (!System.IO.File.Exists(path))
			{
				return false;
			}

			var text = System.IO.File.ReadAllText(path).Trim().Replace(":", string.Empty, StringComparison.Ordinal);
			return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address != 0;
		}
		catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private async Task ApplyTargetAsync(DeviceIdentity target, bool persist, CancellationToken ct)
	{
		Supervisor.SetTarget(target);
		if (_presence is not null && _adverts is not null)
		{
			_presence.TargetAddress = target.Address;
			_adverts.TargetAddress = target.Address;
			try
			{
				await _presence.WatchTargetAsync(target.Address, ct).ConfigureAwait(true);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				ReportError($"Presence watch failed: {ex.Message}");
			}
		}

		if (persist && !Simulated)
		{
			UpdateSettings(s => s with { LastDeviceAddress = target.Address });
		}
	}

	private void OnSupervisorState(object? sender, SessionState state)
	{
		if (state.Phase is SessionPhase.Ready or SessionPhase.ReadOnly && Supervisor.Current is { } active && !ReferenceEquals(active, _refreshedSession))
		{
			_refreshedSession = active;
			_ = Task.Run(async () =>
			{
				try
				{
					await Settings.RefreshAllAsync(CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					ReportError($"Initial read failed: {ex.Message}");
				}
			});
		}

		if (state.Phase == SessionPhase.Ready && !Simulated && Supervisor.Target is { } t && Current.LastDeviceAddress != t.Address)
		{
			UpdateSettings(s => s with { LastDeviceAddress = t.Address });
		}
	}

	private Task OnUiThreadAsync(Func<Task> work)
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Ui.Post(async () =>
		{
			try
			{
				await work().ConfigureAwait(true);
				tcs.SetResult();
			}
			catch (Exception ex)
			{
				tcs.SetException(ex);
			}
		});
		return tcs.Task;
	}
}
