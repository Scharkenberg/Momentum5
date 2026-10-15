using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Shell;
using Momentum5.Client;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;

namespace Momentum5.App.ViewModels;

/// <summary>
/// X-04 parametric EQ page: a read-dump of the per-band table — band number, centre frequency
/// (EqBandFreq, "90 Hz" labels like the sound page) and the raw Q (100D) and filter-type (100F) bytes.
/// The PEQ write commands are not confirmed for this firmware yet (research lanes R-04/R-05 pending),
/// so this view model never writes; a firmware rejection with error 00/01 latches a read-only note and
/// stops the per-band reads on later refreshes.
/// </summary>
public sealed partial class ParametricEqViewModel : ShellViewModelBase
{
	private readonly ISettingService _service;
	private bool _peqRefreshed;
	private bool _peqUnavailable;

	public ParametricEqViewModel(IUiDispatcher dispatcher, IHeadsetState state, ISettingService service)
		: base(dispatcher, state)
	{
		_service = service;
		OnSnapshot(state.Current);
	}

	/// <summary>Band rows; rebuilt when the band count changes, otherwise synced in place.</summary>
	public ObservableCollection<PeqBandVm> Bands { get; } = [];

	[ObservableProperty]
	public partial bool IsBusy { get; private set; }

	[ObservableProperty]
	public partial bool IsAvailable { get; private set; }

	/// <summary>Sticky warning for the last failed refresh or per-band read; null while healthy.</summary>
	[ObservableProperty]
	public partial string? Warning { get; private set; }

	/// <summary>Latched when the firmware rejected the PEQ reads (error 00/01): the panel stays read-only.</summary>
	[ObservableProperty]
	public partial string? ReadOnlyNote { get; private set; }

	[ObservableProperty]
	public partial string EqSummary { get; private set; } = string.Empty;

	public string? WaitingText => Bands.Count > 0 ? null : "Waiting for the headset equalizer data…";

	/// <summary>Page Loaded path (the seed every page uses): one Eq-group refresh plus the per-band reads.</summary>
	public Task InitializeAsync() => RefreshAsync();

	/// <summary>
	/// "Read from device": refreshes the Eq group (config, gains, centre frequencies — plus the per-band
	/// params the service loop fills), then live-reads any band parameters still missing so a 00/01
	/// rejection can be detected and latched.
	/// </summary>
	public async Task RefreshAsync()
	{
		if (IsBusy)
		{
			return;
		}

		IsBusy = true;
		Warning = null;
		try
		{
			try
			{
				await _service.RefreshAsync(SettingGroup.Eq, CancellationToken.None).ConfigureAwait(true);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Warning = ex.Message;
			}

			await ReadBandParamsAsync().ConfigureAwait(true);
		}
		finally
		{
			IsBusy = false;
		}
	}

	protected override void OnSnapshot(HeadsetSnapshot snapshot)
	{
		IsAvailable = snapshot.Session is not null;
		SyncBands(snapshot);
		if (snapshot.Session is not null && !_peqRefreshed && Bands.Count == 0)
		{
			// One extra Eq refresh after connect: it fills the band count, centre frequencies and band params.
			_peqRefreshed = true;
			_ = RefreshAsync();
		}
	}

	/// <summary>
	/// Live per-band reads of the raw Q (100D) and filter-type (100F) keys. Values already present in the
	/// snapshot are skipped (the keys are read-only, so a landed value never changes). The first 00/01
	/// rejection latches <see cref="ReadOnlyNote"/> and stops all later attempts; session-level failures
	/// abort with a warning; other per-key rejections leave the row at "—" and the dump continues.
	/// </summary>
	private async Task ReadBandParamsAsync()
	{
		if (_peqUnavailable)
		{
			return;
		}

		var config = Snapshot.Get(Keys.EqConfig);
		if (config is null || Snapshot.Session is null)
		{
			return;
		}

		var attempted = 0;
		var failed = 0;
		foreach (var key in BandParamKeys(config.Bands))
		{
			if (Snapshot.TryGet(key, out _))
			{
				continue;
			}

			attempted++;
			try
			{
				// The decoded value lands in the store, so OnSnapshot pushes it into the row.
				await _service.GetAsync(key, CancellationToken.None).ConfigureAwait(true);
			}
			catch (GaiaProtocolException ex) when (
				ex.KnownReason is GaiaErrorReason.FeatureUnsupported or GaiaErrorReason.OperationUnsupported)
			{
				_peqUnavailable = true;
				ReadOnlyNote = "The headset does not expose the parametric EQ — the panel stays read-only.";
				return;
			}
			catch (Exception ex) when (ex is TimeoutException or NotConnectedException or LinkException)
			{
				Warning = ex.Message;
				return;
			}
			catch (Exception ex) when (ex is GaiaProtocolException or GaiaDecodeException or EqBandOutOfRangeException)
			{
				failed++;
				AppLog.StaticWrite($"peq: {key.Name} ({key.Index}) not read: {ex.Message}");
			}
		}

		if (failed > 0)
		{
			Warning = FormattableString.Invariant($"{failed} of {attempted} per-band values were not returned by the headset.");
		}
	}

