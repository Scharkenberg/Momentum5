using System;
using System.Threading.Tasks;
using Momentum5.App.Services;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

/// <summary>Noise control as the official app models it: Adaptive / Custom / Off, one ANC to transparency slider, anti-wind, phone-call transparency.</summary>
public sealed class NoiseViewModel : ViewModelBase
{
	private readonly AppServices _services;
	private bool _busy;
	private byte _lastCalls = 2;

	public NoiseViewModel(AppServices services)
		: base(services?.Store ?? throw new ArgumentNullException(nameof(services)), services.Ui)
	{
		_services = services;
		Level = new SettingViewModel<int>(Keys.AncLevel, services.Settings, services.Store, services.Ui, services.ReportError) { DebounceMs = 250 };
		AutoPause = new SettingViewModel<bool>(Keys.TransparencyAutoPause, services.Settings, services.Store, services.Ui, services.ReportError);
		PhoneCalls = new SettingViewModel<byte>(Keys.PhoneCallTransparency, services.Settings, services.Store, services.Ui, services.ReportError) { DebounceMs = 300 };
		Level.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Level.IsEnabled))
			{
				OnPropertyChanged(nameof(LevelEnabled));
			}
			else if (e.PropertyName is nameof(Level.Value) or nameof(Level.NumericValue))
			{
				OnPropertyChanged(nameof(AncText));
				OnPropertyChanged(nameof(TransparencyText));
			}
		};
		PhoneCalls.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(PhoneCalls.Value) && PhoneCalls.Value > 0)
			{
				_lastCalls = PhoneCalls.Value;
			}

			OnPropertyChanged(nameof(CallsOn));
			OnPropertyChanged(nameof(CallsLevelEnabled));
		};
	}

	/// <summary>0 ANC to 100 transparency; adjustable only in Custom mode.</summary>
	public SettingViewModel<int> Level { get; }

	public SettingViewModel<bool> AutoPause { get; }

	/// <summary>Transparency during phone calls: 0 = off, 1..4 = amount.</summary>
	public SettingViewModel<byte> PhoneCalls { get; }

	public bool ModeEnabled => !_busy && NoiseControl.IsAvailable(_services.Store.Current);

	private int ModeIndex => NoiseControl.TryGetMode(_services.Store.Current, out var m) ? (int)m : -1;

	/// <summary>Display order of the official app: Adaptive, Custom, Off.</summary>
	public int UiModeIndex
	{
		get => ModeIndex switch { 1 => 0, 2 => 1, 0 => 2, _ => -1 };
		set
		{
			NoiseMode? mode = value switch { 0 => NoiseMode.Adaptive, 1 => NoiseMode.Custom, 2 => NoiseMode.Off, _ => null };
			if (mode is { } m && (int)m != ModeIndex)
			{
				_ = RunAsync(() => NoiseControl.SetModeAsync(_services, m));
			}
		}
	}

	public string ModeHint => ModeIndex switch
	{
		1 => "Adaptive: the headset adjusts noise cancelling to your surroundings.",
		2 => "Custom: blend noise cancelling and transparency with the slider.",
		0 => "Noise cancelling and transparency are off.",
		_ => string.Empty,
	};

	public bool LevelEnabled => Level.IsEnabled && ModeIndex == (int)NoiseMode.Custom;

	public string AncText => $"ANC {100 - Math.Clamp(Level.Value, 0, 100)}%";

	public string TransparencyText => $"{Math.Clamp(Level.Value, 0, 100)}% Transparency";

	private int WindIndex => NoiseControl.GetWindIndex(_services, _services.Store.Current);

	private bool WindAvailable => !_busy && NoiseControl.IsAvailable(_services.Store.Current) && _services.Store.Current.TryGet(Keys.AncAntiWind, out _);

	/// <summary>Anti-wind only acts with Custom ANC; the official app greys it out in Adaptive.</summary>
	public bool WindOnEnabled => WindAvailable && ModeIndex == (int)NoiseMode.Custom;

	public bool WindOn
	{
		get => WindIndex > 0;
		set
		{
			if (value != WindOn)
			{
				_ = RunAsync(() => NoiseControl.SetWindAsync(_services, value ? (_services.Current.AntiWindAuto ? 1 : 2) : 0));
			}
		}
	}

	/// <summary>0 = Auto, 1 = Max.</summary>
	public int WindChoiceIndex
	{
		get => _services.Current.AntiWindAuto ? 0 : 1;
		set
		{
			if (value is 0 or 1 && WindOn && value != WindChoiceIndex)
			{
				_ = RunAsync(() => NoiseControl.SetWindAsync(_services, value == 0 ? 1 : 2));
			}
		}
	}

	public bool WindChoiceEnabled => WindOnEnabled && WindOn;

	public bool CallsOn
	{
		get => PhoneCalls.Value > 0;
		set
		{
			if (value != CallsOn)
			{
				PhoneCalls.Value = value ? (_lastCalls > 0 ? _lastCalls : (byte)2) : (byte)0;
			}
		}
	}

	public bool CallsLevelEnabled => PhoneCalls.IsEnabled && CallsOn;

	protected override void OnStateChanged(HeadsetSnapshot snapshot) => Raise();

	protected override void OnDisposed()
	{
		Level.Dispose();
		AutoPause.Dispose();
		PhoneCalls.Dispose();
	}

	private async Task RunAsync(Func<Task> work)
	{
		_busy = true;
		Raise();
		try
		{
			await work().ConfigureAwait(true);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_services.ReportError($"Noise control: {ex.Message}");
		}
		finally
		{
			_busy = false;
			Raise();
		}
	}

	private void Raise()
	{
		OnPropertyChanged(nameof(UiModeIndex));
		OnPropertyChanged(nameof(ModeHint));
		OnPropertyChanged(nameof(ModeEnabled));
		OnPropertyChanged(nameof(LevelEnabled));
		OnPropertyChanged(nameof(WindOn));
		OnPropertyChanged(nameof(WindOnEnabled));
		OnPropertyChanged(nameof(WindChoiceIndex));
		OnPropertyChanged(nameof(WindChoiceEnabled));
		OnPropertyChanged(nameof(CallsOn));
		OnPropertyChanged(nameof(CallsLevelEnabled));
	}
}
