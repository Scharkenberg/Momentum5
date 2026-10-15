using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

public sealed record PresetItem(EqPreset Preset, bool BuiltIn)
{
	public string Name => Preset.Name;

	public string Summary => $"{(BuiltIn ? "Built-in" : "Yours")} · {(Preset.Bass ? "bass boost" : "no bass boost")} · {Preset.Mode} · {string.Join(' ', Preset.Gains.Select(g => g.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture)))}";
}

public sealed class PresetsViewModel : ObservableObject
{
	public static IReadOnlyList<EqPreset> BuiltIns { get; } =
	[
		new("Flat", new double[8], false, SoundMode.GraphicEq),
		new("Bass+", [4, 3.5, 2, 0.5, 0, 0, 0, 0], true, SoundMode.GraphicEq),
		new("Vocal", [-1.5, -1, 0, 1.5, 3, 2, 0.5, -0.5], false, SoundMode.GraphicEq),
		new("Volume floor −4.5 dB (all bands)", [.. Enumerable.Repeat(-4.5, 8)], false, SoundMode.GraphicEq),
	];

	private readonly AppServices _services;
	private readonly PresetStore _store;
	private PresetItem? _selected;
	private string _nameInput = string.Empty;
	private string _message = string.Empty;
	private bool _busy;

	public PresetsViewModel(AppServices services)
	{
		ArgumentNullException.ThrowIfNull(services);
		_services = services;
		_store = new PresetStore(null, services.ReportError);
		ApplyCommand = new AsyncRelayCommand(ApplySelectedAsync, () => _selected is not null && !_busy);
		SaveCurrentCommand = new RelayCommand(SaveCurrent);
		RenameCommand = new RelayCommand(Rename, () => _selected is { BuiltIn: false });
		DeleteCommand = new RelayCommand(Delete, () => _selected is { BuiltIn: false });
		Reload();
	}

	public ObservableCollection<PresetItem> Items { get; } = [];

	public IAsyncRelayCommand ApplyCommand { get; }

	public IRelayCommand SaveCurrentCommand { get; }

	public IRelayCommand RenameCommand { get; }

	public IRelayCommand DeleteCommand { get; }

	public PresetItem? Selected
	{
		get => _selected;
		set
		{
			if (SetProperty(ref _selected, value))
			{
				if (value is { BuiltIn: false })
				{
					NameInput = value.Name;
				}

				ApplyCommand.NotifyCanExecuteChanged();
				RenameCommand.NotifyCanExecuteChanged();
				DeleteCommand.NotifyCanExecuteChanged();
			}
		}
	}

	public string NameInput
	{
		get => _nameInput;
		set => SetProperty(ref _nameInput, value);
	}

	public string Message
	{
		get => _message;
		private set => SetProperty(ref _message, value);
	}

	/// <summary>The selected preset, or all user presets when nothing is selected.</summary>
	public string ExportJson() =>
		PresetCodec.Serialize(_selected is { } s ? [s.Preset] : UserPresets());

	/// <summary>Adds the presets in <paramref name="json"/>; names that already exist are numbered.</summary>
	public int Import(string json)
	{
		try
		{
			var parsed = PresetCodec.Parse(json);
			var user = UserPresets().ToList();
			foreach (var p in parsed)
			{
				user.Add(p with { Name = UniqueName(p.Name, user.Select(x => x.Name)) });
			}

			_store.Save(user);
			Reload();
			Message = $"Imported {parsed.Count} preset(s).";
			return parsed.Count;
		}
		catch (FormatException ex)
		{
			Message = ex.Message;
			return 0;
		}
	}

