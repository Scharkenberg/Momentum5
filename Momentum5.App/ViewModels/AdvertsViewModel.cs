using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum5.App.Services;
using Momentum5.Core.Transport;
using Momentum5.Transport.Ble;

namespace Momentum5.App.ViewModels;

public sealed partial class SightingRow : ObservableObject
{
	public SightingRow(ulong address) => Address = address;

	public ulong Address { get; }

	public string AddressText => BleAddress.Format(Address);

	[ObservableProperty]
	public partial string Name { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string Rssi { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string LastSeen { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string Note { get; set; } = string.Empty;
}

/// <summary>U-15: BLE sightings. Live feed from the app's presence watcher plus an on-demand scan of everything nearby.</summary>
public sealed partial class AdvertsViewModel : ObservableObject, IDisposable
{
	private readonly AppServices _services;
	private CancellationTokenSource? _cts;

	public AdvertsViewModel(AppServices services)
	{
		_services = services;
		ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
		if (services.Adverts is { } adverts)
		{
			adverts.Sighted += OnSighted;
		}

		Status = services.Adverts is null
			? "No BLE watcher in the simulator."
			: "Listening for the headset. It only advertises in pairing mode (Swift Pair beacon); use Scan for everything nearby.";
	}

	public ObservableCollection<SightingRow> Rows { get; } = [];

	public IAsyncRelayCommand ScanCommand { get; }

	[ObservableProperty]
	public partial string Status { get; set; }

	[ObservableProperty]
	public partial bool IsScanning { get; set; }

	partial void OnIsScanningChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

	private SightingRow Row(ulong address)
	{
		var row = Rows.FirstOrDefault(r => r.Address == address);
		if (row is null)
		{
			row = new SightingRow(address);
			Rows.Add(row);
		}

		return row;
	}

	private void OnSighted(object? sender, AdvertisementSighting a) => _services.Ui.Post(() =>
	{
		var row = Row(a.Address);
		row.Name = a.Name ?? row.Name;
		row.Rssi = $"{a.Rssi} dBm";
		row.LastSeen = a.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
		row.Note = "live";
	});

	private async Task ScanAsync()
	{
		IsScanning = true;
		_cts = new CancellationTokenSource();
		Status = "Scanning for 8 s…";
		try
		{
			var list = await GattLab.ScanAsync(TimeSpan.FromSeconds(8), _services.Current.LastDeviceAddress, _cts.Token);
			foreach (var c in list.Where(c => c.Address != 0))
			{
				var row = Row(c.Address);
				row.Name = c.Name ?? row.Name;
				row.Rssi = c.Rssi is { } r ? $"{r} dBm" : row.Rssi;
				row.LastSeen = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
				row.Note = c.Reason.Length > 0 ? c.Reason : c.AdvertisementTypes;
			}

			Status = $"{list.Count} device(s) seen.";
		}
		catch (OperationCanceledException)
		{
			Status = "Cancelled.";
		}
		catch (Exception ex)
		{
			Status = $"Scan failed: {ex.Message}";
		}
		finally
		{
			IsScanning = false;
		}
	}

	public void Dispose()
	{
		_cts?.Cancel();
		if (_services.Adverts is { } adverts)
		{
			adverts.Sighted -= OnSighted;
		}
	}
}
