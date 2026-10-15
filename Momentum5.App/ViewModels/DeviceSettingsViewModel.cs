using System;
using Momentum5.App.Services;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

/// <summary>The official app's "Device Settings" list with its dependencies: smart pause, auto-answer and auto power-off need on-head detection; prompt volume and language need prompts.</summary>
public sealed class DeviceSettingsViewModel : PageViewModelBase
{
	private static readonly AutoOffTimer[] Options = [AutoOffTimer.Never, AutoOffTimer.Minutes15, AutoOffTimer.Minutes30, AutoOffTimer.Minutes60];

	public DeviceSettingsViewModel(AppServices services)
		: base(services)
	{
		WearDetection = Make(Keys.WearDetection);
		SmartPause = Make(Keys.SmartPause);
		AutoAnswer = Make(Keys.AutoAnswer);
		AutoOff = Make(Keys.AutoOff);
		ComfortCall = Make(Keys.ComfortCall);
		PromptModeSetting = Make(Keys.PromptMode);
		PromptVolume = Make(Keys.PromptVolume, debounceMs: 300);
		BatteryProtection = Make(Keys.BatteryProtection);
		Touch = Make(Keys.TouchControlsEnabled);
		LowLatency = Make(Keys.LowLatency);
		Watch(WearDetection, nameof(OnHeadOn), nameof(AutoOffEnabled));
		Watch(AutoOff, nameof(AutoOffIndex));
		Watch(PromptModeSetting, nameof(PromptUiIndex), nameof(VolumeEnabled), nameof(LanguageEnabled));
		Watch(Touch, nameof(ControlsOn));
	}

	public SettingViewModel<bool> WearDetection { get; }

	public SettingViewModel<bool> SmartPause { get; }

	public SettingViewModel<bool> AutoAnswer { get; }

	public SettingViewModel<AutoOffTimer> AutoOff { get; }

	public SettingViewModel<bool> ComfortCall { get; }

	public SettingViewModel<PromptMode> PromptModeSetting { get; }

	public SettingViewModel<sbyte> PromptVolume { get; }

	public SettingViewModel<bool> BatteryProtection { get; }

	public SettingViewModel<bool> Touch { get; }

	public SettingViewModel<bool> LowLatency { get; }

	public bool OnHeadOn => WearDetection.Value;

	public bool AutoOffEnabled => AutoOff.IsEnabled && WearDetection.Value;

	public int AutoOffIndex
	{
		get => Math.Max(0, Array.IndexOf(Options, AutoOff.Value));
		set
		{
			if (value >= 0 && value < Options.Length)
			{
				AutoOff.Value = Options[value];
			}
		}
	}

	/// <summary>Display order of the official app: Tone and voice, Tone only, Off.</summary>
	public int PromptUiIndex
	{
		get => 2 - (int)PromptModeSetting.Value;
		set
		{
			if (value is >= 0 and <= 2)
			{
				PromptModeSetting.Value = (PromptMode)(2 - value);
			}
		}
	}

	public bool VolumeEnabled => PromptVolume.IsEnabled && (int)PromptModeSetting.Value != 0;

	public bool LanguageEnabled => (int)PromptModeSetting.Value == 2;

	public string PromptLanguageText =>
		Services.Store.Current.TryGet(Keys.PromptLanguage, out var l) && l.Length > 0 ? Convert.ToHexString(l) : "not reported";

	public bool ControlsOn => Touch.Value;

	public bool LowLatencyVisible => Services.Store.Current.Session is { Trust: TrustTier.Verified } && LowLatency.IsAvailable;

	public string FirmwareText => Services.Store.Current.Session?.Firmware is { Length: > 0 } f ? f : "—";

	public string CodecText => Services.Store.Current.TryGet(Keys.Codec, out var c) && c != 255 ? $"codec 0x{c:X2}" : "No audio stream";

	public string NameText => Services.Store.Current.TryGet(Keys.LocalName, out var n) && n.Length > 0 ? n : "—";

	protected override void OnStateChanged(HeadsetSnapshot snapshot)
	{
		foreach (var name in new[]
		{
			nameof(OnHeadOn), nameof(AutoOffEnabled), nameof(AutoOffIndex), nameof(PromptUiIndex), nameof(VolumeEnabled),
			nameof(LanguageEnabled), nameof(PromptLanguageText), nameof(ControlsOn), nameof(LowLatencyVisible),
			nameof(FirmwareText), nameof(CodecText), nameof(NameText),
		})
		{
			OnPropertyChanged(name);
		}
	}

	private void Watch<T>(SettingViewModel<T> vm, params string[] names) =>
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(vm.Value) or nameof(vm.IsEnabled))
			{
				foreach (var n in names)
				{
					OnPropertyChanged(n);
				}
			}
		};
}
