using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
	private readonly AppServices _services;
	private int _batteryPercent;
	private bool _hasBattery;
	private string _chargingText = string.Empty;
	private string _model = "—";
	private string _firmware = "—";
	private string _serial = "—";
	private string? _trustMessage;
	private string _codecText = "—";
	private string _sampleRateText = "—";
	private string _transportText = "—";
	private bool _showGuidance;
	private string _headsetName = "No headset";
	private string _connectedText = string.Empty;
	private int _connLoading;

	public DashboardViewModel(AppServices services)
		: base(services?.Store ?? throw new ArgumentNullException(nameof(services)), services.Ui)
	{
		_services = services;
		Noise = new NoiseViewModel(services);
		Immersive = new SettingViewModel<bool>(Keys.ImmersiveAudio, services.Settings, services.Store, services.Ui, services.ReportError);
		BassBoost = new SettingViewModel<bool>(Keys.BassBoost, services.Settings, services.Store, services.Ui, services.ReportError);
		Immersive.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Immersive.Value))
			{
				OnPropertyChanged(nameof(ImmersiveIndex));
			}
		};
		Status = new ConnectionStatusViewModel(services);
		services.Supervisor.StateChanged += OnSupervisorState;
		services.SettingsChanged += OnSettingsChanged;
		OnStateChanged(services.Store.Current);
		if (services.Supervisor.State.Phase is SessionPhase.Ready or SessionPhase.ReadOnly)
		{
			_ = LoadConnectionsAsync();
		}
	}

	public NoiseViewModel Noise { get; }

	public SettingViewModel<bool> Immersive { get; }

	public SettingViewModel<bool> BassBoost { get; }

	/// <summary>0 = On, 1 = Off (display order of the official app).</summary>
	public int ImmersiveIndex
	{
		get => Immersive.Value ? 0 : 1;
		set => Immersive.Value = value == 0;
	}

	/// <summary>Model line under the name; empty when the headset has no custom name.</summary>
	public string SubtitleText => string.Equals(_headsetName, Model, StringComparison.Ordinal) ? string.Empty : Model;

	public string HeadsetName
	{
		get => _headsetName;
		private set
		{
			if (SetProperty(ref _headsetName, value))
			{
				OnPropertyChanged(nameof(SubtitleText));
			}
		}
	}

	/// <summary>Connected devices as the headset reports them (numbered, like the official app).</summary>
	public string ConnectedText
	{
		get => _connectedText;
		private set => SetProperty(ref _connectedText, value);
	}

	public ConnectionStatusViewModel Status { get; }

	public bool HasBattery
	{
		get => _hasBattery;
		private set => SetProperty(ref _hasBattery, value);
	}

	public int BatteryPercent
	{
		get => _batteryPercent;
		private set
		{
			if (SetProperty(ref _batteryPercent, value))
			{
				OnPropertyChanged(nameof(BatteryText));
			}
		}
	}

	public string BatteryText => _hasBattery ? $"{_batteryPercent} %" : "—";

	private ChargingState? _charging;

	public bool IsCharging => _charging == ChargingState.Charging;

	/// <summary>Segoe Fluent battery glyph for the level; the charging variants carry a bolt.</summary>
	public string BatteryGlyph
	{
		get
		{
			if (!_hasBattery)
			{
				return "\uE850";
			}

			var level = Math.Clamp(_batteryPercent / 10, 0, 10);
			if (_charging == ChargingState.Charging)
			{
				return level >= 10 ? "\uE83E" : ((char)(0xE85A + level)).ToString();
			}

			return level >= 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
		}
	}

	/// <summary>"85 % · Charging" (or just the percentage when the state is unknown).</summary>
	public string BatteryLine => ChargingText.Length > 0 && _hasBattery ? $"{BatteryText} · {ChargingText}" : BatteryText;

	public string ChargingText
	{
		get => _chargingText;
		private set => SetProperty(ref _chargingText, value);
	}

	public string Model
	{
		get => _model;
		private set
		{
			if (SetProperty(ref _model, value))
			{
				OnPropertyChanged(nameof(SubtitleText));
			}
		}
	}

	public string Firmware
	{
		get => _firmware;
		private set => SetProperty(ref _firmware, value);
	}

	public string Serial
	{
		get => _serial;
		private set => SetProperty(ref _serial, value);
	}

	/// <summary>Null when the firmware is fully verified.</summary>
	public string? TrustMessage
	{
		get => _trustMessage;
		private set
		{
			if (SetProperty(ref _trustMessage, value))
			{
				OnPropertyChanged(nameof(ShowTrustBanner));
			}
		}
	}

	public bool ShowTrustBanner => _trustMessage is not null;

	public string CodecText
	{
		get => _codecText;
		private set => SetProperty(ref _codecText, value);
	}

	public string SampleRateText
	{
		get => _sampleRateText;
		private set => SetProperty(ref _sampleRateText, value);
	}

	/// <summary>The transport id is a developer detail: shown only while developer mode is on (Diagnostics always lists it).</summary>
	public bool ShowTransport => _services.Current.DevModeUnlocked;

	private void OnSettingsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ShowTransport));

	public string TransportText
	{
		get => _transportText;
		private set => SetProperty(ref _transportText, value);
	}

	/// <summary>No headset chosen yet: show the pairing guidance card instead of an error.</summary>
	public bool ShowGuidance
	{
		get => _showGuidance;
		private set => SetProperty(ref _showGuidance, value);
	}

	protected override void OnStateChanged(HeadsetSnapshot snapshot)
	{
		HasBattery = snapshot.TryGet(Keys.BatteryPercent, out var battery);
		BatteryPercent = battery;
		_charging = snapshot.TryGet(Keys.ChargingState, out var cs) ? cs : null;
		ChargingText = snapshot.TryGet(Keys.ChargingState, out var charging)
			? charging switch { ChargingState.Charging => "Charging", ChargingState.Full => "Fully charged", _ => "On battery" }
			: string.Empty;
		OnPropertyChanged(nameof(BatteryGlyph));
		OnPropertyChanged(nameof(IsCharging));
		OnPropertyChanged(nameof(BatteryLine));

		var session = snapshot.Session;
		HeadsetName = snapshot.TryGet(Keys.LocalName, out var nm) && nm.Length > 0 ? nm : session?.Model ?? "No headset";
		Model = session?.Model ?? "—";
		Firmware = session?.Firmware ?? "—";
		Serial = session?.Serial ?? "—";
		TransportText = session?.TransportId ?? "—";
		TrustMessage = session?.Trust switch
		{
			TrustTier.Compatible => $"Firmware {session.Firmware} is newer than the tested 6.21.5. Features should work but are not verified.",
			TrustTier.Unknown => $"Firmware {session.Firmware} is not recognized. The app is read-only for this headset.",
			_ => null,
		};

		CodecText = snapshot.TryGet(Keys.Codec, out var codec) && codec != 255 ? $"codec 0x{codec:X2}" : "no audio stream";
		SampleRateText = snapshot.TryGet(Keys.SampleRate, out var rate) && rate > 0
			? string.Create(CultureInfo.InvariantCulture, $"{rate / 1000.0:0.#} kHz")
			: "—";
		ShowGuidance = _services.Supervisor.Target is null;
	}

	protected override void OnDisposed()
	{
		_services.Supervisor.StateChanged -= OnSupervisorState;
		_services.SettingsChanged -= OnSettingsChanged;
		Noise.Dispose();
		Immersive.Dispose();
		BassBoost.Dispose();
		Status.Dispose();
	}

	private void OnSupervisorState(object? sender, SessionState state) =>
		Ui.Post(() =>
		{
			OnStateChanged(_services.Store.Current);
			if (state.Phase is SessionPhase.Ready or SessionPhase.ReadOnly)
			{
				_ = LoadConnectionsAsync();
			}
			else if (state.Phase is SessionPhase.Idle or SessionPhase.RadioOff)
			{
				ConnectedText = string.Empty;
			}
		});

	public async Task LoadConnectionsAsync()
	{
		if (Interlocked.Exchange(ref _connLoading, 1) == 1)
		{
			return;
		}

		try
		{
			if (_services.Supervisor.Current is not { } session)
			{
				return;
			}

			var table = await MultipointReader.ReadAsync(session, CancellationToken.None).ConfigureAwait(true);
			var lines = table.Devices.Where(d => d.IsConnected)
				.Select((d, i) => $"{i + 1}. {(d.Name.Length > 0 ? d.Name : AppServices.FormatAddress(d.Address))}{(table.Current == d.Address ? " (this PC)" : string.Empty)}")
				.ToList();
			ConnectedText = lines.Count > 0 ? string.Join('\n', lines) : "No device connected.";
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			ConnectedText = string.Empty;
		}
		finally
		{
			Interlocked.Exchange(ref _connLoading, 0);
		}
	}
}
