using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Services;
using Momentum5.Client;

namespace Momentum5.App.ViewModels;

public enum StatusKind
{
	Neutral,
	Good,
	Warning,
	Error,
}

/// <summary>Text and severity for the shell's status chip, driven by the supervisor (countdown ticks once per second while backing off).</summary>
public sealed class ConnectionStatusViewModel : ObservableObject, IDisposable
{
	private readonly AppServices _services;
	private readonly Timer _timer;
	private readonly UiCoalescer _refresh;
	private bool _disposed;
	private SessionState _state;
	private string _text = "Not connected";
	private string? _detail;
	private string _chipText = "Not connected";
	private string _phaseText = "Not connected";
	private string _subText = string.Empty;
	private StatusKind _kind;

	public ConnectionStatusViewModel(AppServices services)
	{
		ArgumentNullException.ThrowIfNull(services);
		_services = services;
		_state = services.Supervisor.State;
		services.Supervisor.StateChanged += OnState;
		services.Store.Changed += OnStore;
		services.SettingsChanged += OnSettings;
		_refresh = new UiCoalescer(services.Ui, Refresh);
		// The countdown only ticks while backing off; otherwise no timer thread wakes up at all.
		_timer = new Timer(_ => _refresh.Request(), null, Timeout.Infinite, Timeout.Infinite);
		Refresh();
	}

	public string Text
	{
		get => _text;
		private set => SetProperty(ref _text, value);
	}

	/// <summary>Device name when connected, otherwise the status text.</summary>
	public string ChipText
	{
		get => _chipText;
		private set => SetProperty(ref _chipText, value);
	}

	/// <summary>Second line under <see cref="ChipText"/>: the full status when connected, otherwise the detail.</summary>
	public string SubText
	{
		get => _subText;
		private set => SetProperty(ref _subText, value);
	}

	/// <summary>Short phase label ("Connected", "Connecting…", …) without model or transport.</summary>
	public string PhaseText
	{
		get => _phaseText;
		private set => SetProperty(ref _phaseText, value);
	}

	public string? Detail
	{
		get => _detail;
		private set => SetProperty(ref _detail, value);
	}

	public StatusKind Kind
	{
		get => _kind;
		private set => SetProperty(ref _kind, value);
	}

	public SessionPhase Phase => _state.Phase;

	public void Dispose()
	{
		_disposed = true;
		_services.Supervisor.StateChanged -= OnState;
		_services.Store.Changed -= OnStore;
		_services.SettingsChanged -= OnSettings;
		_timer.Dispose();
	}

	// The headset name (and rename) arrives through the state store, not the supervisor: refresh with it, like the hero card does.
	private void OnStore(object? sender, Momentum5.Core.Settings.HeadsetSnapshot snapshot) => _refresh.Request();

	private void OnSettings(object? sender, EventArgs e) => Refresh();

	private void OnState(object? sender, SessionState state)
	{
		_state = state;
		_refresh.Request();
	}

	private void Refresh()
	{
		if (_disposed)
		{
			return;
		}

		var info = _services.Supervisor.Current?.Info ?? _services.Store.Current.Session;
		var model = info?.Model ?? "headset";
		(Text, Kind) = _state.Phase switch
		{
			SessionPhase.Idle => ("Not connected", StatusKind.Neutral),
			SessionPhase.Connecting => ("Connecting…", StatusKind.Warning),
			SessionPhase.Backoff => ($"Reconnecting in {Math.Max(0, (int)Math.Ceiling(((_state.RetryDue ?? DateTimeOffset.Now) - DateTimeOffset.Now).TotalSeconds))} s", StatusKind.Warning),
			SessionPhase.Ready => (_services.Current.DevModeUnlocked ? $"Connected · {model} · {info?.TransportId}" : $"Connected · {model}", StatusKind.Good),
			SessionPhase.ReadOnly => ($"Read-only · {model}", StatusKind.Warning),
			SessionPhase.Unsupported => ("Unsupported device", StatusKind.Error),
			SessionPhase.RadioOff => ("Bluetooth is off", StatusKind.Error),
			_ => ("Unknown", StatusKind.Neutral),
		};
		Detail = _state.Detail;
		PhaseText = _state.Phase is SessionPhase.Ready ? "Connected" : Text;
		SubText = _state.Phase is SessionPhase.Ready or SessionPhase.ReadOnly ? Text : Detail ?? string.Empty;
		var name = _services.Store.Current.TryGet(Momentum5.Core.Settings.Keys.LocalName, out var local) && local.Length > 0 ? local : info?.Model;
		ChipText = _state.Phase is SessionPhase.Ready or SessionPhase.ReadOnly && !string.IsNullOrEmpty(name) ? name : Text;
		OnPropertyChanged(nameof(Phase));
		_timer.Change(_state.Phase == SessionPhase.Backoff ? 1000 : Timeout.Infinite, 1000);
	}
}