	/// <summary>Rebuilds the rows when the band count changes, otherwise syncs the values in place.</summary>
	private void SyncBands(HeadsetSnapshot snapshot)
	{
		var count = snapshot.Get(Keys.EqConfig)?.Bands ?? 0;
		if (count <= 0)
		{
			EqSummary = string.Empty;
			if (Bands.Count > 0)
			{
				Bands.Clear();
				OnPropertyChanged(nameof(WaitingText));
			}

			return;
		}

		EqSummary = FormattableString.Invariant($"{count} bands · raw per-band parameters");
		if (Bands.Count != count)
		{
			Bands.Clear();
			for (var band = 0; band < count; band++)
			{
				Bands.Add(new PeqBandVm(band));
			}

			OnPropertyChanged(nameof(WaitingText));
		}

		for (var band = 0; band < count; band++)
		{
			var row = Bands[band];
			row.SetFrequency(FrequencyText(snapshot, band));
			row.SetQ(RawText(snapshot, Keys.EqBandQ(band)));
			row.SetFilter(RawText(snapshot, Keys.EqBandFilterType(band)));
		}
	}

	// Same enumeration SettingService.Groups uses for its Eq-group refresh (private there, so a local copy).
	private static IEnumerable<SettingKey<int>> BandParamKeys(int bands)
	{
		for (var band = 0; band < bands; band++)
		{
			yield return Keys.EqBandQ(band);
			yield return Keys.EqBandFilterType(band);
		}
	}

	private static string FrequencyText(HeadsetSnapshot snapshot, int band)
	{
		if (snapshot.TryGet(Keys.EqBandFreq(band), out var hz) && hz > 0)
		{
			return hz >= 1000
				? FormattableString.Invariant($"{hz / 1000.0:0.#} kHz")
				: FormattableString.Invariant($"{hz} Hz");
		}

		return "—";
	}

	private static string RawText(HeadsetSnapshot snapshot, SettingKey<int> key) =>
		snapshot.TryGet(key, out var raw)
			? FormattableString.Invariant($"{raw} (0x{raw:X2})")
			: "—";
}

/// <summary>One parametric-EQ band row: band number, centre frequency, raw Q byte and raw filter-type byte.</summary>
public sealed class PeqBandVm : INotifyPropertyChanged
{
	private string _frequencyText = "—";
	private string _qText = "—";
	private string _filterText = "—";

	public PeqBandVm(int band) => Band = band;

	public event PropertyChangedEventHandler? PropertyChanged;

	public int Band { get; }

	/// <summary>1-based band number for the table column.</summary>
	public string BandText => FormattableString.Invariant($"{Band + 1}");

	/// <summary>Centre frequency ("90 Hz" style) or "—" while unknown.</summary>
	public string FrequencyText => _frequencyText;

	/// <summary>Raw Q byte as "decimal (0x hex)" or "—" while the value is unknown or rejected.</summary>
	public string QText => _qText;

	/// <summary>Raw filter-type byte as "decimal (0x hex)" or "—" while the value is unknown or rejected.</summary>
	public string FilterText => _filterText;

	/// <summary>Pushes a snapshot-derived frequency into the row.</summary>
	public void SetFrequency(string text) => Set(ref _frequencyText, text, nameof(FrequencyText));

	/// <summary>Pushes a snapshot-derived raw Q value into the row.</summary>
	public void SetQ(string text) => Set(ref _qText, text, nameof(QText));

	/// <summary>Pushes a snapshot-derived raw filter type into the row.</summary>
	public void SetFilter(string text) => Set(ref _filterText, text, nameof(FilterText));

	private void Set(ref string field, string text, string name)
	{
		if (field == text)
		{
			return;
		}

		field = text;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
