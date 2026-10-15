using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momentum5.App.Services;
using Momentum5.Client;
using Momentum5.Protocol;

namespace Momentum5.App.ViewModels;

/// <summary>U-14: live GAIA trace over the in-memory ring tap. Appends incrementally so scrolling is never reset.</summary>
public sealed partial class TraceViewModel : ObservableObject, IDisposable
{
	private readonly InMemoryTap _tap;
	private readonly DispatcherQueueTimer _timer;
	private long _lastSequence = -1;
	private long _lastVersion = -1;

	public TraceViewModel(AppServices services)
	{
		ArgumentNullException.ThrowIfNull(services);
		_tap = services.Trace;
		ClearCommand = new RelayCommand(Clear);
		_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_timer.Interval = TimeSpan.FromMilliseconds(400);
		_timer.Tick += (_, _) => Pump();
		Rebuild();
		_timer.Start();
	}

	public ObservableCollection<TraceEntry> Rows { get; } = [];

	public IRelayCommand ClearCommand { get; }

	[ObservableProperty]
	public partial bool IsPaused { get; set; }

	/// <summary>0 = both, 1 = TX, 2 = RX.</summary>
	[ObservableProperty]
	public partial int DirectionIndex { get; set; }

	/// <summary>Case-insensitive substring of "S:0603"-style command or of the formatted line.</summary>
	[ObservableProperty]
	public partial string CommandFilter { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string CountText { get; set; } = string.Empty;

	partial void OnDirectionIndexChanged(int value) => Rebuild();

	partial void OnCommandFilterChanged(string value) => Rebuild();

	private bool Matches(TraceEntry e)
	{
		if (DirectionIndex == 1 && e.Direction != FrameDirection.Tx)
		{
			return false;
		}

		if (DirectionIndex == 2 && e.Direction != FrameDirection.Rx)
		{
			return false;
		}

		var f = CommandFilter.Trim();
		return f.Length == 0 || e.Command.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Text.Contains(f, StringComparison.OrdinalIgnoreCase);
	}

	private void Rebuild()
	{
		Rows.Clear();
		_lastSequence = -1;
		_lastVersion = -1;
		Append(force: true);
	}

	private void Pump()
	{
		if (!IsPaused)
		{
			Append(force: false);
		}
	}

	private void Append(bool force)
	{
		var version = _tap.Version;
		if (!force && version == _lastVersion)
		{
			return;
		}

		_lastVersion = version;
		var all = _tap.Snapshot();
		if (all.Length > 0 && all[^1].Sequence < _lastSequence)
		{
			Rows.Clear();
			_lastSequence = -1;
		}

		foreach (var e in all.Where(e => e.Sequence > _lastSequence))
		{
			_lastSequence = e.Sequence;
			if (Matches(e))
			{
				Rows.Add(e);
			}
		}

		while (Rows.Count > _tap.Capacity)
		{
			Rows.RemoveAt(0);
		}

		CountText = $"{Rows.Count} shown of {all.Length} captured";
	}

	private void Clear()
	{
		_tap.Clear();
		Rebuild();
	}

	public static string ToHexBlock(IEnumerable<TraceEntry> entries) =>
		string.Join(Environment.NewLine, entries.Select(e => $"{(e.Direction == FrameDirection.Tx ? "TX" : "RX")} {e.Hex}"));

	public static string ToLineBlock(IEnumerable<TraceEntry> entries) =>
		string.Join(Environment.NewLine, entries.Select(e => e.Line));

	public void Dispose() => _timer.Stop();
}