	public async Task ApplyAsync(EqPreset preset)
	{
		ArgumentNullException.ThrowIfNull(preset);
		var snapshot = _services.Store.Current;
		if (snapshot.Session is null || snapshot.Session.Trust == TrustTier.Unknown)
		{
			Message = "Connect a supported headset first.";
			return;
		}

		if (!snapshot.TryGet(Keys.EqConfig, out var cfg))
		{
			Message = "The headset's equalizer layout is not known yet.";
			return;
		}

		if (preset.Gains.Length != cfg.Bands)
		{
			Message = $"'{preset.Name}' has {preset.Gains.Length} bands; the headset has {cfg.Bands}.";
			return;
		}

		var gains = preset.Gains.Select(g => Math.Round(Math.Clamp(g, cfg.MinDb, cfg.MaxDb), 1)).ToArray();
		_busy = true;
		ApplyCommand.NotifyCanExecuteChanged();
		try
		{
			var mode = preset.Mode == SoundMode.Personalization ? SoundMode.GraphicEq : preset.Mode;
			var s = _services.Settings;
			var modeResult = await s.SetAsync(Keys.SoundMode, mode, CancellationToken.None).ConfigureAwait(true);
			var eq = await s.SetEqAsync(gains, CancellationToken.None).ConfigureAwait(true);
			var bass = await s.SetAsync(Keys.BassBoost, preset.Bass, CancellationToken.None).ConfigureAwait(true);
			Message = modeResult.Verified && eq.Verified && bass.Verified
				? $"Applied '{preset.Name}'."
				: $"Applied '{preset.Name}', but the headset reports different values.";
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Message = $"Could not apply '{preset.Name}': {ex.Message}";
			_services.ReportError(Message);
		}
		finally
		{
			_busy = false;
			ApplyCommand.NotifyCanExecuteChanged();
		}
	}

	private Task ApplySelectedAsync() => _selected is null ? Task.CompletedTask : ApplyAsync(_selected.Preset);

	private void SaveCurrent()
	{
		var snapshot = _services.Store.Current;
		if (!snapshot.TryGet(Keys.EqAllGains, out var gains))
		{
			Message = "Connect the headset first: its current equalizer is needed.";
			return;
		}

		var name = _nameInput.Trim();
		if (!ValidName(name, null))
		{
			return;
		}

		var mode = snapshot.TryGet(Keys.SoundMode, out var m) && m != SoundMode.Personalization ? m : SoundMode.GraphicEq;
		var user = UserPresets().ToList();
		user.Add(new EqPreset(name, [.. gains], snapshot.TryGet(Keys.BassBoost, out var bass) && bass, mode));
		_store.Save(user);
		Reload();
		Message = $"Saved '{name}'.";
	}

	private void Rename()
	{
		if (_selected is not { BuiltIn: false } item)
		{
			return;
		}

		var name = _nameInput.Trim();
		if (!ValidName(name, item.Name))
		{
			return;
		}

		_store.Save(UserPresets().Select(p => p.Name == item.Name ? p with { Name = name } : p));
		Reload();
		Message = $"Renamed to '{name}'.";
	}

	private void Delete()
	{
		if (_selected is not { BuiltIn: false } item)
		{
			return;
		}

		_store.Save(UserPresets().Where(p => p.Name != item.Name));
		Reload();
		Message = $"Deleted '{item.Name}'.";
	}

	private bool ValidName(string name, string? allowed)
	{
		if (name.Length is 0 or > 60)
		{
			Message = "Enter a preset name (1-60 characters).";
			return false;
		}

		if (!string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase)
			&& Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
		{
			Message = $"A preset named '{name}' already exists.";
			return false;
		}

		return true;
	}

	private static string UniqueName(string name, IEnumerable<string> existing)
	{
		var taken = new HashSet<string>(existing.Concat(BuiltIns.Select(b => b.Name)), StringComparer.OrdinalIgnoreCase);
		if (!taken.Contains(name))
		{
			return name;
		}

		for (var n = 2; ; n++)
		{
			var candidate = $"{name} ({n})";
			if (!taken.Contains(candidate))
			{
				return candidate;
			}
		}
	}

	private IReadOnlyList<EqPreset> UserPresets() => _store.Load();

	private void Reload()
	{
		var keep = _selected?.Name;
		Items.Clear();
		foreach (var p in BuiltIns)
		{
			Items.Add(new PresetItem(p, true));
		}

		foreach (var p in _store.Load())
		{
			Items.Add(new PresetItem(p, false));
		}

		Selected = Items.FirstOrDefault(i => i.Name == keep);
	}
}
