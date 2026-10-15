using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

/// <summary>Owns the <see cref="SettingViewModel{T}"/> instances a page creates and disposes them with the page.</summary>
public abstract class PageViewModelBase : ViewModelBase
{
	private readonly List<IDisposable> _owned = [];

	protected PageViewModelBase(AppServices services)
		: base(services?.Store ?? throw new ArgumentNullException(nameof(services)), services.Ui) => Services = services;

	protected AppServices Services { get; }

	protected SettingViewModel<T> Make<T>(SettingKey<T> key, Action<string>? error = null, int debounceMs = 300)
	{
		var vm = new SettingViewModel<T>(key, Services.Settings, Services.Store, Services.Ui, error ?? Services.ReportError) { DebounceMs = debounceMs };
		_owned.Add(vm);
		return vm;
	}

	protected override void OnStateChanged(HeadsetSnapshot snapshot)
	{
	}

	protected override void OnDisposed()
	{
		foreach (var d in _owned)
		{
			d.Dispose();
		}
	}
}

/// <summary>U-08: immersive audio, head tracking, battery protection.</summary>
public sealed class SpatialViewModel : PageViewModelBase
{
	private string? _banner;

	public SpatialViewModel(AppServices services)
		: base(services)
	{
		Immersive = Make(Keys.ImmersiveAudio, ShowBanner);
		HeadTracking = Make(Keys.HeadTracking);
		Immersive.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Immersive.Value))
			{
				OnPropertyChanged(nameof(HeadTrackingHint));
				Banner = null;
			}
		};
	}

	public SettingViewModel<bool> Immersive { get; }

	public SettingViewModel<bool> HeadTracking { get; }

	public string? Banner
	{
		get => _banner;
		private set
		{
			if (SetProperty(ref _banner, value))
			{
				OnPropertyChanged(nameof(HasBanner));
			}
		}
	}

	public bool HasBanner => _banner is not null;

	public string HeadTrackingHint => Immersive.Value ? string.Empty : "Turn on immersive audio to use head tracking.";

	private void ShowBanner(string message)
	{
		Banner = message.Contains("Personalization", StringComparison.OrdinalIgnoreCase)
			? $"Immersive audio was refused while Sound Personalization is active. Headset said: {message}"
			: message;
		Services.ReportError(message);
	}
}

public sealed record InfoRow(string Label, string Value);

/// <summary>U-10 (device): info table and rename (UTF-8, at most 16 bytes).</summary>
public sealed partial class DeviceViewModel : PageViewModelBase
{
	public const int MaxNameBytes = 16;
	private string _nameInput = string.Empty;
	private string _message = string.Empty;
	private bool _busy;

	public DeviceViewModel(AppServices services)
		: base(services)
	{
		_nameInput = CurrentName;
		services.SettingsChanged += OnSettingsChanged;
		RenameCommand = new AsyncRelayCommand(RenameAsync, () => CanRename);
		RefreshCommand = new AsyncRelayCommand(RefreshDeviceAsync);
		_ = RefreshDeviceAsync();
		Rebuild();
	}

	public ObservableCollection<InfoRow> Info { get; } = [];

	private void OnSettingsChanged(object? sender, EventArgs e) => Rebuild();

	protected override void OnDisposed()
	{
		Services.SettingsChanged -= OnSettingsChanged;
		base.OnDisposed();
	}

	public IAsyncRelayCommand RenameCommand { get; }

	public IAsyncRelayCommand RefreshCommand { get; }

	public string CurrentName => Services.Store.Current.TryGet(Keys.LocalName, out var n) ? n : string.Empty;

	public string NameInput
	{
		get => _nameInput;
		set
		{
			if (SetProperty(ref _nameInput, value ?? string.Empty))
			{
				OnPropertyChanged(nameof(ByteCountText));
				OnPropertyChanged(nameof(CanRename));
				RenameCommand.NotifyCanExecuteChanged();
			}
		}
	}

	public int ByteCount => Encoding.UTF8.GetByteCount(_nameInput);

	public string ByteCountText => $"{ByteCount} / {MaxNameBytes} bytes";

	public bool CanRename => !_busy && Services.Store.Current.Session is { Trust: not TrustTier.Unknown }
		&& ByteCount is > 0 and <= MaxNameBytes && _nameInput != CurrentName && Services.Store.Current.TryGet(Keys.LocalName, out _);

	public string Message
	{
		get => _message;
		private set => SetProperty(ref _message, value);
	}

	private async Task RefreshDeviceAsync()
	{
		try
		{
			await Services.Settings.RefreshAsync(SettingGroup.Identity, CancellationToken.None).ConfigureAwait(true);
			await Services.Settings.RefreshAsync(SettingGroup.Device, CancellationToken.None).ConfigureAwait(true);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Message = $"Refresh failed: {ex.Message}";
		}
	}

