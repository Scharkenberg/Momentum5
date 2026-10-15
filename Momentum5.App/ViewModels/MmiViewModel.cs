using System;
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
using Momentum5.Protocol.Bindings;

namespace Momentum5.App.ViewModels;

/// <summary>
/// X-05 MMI (touch/button) configuration — read-only. The 1601/1605 payload format is not decoded (R-03/R-04),
/// so the table shows the RAW reply bytes per probed slot; a slot whose GET answers a GAIA error shows
/// "not present", and a full-table degrade note appears when every slot errors. The editor (SET 1600) is
/// deferred until the R-04 capture and the 1604 reset stays a Blocked raw row (GAIA console only).
/// </summary>
public sealed partial class MmiViewModel : ShellViewModelBase
{
	private readonly ISettingService _service;
	private bool _readQueued;
	private bool _reading;

	public MmiViewModel(IUiDispatcher dispatcher, IHeadsetState state, ISettingService service)
		: base(dispatcher, state)
	{
		_service = service;
		for (var slot = 0; slot < MmiBindings.SlotCount; slot++)
		{
			Rows.Add(new MmiSlotVm(slot));
		}

		OnSnapshot(State.Current);
	}

	/// <summary>One row per probed MMI slot (GET 1601 selector 00 0N); values update in place.</summary>
	public ObservableCollection<MmiSlotVm> Rows { get; } = [];

	/// <summary>Raw reply of GET 1605 — "—" before the first read, hex after, "not present" on an error.</summary>
	[ObservableProperty]
	public partial string DefaultMappingText { get; private set; } = "—";

	/// <summary>True while the slot reads are in flight.</summary>
	[ObservableProperty]
	public partial bool IsBusy { get; private set; }

	/// <summary>True while a session is up (drives the Read button and the empty-state hint).</summary>
	[ObservableProperty]
	public partial bool IsAvailable { get; private set; }

	/// <summary>Every probed slot answered an error: the mapping table is not readable at all.</summary>
	[ObservableProperty]
	public partial bool AllSlotsAbsent { get; private set; }

	/// <summary>Sticky page-level warning (link loss, timeout, no session); null while healthy.</summary>
	[ObservableProperty]
	public partial string? Warning { get; private set; }

	/// <summary>
	/// Seeds the page when it is opened after the connect refresh (snapshot events only reach VMs created
	/// before they fired). MMI has no push words, so later updates need an explicit read.
	/// </summary>
	public async Task InitializeAsync()
	{
		if (!IsAvailable)
		{
			return; // no session yet: the page waits for the next snapshot (or the Read button)
		}

		await ReadAsync().ConfigureAwait(true);
	}

	/// <summary>
	/// "Read from device": live GETs of every MMI slot plus 1605 (the house GetAsync path — the values also
	/// land in the snapshot, so OnSnapshot mirrors them into the rows). A GAIA error reply marks that slot
	/// "not present" instead of failing the page; a link error stops the loop and surfaces as a warning.
	/// </summary>
	public async Task ReadAsync()
	{
		if (!IsAvailable)
		{
			Warning = "Not connected to the headphones.";
			return;
		}

		if (_reading)
		{
			_readQueued = true;
			return;
		}

		_reading = true;
		IsBusy = true;
		string? linkWarning = null;
		try
		{
			var absent = 0;
			foreach (var row in Rows)
			{
				try
				{
					var raw = await _service.GetAsync(Keys.MmiConfig(row.Slot), CancellationToken.None).ConfigureAwait(true);
					row.SetValue(raw);
				}
				catch (Exception ex) when (ex is GaiaProtocolException or GaiaDecodeException or ArgumentException)
				{
					// GAIA error reply (slot not present on this headset) — or a binding that is not
					// registered yet: either way this slot has no mapping to show.
					row.MarkNotPresent();
					absent++;
				}
				catch (Exception ex) when (ex is NotConnectedException or TimeoutException or LinkException)
				{
					linkWarning = ex.Message;
					break; // the link is down: keep the last known rows, stop hammering the pipeline
				}
			}

			if (linkWarning is null)
			{
				try
				{
					var isDefault = await _service.GetAsync(Keys.MmiIsDefault, CancellationToken.None).ConfigureAwait(true);
					DefaultMappingText = FormatRaw(isDefault);
				}
				catch (Exception ex) when (ex is GaiaProtocolException or GaiaDecodeException or ArgumentException)
				{
					DefaultMappingText = "not present";
				}
				catch (Exception ex) when (ex is NotConnectedException or TimeoutException or LinkException)
				{
					linkWarning = ex.Message;
				}
			}

			Warning = linkWarning;
			AllSlotsAbsent = Rows.Count > 0 && absent >= Rows.Count;
		}
		finally
		{
			_reading = false;
			IsBusy = false;
			if (_readQueued)
			{
				_readQueued = false;
				_ = ReadAsync();
			}
		}
	}

	protected override void OnSnapshot(HeadsetSnapshot snapshot)
	{
		IsAvailable = snapshot.Session is not null;

		// Rows update in place from the store mirror (our own reads write the snapshot there too).
		foreach (var row in Rows)
		{
			var raw = snapshot.Get(Keys.MmiConfig(row.Slot));
			if (raw is { Length: > 0 })
			{
				row.SetValue(raw);
			}
		}

		var isDefault = snapshot.Get(Keys.MmiIsDefault);
		if (isDefault is { Length: > 0 })
		{
			DefaultMappingText = FormatRaw(isDefault);
		}
	}

	private static string FormatRaw(byte[] raw) => raw.Length == 0 ? "(empty)" : GaiaFormatter.Hex(raw);
}

/// <summary>One probed MMI slot row: "Slot #N" and the raw reply bytes (or "not present").</summary>
public sealed class MmiSlotVm : INotifyPropertyChanged
{
	private string _valueText = "—";

	public MmiSlotVm(int slot)
	{
		Slot = slot;
		SlotText = FormattableString.Invariant($"Slot #{slot}");
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Slot index sent as the second GET 1601 selector byte (selector 00 0N).</summary>
	public int Slot { get; }

	/// <summary>"Slot #2".</summary>
	public string SlotText { get; }

	/// <summary>Raw reply hex ("00 01 01"), "—" before the first read, "not present" after a GAIA error.</summary>
	public string ValueText
	{
		get => _valueText;
		private set
		{
			if (_valueText == value)
			{
				return;
			}

			_valueText = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
		}
	}

	/// <summary>Shows a successful raw reply (empty replies render as "(empty)").</summary>
	public void SetValue(byte[] raw) => ValueText = raw.Length == 0 ? "(empty)" : GaiaFormatter.Hex(raw);

	/// <summary>The slot's GET answered an error: no mapping is present for it.</summary>
	public void MarkNotPresent() => ValueText = "not present";
}
