using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Services;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

public sealed class EqBandViewModel(int index, Action<EqBandViewModel> changed) : ObservableObject
{
	private double _gain;
	private bool _isEnabled;
	private bool _silent;
	private string _label = string.Empty;

	public int Index { get; } = index;

	public double Min { get; set; } = -6;

	public double Max { get; set; } = 6;

	public string Label
	{
		get => _label;
		set
		{
			if (SetProperty(ref _label, value))
			{
				OnPropertyChanged(nameof(ShortLabel));
			}
		}
	}

	/// <summary>Label without units ("325", "1.5k") so eight bands fit a 360 DIP window.</summary>
	public string ShortLabel => _label.Replace(" kHz", "k", StringComparison.Ordinal).Replace(" Hz", string.Empty, StringComparison.Ordinal);

	public bool IsEnabled
	{
		get => _isEnabled;
		set => SetProperty(ref _isEnabled, value);
	}

	public double Gain
	{
		get => _gain;
		set
		{
			var rounded = Math.Round(Math.Clamp(value, Min, Max), 1);
			if (_gain == rounded)
			{
				return;
			}

			_gain = rounded;
			OnPropertyChanged();
			OnPropertyChanged(nameof(GainText));
			OnPropertyChanged(nameof(GainShort));
			if (!_silent)
			{
				changed(this);
			}
		}
	}

	public string GainShort => string.Create(CultureInfo.InvariantCulture, $"{_gain:+0.0;-0.0;0}");

	public string GainText => string.Create(CultureInfo.InvariantCulture, $"{_gain:+0.0;-0.0;0.0} dB");

	public void SetSilently(double value)
	{
		_silent = true;
		try
		{
			Gain = value;
		}
		finally
		{
			_silent = false;
		}
	}
}

public sealed class SoundViewModel : ViewModelBase
{
	private const int ApplyDelayMs = 400;
	private readonly AppServices _services;
	private CancellationTokenSource? _debounce;
	private bool _pending;
	private bool _busy;
	private string? _warning;

	public SoundViewModel(AppServices services)
		: base(services?.Store ?? throw new ArgumentNullException(nameof(services)), services.Ui)
	{
		_services = services;
		Mode = new SettingViewModel<SoundMode>(Keys.SoundMode, services.Settings, services.Store, services.Ui, services.ReportError);
		Bass = new SettingViewModel<bool>(Keys.BassBoost, services.Settings, services.Store, services.Ui, services.ReportError);
		Mode.PropertyChanged += (_, _) => RefreshFlags();
		OnStateChanged(services.Store.Current);
	}

	public SettingViewModel<SoundMode> Mode { get; }

	public SettingViewModel<bool> Bass { get; }

	public ObservableCollection<EqBandViewModel> Bands { get; } = [];

	/// <summary>0 Off, 1 Graphic EQ, 2 Speech clarity; -1 while the headset is in a personalized mode (read-only here).</summary>
	public int ModeIndex
	{
		get => Mode.Value switch { SoundMode.Off => 0, SoundMode.GraphicEq => 1, SoundMode.SpeechClarity => 2, _ => -1 };
		set
		{
			if (value is >= 0 and <= 2)
			{
				Mode.Value = value switch { 0 => SoundMode.Off, 1 => SoundMode.GraphicEq, _ => SoundMode.SpeechClarity };
			}
		}
	}

	public bool IsPersonalized => Mode.IsAvailable && Mode.Value == SoundMode.Personalization;

	public bool EqEnabled => Mode.IsAvailable && Mode.IsWritable && Mode.Value == SoundMode.GraphicEq && Bands.Count > 0 && !_busy;

	public string? Warning
	{
		get => _warning;
		private set => SetProperty(ref _warning, value);
	}

	protected override void OnStateChanged(HeadsetSnapshot snapshot)
	{
		if (snapshot.TryGet(Keys.EqConfig, out var cfg))
		{
			if (Bands.Count != cfg.Bands)
			{
				Bands.Clear();
				for (var i = 0; i < cfg.Bands; i++)
				{
					Bands.Add(new EqBandViewModel(i, OnBandChanged));
				}
			}

			foreach (var band in Bands)
			{
				band.Min = cfg.MinDb;
				band.Max = cfg.MaxDb;
				band.Label = snapshot.TryGet(Keys.EqBandFreq(band.Index), out var hz) ? FormatHz(hz) : $"Band {band.Index + 1}";
			}
		}

		if (!_pending && !_busy && snapshot.TryGet(Keys.EqAllGains, out var gains))
		{
			for (var i = 0; i < Math.Min(gains.Count, Bands.Count); i++)
			{
				Bands[i].SetSilently(gains[i]);
			}
		}

		RefreshFlags();
	}

	protected override void OnDisposed()
	{
		_debounce?.Cancel();
		Mode.Dispose();
		Bass.Dispose();
	}

	private static string FormatHz(int hz) =>
		hz >= 1000 ? string.Create(CultureInfo.InvariantCulture, $"{hz / 1000.0:0.#} kHz") : string.Create(CultureInfo.InvariantCulture, $"{hz} Hz");

	private void RefreshFlags()
	{
		var enabled = EqEnabled;
		foreach (var band in Bands)
		{
			band.IsEnabled = enabled;
		}

		OnPropertyChanged(nameof(ModeIndex));
		OnPropertyChanged(nameof(IsPersonalized));
		OnPropertyChanged(nameof(EqEnabled));
	}

	private void OnBandChanged(EqBandViewModel band)
	{
		_pending = true;
		_debounce?.Cancel();
		var cts = _debounce = new CancellationTokenSource();
		_ = ApplyAfterDelayAsync(cts.Token);
	}

	private async Task ApplyAfterDelayAsync(CancellationToken ct)
	{
		try
		{
			await Task.Delay(ApplyDelayMs, ct).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		_pending = false;
		_busy = true;
		RefreshFlags();
		try
		{
			var result = await _services.Settings.SetEqAsync([.. Bands.Select(b => b.Gain)], CancellationToken.None).ConfigureAwait(true);
			for (var i = 0; i < Math.Min(result.Actual.Count, Bands.Count); i++)
			{
				Bands[i].SetSilently(result.Actual[i]);
			}

			Warning = result.Verified ? null : "The headset reports different gains than written; the values shown are what it holds.";
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_services.ReportError($"Equalizer: {ex.Message}");
			OnStateChanged(_services.Store.Current);
		}
		finally
		{
			_busy = false;
			RefreshFlags();
		}
	}
}