	protected override void OnStateChanged(HeadsetSnapshot snapshot)
	{
		if (_nameInput.Length == 0 && CurrentName.Length > 0)
		{
			NameInput = CurrentName;
		}

		Rebuild();
		OnPropertyChanged(nameof(CurrentName));
		OnPropertyChanged(nameof(CanRename));
		RenameCommand.NotifyCanExecuteChanged();
	}

	private void Rebuild()
	{
		var s = Services.Store.Current;
		var session = s.Session;
		string Hex(SettingKey<byte[]> k) => s.TryGet(k, out var b) && b.Length > 0 ? Convert.ToHexString(b) : "—";
		var rows = new List<InfoRow>
		{
			new("Model", session?.Model ?? "—"),
			new("Firmware", session?.Firmware ?? "—"),
			new("Hardware revision", Hex(Keys.HwRevision)),
			new("Serial", session?.Serial is { Length: > 0 } sn ? sn : "—"),
			new("Features", session is { Features.Count: > 0 } ? string.Join(", ", session.Features) : "—"),
			new("Trust", session?.Trust.ToString() ?? "—"),
			new("Name on headset", CurrentName is { Length: > 0 } n ? n : "—"),
		};
		if (Services.Current.DevModeUnlocked)
		{
			rows.Insert(6, new("Transport", session?.TransportId ?? "—"));
		}

		Info.Clear();
		foreach (var r in rows)
		{
			Info.Add(r);
		}
	}

	/// <summary>Called by the page after the user confirmed the dialog.</summary>
	public async Task RenameAsync()
	{
		if (!CanRename)
		{
			return;
		}

		_busy = true;
		RenameCommand.NotifyCanExecuteChanged();
		try
		{
			var r = await Services.Settings.SetAsync(Keys.LocalName, _nameInput, CancellationToken.None).ConfigureAwait(true);
			Message = r.Verified ? $"Renamed to '{r.Actual}'." : $"The headset did not accept the new name; it still reports '{r.Actual}'. The M5 rename command may differ from the one used here.";
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Message = $"Rename failed: {ex.Message}";
			Services.ReportError(Message);
		}
		finally
		{
			_busy = false;
			OnPropertyChanged(nameof(CanRename));
			RenameCommand.NotifyCanExecuteChanged();
		}
	}
}

public sealed record SlotItem(string Title, string Detail, bool IsConnected)
{
	public string Check => IsConnected ? "\uE73E" : string.Empty;
}

/// <summary>
/// Multipoint list for the M5, which addresses devices by MAC (1409/1413/140D/140E/1414). Read-only: the connect command of this family is unknown.
/// </summary>
public sealed class MultipointViewModel : PageViewModelBase
{
	private string _message = string.Empty;
	private string _summary = string.Empty;
	private string _trace = string.Empty;
	private bool _busy;

	public MultipointViewModel(AppServices services)
		: base(services) => RefreshCommand = new AsyncRelayCommand(RefreshAsync);

	public ObservableCollection<SlotItem> Slots { get; } = [];

	public IAsyncRelayCommand RefreshCommand { get; }

	public string Message
	{
		get => _message;
		private set => SetProperty(ref _message, value);
	}

	public string Summary
	{
		get => _summary;
		private set => SetProperty(ref _summary, value);
	}

	public string Trace
	{
		get => _trace;
		private set => SetProperty(ref _trace, value);
	}

	public async Task RefreshAsync()
	{
		if (_busy)
		{
			return;
		}

		_busy = true;
		try
		{
			var session = Services.Supervisor.Current;
			if (session is null)
			{
				Message = "Not connected to the headset.";
				return;
			}

			var table = await MultipointReader.ReadAsync(session, CancellationToken.None).ConfigureAwait(true);
			Trace = table.Trace;
			Slots.Clear();
			foreach (var d in table.Devices.OrderByDescending(x => x.IsConnected))
			{
				var own = table.Current is { } c && c == d.Address;
				var name = d.Name.Length > 0 ? d.Name : "(unnamed)";
				var state = d.IsConnected ? "Connected" : "Paired";
				Slots.Add(new SlotItem(own ? $"{name} (this PC)" : name, $"{state} · {AppServices.FormatAddress(d.Address)}", d.IsConnected));
			}

			Summary = table.MaxConnections is { } m ? $"Multipoint: up to {m} devices at once · {table.Devices.Count} paired" : $"{table.Devices.Count} paired device(s)";
			Message = table.Devices.Count == 0 ? "The headset returned no devices; see the raw trace below." : string.Empty;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Message = $"Could not read the table: {ex.Message}";
			Services.ReportError(Message);
		}
		finally
		{
			_busy = false;
		}
	}
}
