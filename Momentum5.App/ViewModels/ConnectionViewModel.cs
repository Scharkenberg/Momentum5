using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Core.Transport;

namespace Momentum5.App.ViewModels;

public sealed record DeviceItem(DeviceIdentity Identity, bool IsTarget)
{
	public string Name => Identity.Name ?? "(unnamed device)";

	public string AddressText => AppServices.FormatAddress(Identity.Address);

	public string StateText => Identity.IsConnected ? "Connected to Windows" : "Not connected";

	public string TargetText => IsTarget ? "Active headset" : string.Empty;
}

public sealed class ConnectionViewModel : ObservableObject, IDisposable
{
	private readonly AppServices _services;
	private DeviceItem? _selected;

	public ConnectionViewModel(AppServices services)
	{
		ArgumentNullException.ThrowIfNull(services);
		_services = services;
		Status = new ConnectionStatusViewModel(services);
		RefreshCommand = new AsyncRelayCommand(RefreshAsync);
		ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _selected is not null);
		DisconnectCommand = new RelayCommand(services.Disconnect);
		ForgetCommand = new RelayCommand(Forget);
		Status.PropertyChanged += OnStatusChanged;
		services.SettingsChanged += OnSettingsChanged;
		services.PairedDevicesChanged += OnPairedChanged;
		_ = RefreshAsync();
	}

	public ConnectionStatusViewModel Status { get; }

	public ObservableCollection<DeviceItem> Devices { get; } = [];

	public IAsyncRelayCommand RefreshCommand { get; }

	public IAsyncRelayCommand ConnectCommand { get; }

	public IRelayCommand DisconnectCommand { get; }

	public IRelayCommand ForgetCommand { get; }

	public DeviceItem? SelectedDevice
	{
		get => _selected;
		set
		{
			if (SetProperty(ref _selected, value))
			{
				ConnectCommand.NotifyCanExecuteChanged();
			}
		}
	}

	public bool AutoConnect
	{
		get => _services.Current.AutoConnect;
		set
		{
			if (value != _services.Current.AutoConnect)
			{
				_services.SetAutoConnect(value);
			}
		}
	}

	public bool RadioOff => Status.Phase == SessionPhase.RadioOff;

	public bool CanDisconnect => Status.Phase is SessionPhase.Ready or SessionPhase.ReadOnly or SessionPhase.Connecting or SessionPhase.Backoff;

	public string LastAddressText => _services.Current.LastDeviceAddress is { } a
		? $"Remembered headset: {AppServices.FormatAddress(a)}"
		: "No headset remembered.";

	public void Dispose()
	{
		Status.PropertyChanged -= OnStatusChanged;
		_services.SettingsChanged -= OnSettingsChanged;
		_services.PairedDevicesChanged -= OnPairedChanged;
		Status.Dispose();
	}

	private async Task RefreshAsync()
	{
		try
		{
			var paired = await _services.GetPairedAsync().ConfigureAwait(true);
			var target = _services.Supervisor.Target?.Address;
			var selectedAddress = _selected?.Identity.Address;
			Devices.Clear();
			foreach (var d in paired
				.OrderByDescending(d => d.Address == target)
				.ThenByDescending(d => d.Name?.Contains("MOMENTUM", StringComparison.OrdinalIgnoreCase) == true)
				.ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
			{
				Devices.Add(new DeviceItem(d, d.Address == target));
			}

			SelectedDevice = Devices.FirstOrDefault(d => d.Identity.Address == selectedAddress) ?? Devices.FirstOrDefault(d => d.IsTarget);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_services.ReportError($"Could not list paired devices: {ex.Message}");
		}
	}

	private async Task ConnectAsync()
	{
		if (_selected is null)
		{
			return;
		}

		await _services.SelectDeviceAsync(_selected.Identity).ConfigureAwait(true);
		_services.Connect();
		await RefreshAsync().ConfigureAwait(true);
	}

	private void Forget()
	{
		_services.ForgetAssociation();
		OnPropertyChanged(nameof(LastAddressText));
	}

	private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ConnectionStatusViewModel.Phase))
		{
			OnPropertyChanged(nameof(RadioOff));
			OnPropertyChanged(nameof(CanDisconnect));
			_ = RefreshAsync();
		}
	}

	private void OnSettingsChanged(object? sender, EventArgs e)
	{
		OnPropertyChanged(nameof(AutoConnect));
		OnPropertyChanged(nameof(LastAddressText));
	}

	private void OnPairedChanged(object? sender, EventArgs e) => _services.Ui.Post(() => _ = RefreshAsync());
}
