using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Shell;
using Momentum5.Client;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.App.ViewModels;

/// <summary>Safety-class filter for the catalog browser; values match the page ComboBox item order (X-03).</summary>
public enum ConsoleSafetyFilter
{
	All,
	Read,
	Write,
	Gated,
	Dangerous,
	Blocked,
	Unknown,
}

/// <summary>One sendable command word offered by a catalog row: GET (read), SET (write) or the raw word as listed.</summary>
public sealed record ConsoleSendOption(string Label, ushort Word, bool IsWrite, SafetyClass Safety);

/// <summary>One catalog row (typed binding or raw command). Immutable — selection state lives in the view model.</summary>
public sealed class CatalogRowVm(
	string name,
	string kindText,
	GaiaVendor vendor,
	string vendorText,
	string wordsText,
	SafetyClass safety,
	string safetyText,
	string groupText,
	string noteText,
	string description,
	IReadOnlyList<ConsoleSendOption> options,
	(GaiaVendor Vendor, ushort Word)? blockedKey)
{
	/// <summary>Setting key name (bindings) or the raw row name.</summary>
	public string Name { get; } = name;

	/// <summary>"Typed binding" or "Raw command".</summary>
	public string KindText { get; } = kindText;

	/// <summary>GAIA vendor of the row.</summary>
	public GaiaVendor Vendor { get; } = vendor;

	/// <summary>"S" or "Q" (the trace notation).</summary>
	public string VendorText { get; } = vendorText;

	/// <summary>GET/SET/PUSH words as hex, e.g. "GET 1A05 · SET 1A04 · PUSH 1A85" or just "1604".</summary>
	public string WordsText { get; } = wordsText;

	/// <summary>Row safety class (the binding SET safety, or the raw row class).</summary>
	public SafetyClass Safety { get; } = safety;

	/// <summary>Safety class for display.</summary>
	public string SafetyText { get; } = safetyText;

	/// <summary>Setting group (bindings) or "—".</summary>
	public string GroupText { get; } = groupText;

	/// <summary>Catalog note (raw rows); empty when there is none.</summary>
	public string NoteText { get; } = noteText;

	/// <summary>One-sentence description for the detail panel.</summary>
	public string Description { get; } = description;

	/// <summary>Send variants offered in the sender panel.</summary>
	public IReadOnlyList<ConsoleSendOption> Options { get; } = options;

	/// <summary>Command key when the row's send path is Blocked (drives the per-session unlock flow); null otherwise.</summary>
	public (GaiaVendor Vendor, ushort Word)? BlockedKey { get; } = blockedKey;
}

/// <summary>One console log line (immutable).</summary>
public sealed class ConsoleLogRowVm(string time, string text)
{
	/// <summary>"HH:mm:ss.fff" (local time).</summary>
	public string Time { get; } = time;

	/// <summary>Described TX/RX frame, refusal or error text.</summary>
	public string Text { get; } = text;
}

/// <summary>
/// X-03 GAIA console state: a searchable catalog browser over <see cref="GaiaCatalog"/> (typed bindings +
/// raw rows), a guarded sender and a described TX/RX log. Client-side send rules mirror <see cref="SafetyGate"/>
/// (which re-checks every frame in the pipeline): Read rows send the GET word, Write/Gated rows send the SET
/// word with the typed payload, Dangerous rows are refused outright (consent tokens are issued only by the
/// dedicated Advanced pages), Unknown rows send GET probes with <see cref="CommandOptions.IsProbe"/> and never
/// writes, and Blocked rows need a per-command override unlocked by typing the exact command word once per
/// session (<see cref="ConsentService.CreateOverride"/>; the token is cached in this instance for the session).
/// The log keeps the last <see cref="MaxLogLines"/> lines with <see cref="GaiaFormatter.Describe"/> for frames.
/// Plain <see cref="ObservableObject"/> (no headset state needed — the session comes from
/// <see cref="ConnectionSupervisor"/>), like the other DevTools view models.
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
	/// <summary>Console log capacity; the oldest lines are trimmed.</summary>
	public const int MaxLogLines = 500;

	private readonly ConnectionSupervisor _supervisor;
	private readonly ConsentService _consent;
	private readonly AppLog _log;
	private readonly IUiDispatcher _dispatcher;
	private readonly List<CatalogRowVm> _all = [];
	private readonly Dictionary<(GaiaVendor, ushort), OverrideToken> _overrides = new();
	private string _filterText = string.Empty;
	private ConsoleSafetyFilter _safetyFilter = ConsoleSafetyFilter.All;
	private bool _payloadValid = true;

	public ConsoleViewModel(ConnectionSupervisor supervisor, ConsentService consent, AppLog log, IUiDispatcher dispatcher)
	{
		_supervisor = supervisor;
		_consent = consent;
		_log = log;
		_dispatcher = dispatcher;

		BuildCatalog();
		Rows = BuildRows();
		RowCountText = DescribeRowCount();
		LogLines = [];
		UpdateLogCount();
		AddLine($"Console ready — {GaiaCatalog.EntryCount} catalog entries ({GaiaCatalog.Bindings.Count} bindings, {GaiaCatalog.Raw.Count} raw).");
		ApplySession();
		_supervisor.StateChanged += OnSupervisorStateChanged;
	}

	/// <summary>Visible (filtered) catalog rows; replaced wholesale on filter changes.</summary>
	[ObservableProperty]
	public partial ObservableCollection<CatalogRowVm> Rows { get; private set; }

	/// <summary>"N of M commands".</summary>
	[ObservableProperty]
	public partial string RowCountText { get; private set; } = "0 of 0 commands";

	/// <summary>Row selected in the catalog (TwoWay bound to the ListView); drives the sender panel.</summary>
	[ObservableProperty]
	public partial CatalogRowVm? SelectedRow { get; set; }

	/// <summary>True while a session is live.</summary>
	[ObservableProperty]
	public partial bool IsConnected { get; private set; }

	/// <summary>Session status line ("Not connected." or "Connected · M5AEBT · firmware 6.21.5 · Verified").</summary>
	[ObservableProperty]
	public partial string SessionText { get; private set; } = "Not connected.";

	/// <summary>True while a send is in flight.</summary>
	[ObservableProperty]
	public partial bool IsSending { get; private set; }

	/// <summary>Send variants of the selected row (GET/SET/r word); empty without a selection.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<ConsoleSendOption> Options { get; private set; } = [];

	/// <summary>Selected variant index (OneWay to the RadioButtons; 0 after every selection change).</summary>
	[ObservableProperty]
	public partial int SelectedOptionIndex { get; private set; }

	/// <summary>True while a row is selected.</summary>
	[ObservableProperty]
	public partial bool HasSelection { get; private set; }

	/// <summary>Detail panel: name.</summary>
	[ObservableProperty]
	public partial string DetailName { get; private set; } = string.Empty;

	/// <summary>Detail panel: kind ("Typed binding"/"Raw command").</summary>
	[ObservableProperty]
	public partial string DetailKind { get; private set; } = string.Empty;

	/// <summary>Detail panel: vendor ("S — Sennheiser (0x0495)").</summary>
	[ObservableProperty]
	public partial string DetailVendor { get; private set; } = string.Empty;

	/// <summary>Detail panel: command word(s).</summary>
	[ObservableProperty]
	public partial string DetailWords { get; private set; } = string.Empty;

	/// <summary>Detail panel: safety class.</summary>
	[ObservableProperty]
	public partial string DetailSafety { get; private set; } = string.Empty;

	/// <summary>Detail panel: setting group.</summary>
	[ObservableProperty]
	public partial string DetailGroup { get; private set; } = string.Empty;

	/// <summary>Detail panel: catalog note; empty when none.</summary>
	[ObservableProperty]
	public partial string DetailNote { get; private set; } = string.Empty;

	/// <summary>Detail panel: one-sentence description.</summary>
	[ObservableProperty]
	public partial string DetailDescription { get; private set; } = string.Empty;

	/// <summary>True while the selected row is Dangerous (refused in the console).</summary>
	[ObservableProperty]
	public partial bool ShowDangerNote { get; private set; }

	/// <summary>True while the selected row is Blocked and not yet unlocked this session.</summary>
	[ObservableProperty]
	public partial bool ShowUnlock { get; private set; }

	/// <summary>True once the selected Blocked row has a session override.</summary>
	[ObservableProperty]
	public partial bool ShowUnlockBadge { get; private set; }

	/// <summary>Instruction line for the unlock flow ("Type 1604 exactly to unlock S:1604 for this session.").</summary>
	[ObservableProperty]
	public partial string UnlockPrompt { get; private set; } = string.Empty;

	/// <summary>Typed unlock word (TwoWay); must match the exact command word to unlock.</summary>
	[ObservableProperty]
	public partial string UnlockText { get; set; } = string.Empty;

	/// <summary>True while the typed unlock text matches the selected Blocked command word.</summary>
	[ObservableProperty]
	public partial bool CanUnlock { get; private set; }

	/// <summary>Hex payload to send (TwoWay; "01 A2", "01A2", empty = zero-length).</summary>
	[ObservableProperty]
	public partial string PayloadText { get; set; } = string.Empty;

	/// <summary>Parse error for the payload; null while the payload is valid.</summary>
	[ObservableProperty]
	public partial string? PayloadErrorText { get; private set; }

	/// <summary>Send button state.</summary>
	[ObservableProperty]
	public partial bool CanSend { get; private set; }

	/// <summary>Why Send is currently unavailable ("" when it is available).</summary>
	[ObservableProperty]
	public partial string SendNoteText { get; private set; } = "Select a command from the catalog.";

	/// <summary>Console log lines (oldest first, capped at <see cref="MaxLogLines"/>).</summary>
	[ObservableProperty]
	public partial ObservableCollection<ConsoleLogRowVm> LogLines { get; private set; } = [];

	/// <summary>"N line(s)".</summary>
	[ObservableProperty]
	public partial string LogCountText { get; private set; } = "0 lines";

	/// <summary>Raised on the UI thread after lines were appended — drives log auto-scroll.</summary>
	public event EventHandler<int>? LogAppended;

	/// <summary>Sets the catalog text filter (case-insensitive substring over name/words/vendor/note).</summary>
	public void SetFilter(string? text)
	{
		var value = text ?? string.Empty;
		if (string.Equals(_filterText, value, StringComparison.Ordinal))
		{
			return;
		}

		_filterText = value;
		RebuildRows();
	}

	/// <summary>Sets the safety-class filter from the ComboBox index (0 = All).</summary>
	public void SetSafetyFilter(int index)
	{
		if (index < 0 || index > (int)ConsoleSafetyFilter.Unknown)
		{
			return;
		}

		var value = (ConsoleSafetyFilter)index;
		if (_safetyFilter == value)
		{
			return;
		}

		_safetyFilter = value;
		RebuildRows();
	}

	/// <summary>Selects the send variant from the RadioButtons index (skips binding-driven pushes).</summary>
	public void SetSendOption(int index)
	{
		if (index < 0 || index >= Options.Count || index == SelectedOptionIndex)
		{
			return;
		}

		SelectedOptionIndex = index;
		UpdateSendState();
	}

	/// <summary>
	/// Unlocks the selected Blocked command for this session: issues one override token through
	/// <see cref="ConsentService.CreateOverride"/> and caches it; wrong typed text keeps the row locked.
	/// </summary>
	public void TryUnlock()
	{
		var row = SelectedRow;
		if (row?.BlockedKey is not { } key || _overrides.ContainsKey(key) || !CanUnlock)
		{
			return;
		}

		_overrides[key] = ConsentService.CreateOverride(key.Vendor, key.Word);
		AddLine($"Override unlocked: {VendorShort(row.Vendor)}:{key.Word:X4} is unlocked for this session (sends attach the session override token).");
		_log.Write($"console: session override unlocked for {VendorShort(key.Vendor)}:{key.Word:X4}");
		UpdateUnlockState();
	}

	/// <summary>Sends the selected command word under the console safety rules (see the class summary).</summary>
	public async Task SendAsync()
	{
		if (IsSending)
		{
			return;
		}

		var row = SelectedRow;
		if (row is null)
		{
			AddLine("Select a command from the catalog first.");
			return;
		}

		var option = CurrentOption;
		if (option is null)
		{
			AddLine("Choose a command word first.");
			return;
		}

		if (!TryParseHex(PayloadText, out var payload, out var hexError))
		{
			AddLine(hexError ?? "Invalid hex payload.");
			return;
		}

		var session = _supervisor.Current;
		if (session is null)
		{
			AddLine("Not connected.");
			return;
		}

		// Client-side class rules (the SafetyGate re-checks every frame in the pipeline).
		if (option.Safety == SafetyClass.Dangerous)
		{
			AddLine($"Refused {VendorShort(row.Vendor)}:{option.Word:X4}: Dangerous command — the console never issues consent tokens. Use the dedicated pages (Pairings, Factory reset, Firmware) instead.");
			_log.Write($"console: refused dangerous {VendorShort(row.Vendor)}:{option.Word:X4}");
			return;
		}

		if (option.Safety == SafetyClass.Unknown && option.IsWrite)
		{
			AddLine("Refused: Unknown command: only GET probing is allowed.");
			_log.Write($"console: refused unknown write {VendorShort(row.Vendor)}:{option.Word:X4}");
			return;
		}

		OverrideToken? overrideToken = null;
		if (option.Safety == SafetyClass.Blocked)
		{
			overrideToken = _overrides.GetValueOrDefault((row.Vendor, option.Word));
			if (overrideToken is null)
			{
				AddLine("Refused: Blocked command (DevTools override required). Type the command word below to unlock it for this session.");
				_log.Write($"console: refused blocked {VendorShort(row.Vendor)}:{option.Word:X4} without session override");
				return;
			}
		}

		var sendOptions = new CommandOptions
		{
			IsProbe = option.Safety == SafetyClass.Unknown && !option.IsWrite,
			Override = overrideToken,
		};

		IsSending = true;
		try
		{
			AddLine(GaiaFormatter.Describe(new GaiaFrame(row.Vendor, option.Word, payload), FrameDirection.Tx));
			try
			{
				var reply = await session.Pipeline.ExecuteAsync(row.Vendor, option.Word, payload, sendOptions).ConfigureAwait(true);
				AddLine(GaiaFormatter.Describe(reply, FrameDirection.Rx));
			}
			catch (SafetyDeniedException ex)
			{
				AddLine($"Denied: {ex.Message}");
				_log.Write($"console: denied {VendorShort(row.Vendor)}:{option.Word:X4}: {ex.Message}");
			}
			catch (GaiaProtocolException ex)
			{
				AddLine($"Error reply: {ex.Message}");
			}
			catch (NotConnectedException ex)
			{
				AddLine(ex.Message);
			}
			catch (LinkException ex)
			{
				AddLine(ex.Message);
			}
			catch (TimeoutException ex)
			{
				AddLine(ex.Message);
			}
			catch (OperationCanceledException)
			{
				// cancelled — nothing to report
			}
			catch (Exception ex)
			{
				AddLine($"Failed: {ex.Message}");
				_log.Write($"console: send failed: {ex.Message}");
			}
		}
		finally
		{
			IsSending = false;
			UpdateSendState();
		}
	}

	/// <summary>Clears the console log (refusals already mirrored to the app log stay there).</summary>
	public void ClearLog()
	{
		LogLines.Clear();
		UpdateLogCount();
	}

	/// <summary>Whole console log as text ("HH:mm:ss.fff line" per row) for the clipboard copy.</summary>
	public string GetLogText() => string.Join(Environment.NewLine, LogLines.Select(row => $"{row.Time} {row.Text}"));

	/// <summary>
	/// Parses a console hex payload: "01 A2", "01A2", "01:A2" and an optional "0x" prefix are accepted;
	/// null/whitespace is a valid zero-length payload. Pairs must be complete.
	/// </summary>
	public static bool TryParseHex(string? input, out byte[] payload, out string? error)
	{
		payload = [];
		if (string.IsNullOrWhiteSpace(input))
		{
			error = null;
			return true;
		}

		var digits = new StringBuilder();
		var cleaned = input.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
		foreach (var ch in cleaned)
		{
			if (Uri.IsHexDigit(ch))
			{
				digits.Append(ch);
			}
		}

		if (digits.Length == 0)
		{
			error = "Invalid hex payload — no hex digits found (example: 01 A2).";
			return false;
		}

		if (digits.Length % 2 != 0)
		{
			error = "Invalid hex payload — hex digits must come in pairs (example: 01 A2).";
			return false;
		}

		payload = new byte[digits.Length / 2];
		for (var i = 0; i < payload.Length; i++)
		{
			payload[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
		}

		error = null;
		return true;
	}

	private ConsoleSendOption? CurrentOption =>
		Options.Count > 0 && SelectedOptionIndex >= 0 && SelectedOptionIndex < Options.Count ? Options[SelectedOptionIndex] : null;

	partial void OnSelectedRowChanged(CatalogRowVm? value) => ApplySelection(value);

	partial void OnPayloadTextChanged(string value)
	{
		if (TryParseHex(value, out _, out var error))
		{
			_payloadValid = true;
			PayloadErrorText = null;
		}
		else
		{
			_payloadValid = false;
			PayloadErrorText = error;
		}

		UpdateSendState();
	}

	partial void OnIsSendingChanged(bool value) => UpdateSendState();

	partial void OnUnlockTextChanged(string value) => UpdateUnlockMatch();

	partial void OnIsConnectedChanged(bool value) => UpdateSendState();

	private void OnSupervisorStateChanged(object? sender, SessionState state) => _dispatcher.Post(ApplySession);

	private void ApplySession()
	{
		var session = _supervisor.Current;
		IsConnected = session is not null;
		SessionText = session is null
			? "Not connected."
			: $"Connected · {session.Info.Model} · firmware {session.Info.Firmware} · {session.Info.Trust}";
		UpdateSendState();
	}

	/// <summary>Rebuilds the visible rows for the active filters; the selection is cleared deterministically.</summary>
	private void RebuildRows()
	{
		SelectedRow = null;
		Rows = BuildRows();
		RowCountText = DescribeRowCount();
	}

	private ObservableCollection<CatalogRowVm> BuildRows() =>
		new(_all.Where(MatchesFilter));

	private string DescribeRowCount() => FormattableString.Invariant($"{Rows.Count} of {_all.Count} commands");

	private bool MatchesFilter(CatalogRowVm row)
	{
		if (_safetyFilter != ConsoleSafetyFilter.All && row.Safety != MapSafety(_safetyFilter))
		{
			return false;
		}

		return _filterText.Length == 0
			|| row.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
			|| row.WordsText.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
			|| row.VendorText.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
			|| row.SafetyText.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
			|| row.GroupText.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
			|| row.NoteText.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
			|| row.Description.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
	}

	private static SafetyClass MapSafety(ConsoleSafetyFilter filter) => filter switch
	{
		ConsoleSafetyFilter.Read => SafetyClass.Read,
		ConsoleSafetyFilter.Write => SafetyClass.Write,
		ConsoleSafetyFilter.Gated => SafetyClass.Gated,
		ConsoleSafetyFilter.Dangerous => SafetyClass.Dangerous,
		ConsoleSafetyFilter.Blocked => SafetyClass.Blocked,
		ConsoleSafetyFilter.Unknown => SafetyClass.Unknown,
		_ => SafetyClass.Read,
	};

	private void ApplySelection(CatalogRowVm? row)
	{
		HasSelection = row is not null;
		Options = row?.Options ?? [];
		SelectedOptionIndex = 0;
		OnPropertyChanged(nameof(SelectedOptionIndex)); // re-push after the ItemsSource swap (a same-value OneWay bind is not re-raised)
		UnlockText = string.Empty;
		if (row is null)
		{
			DetailName = string.Empty;
			DetailKind = string.Empty;
			DetailVendor = string.Empty;
			DetailWords = string.Empty;
			DetailSafety = string.Empty;
			DetailGroup = string.Empty;
			DetailNote = string.Empty;
			DetailDescription = string.Empty;
			ShowDangerNote = false;
			ShowUnlock = false;
			ShowUnlockBadge = false;
			UnlockPrompt = string.Empty;
		}
		else
		{
			DetailName = row.Name;
			DetailKind = row.KindText;
			DetailVendor = DescribeVendor(row.Vendor);
			DetailWords = row.WordsText;
			DetailSafety = row.SafetyText;
			DetailGroup = row.GroupText;
			DetailNote = row.NoteText;
			DetailDescription = row.Description;
			ShowDangerNote = row.Safety == SafetyClass.Dangerous;
			UpdateUnlockState();
		}

		UpdateUnlockMatch();
		UpdateSendState();
	}

	private void UpdateUnlockState()
	{
		var key = SelectedRow?.BlockedKey;
		ShowUnlock = key is not null && !_overrides.ContainsKey(key.Value);
		ShowUnlockBadge = key is not null && _overrides.ContainsKey(key.Value);
		UnlockPrompt = key is { } k
			? $"Type {k.Word:X4} exactly to unlock {VendorShort(k.Vendor)}:{k.Word:X4} for this session."
			: string.Empty;
	}

	private void UpdateUnlockMatch() => CanUnlock = SelectedRow?.BlockedKey is { } key && MatchesUnlockPhrase(key.Word);

	private bool MatchesUnlockPhrase(ushort word)
	{
		var typed = (UnlockText ?? string.Empty).Trim();
		if (typed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			typed = typed[2..];
		}

		return typed.Length > 0 && string.Equals(typed, word.ToString("X4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
	}

	private void UpdateSendState()
	{
		if (!HasSelection)
		{
			CanSend = false;
			SendNoteText = "Select a command from the catalog.";
			return;
		}

		if (!IsConnected)
		{
			CanSend = false;
			SendNoteText = "Not connected.";
			return;
		}

		if (!_payloadValid)
		{
			CanSend = false;
			SendNoteText = PayloadErrorText ?? "Invalid hex payload.";
			return;
		}

		if (IsSending)
		{
			CanSend = false;
			SendNoteText = "Sending…";
			return;
		}

		if (CurrentOption is null)
		{
			CanSend = false;
			SendNoteText = "Choose a command word.";
			return;
		}

		CanSend = true;
		SendNoteText = string.Empty;
	}

	private void AddLine(string text)
	{
		var row = new ConsoleLogRowVm(DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), text);
		LogLines.Add(row);
		while (LogLines.Count > MaxLogLines)
		{
			LogLines.RemoveAt(0);
		}

		UpdateLogCount();
		LogAppended?.Invoke(this, 1);
	}

	private void UpdateLogCount() => LogCountText = FormattableString.Invariant($"{LogLines.Count} line(s)");

	// ---- catalog construction ----

	private void BuildCatalog()
	{
		foreach (var binding in GaiaCatalog.Bindings)
		{
			_all.Add(BuildBindingRow(binding));
		}

		foreach (var raw in GaiaCatalog.Raw)
		{
			_all.Add(BuildRawRow(raw));
		}
	}

	private static CatalogRowVm BuildBindingRow(ISettingBinding binding)
	{
		var options = new List<ConsoleSendOption>();
		if (binding.GetCmd is { } get)
		{
			options.Add(new ConsoleSendOption($"GET {get:X4}", get, IsWrite: false, Safety: SafetyClass.Read));
		}

		if (binding.SetCmd is { } set)
		{
			options.Add(new ConsoleSendOption($"SET {set:X4}", set, IsWrite: true, Safety: binding.Safety));
		}

		var words = new List<string>();
		if (binding.GetCmd is { } g)
		{
			words.Add($"GET {g:X4}");
		}

		if (binding.SetCmd is { } s)
		{
			words.Add($"SET {s:X4}");
		}

		if (binding.PushCmd is { } p)
		{
			words.Add($"PUSH {p:X4}");
		}

		var sentences = new List<string>
		{
			$"Typed setting binding for '{binding.Name}' (group {binding.Group}).",
			binding.GetCmd is { } gg ? $"GET {gg:X4} reads the value (safety: Read)." : "No GET word — write-only setting.",
			binding.SetCmd is { } ss ? $"SET {ss:X4} writes it (safety: {binding.Safety}; minimum trust {binding.MinTrust})." : "No SET word — read-only.",
		};
		if (binding.PushCmd is { } pp)
		{
			sentences.Add($"Pushes {pp:X4} when the value changes.");
		}

		if (binding.Indexed)
		{
			sentences.Add("Indexed: the GET payload is selector + index byte; the SET payload starts with the index.");
		}

		var blockedKey = binding.Safety == SafetyClass.Blocked
			? ((GaiaVendor, ushort)?)(binding.Vendor, binding.SetCmd ?? binding.GetCmd ?? 0)
			: null;

		return new CatalogRowVm(
			binding.Name,
			"Typed binding",
			binding.Vendor,
			VendorShort(binding.Vendor),
			string.Join(" · ", words),
			binding.Safety,
			binding.Safety.ToString(),
			binding.Group.ToString(),
			string.Empty,
			string.Join(' ', sentences),
			options,
			blockedKey);
	}

	private static CatalogRowVm BuildRawRow(RawCommand raw)
	{
		IReadOnlyList<ConsoleSendOption> options = raw.Safety switch
		{
			SafetyClass.Unknown =>
			[
				new ConsoleSendOption($"GET {raw.Command:X4} (probe)", raw.Command, IsWrite: false, Safety: SafetyClass.Unknown),
				new ConsoleSendOption($"SET {raw.Command:X4}", raw.Command, IsWrite: true, Safety: SafetyClass.Unknown),
			],
			SafetyClass.Read => [new ConsoleSendOption($"GET {raw.Command:X4}", raw.Command, IsWrite: false, Safety: SafetyClass.Read)],
			SafetyClass.Gated => [new ConsoleSendOption($"SET {raw.Command:X4}", raw.Command, IsWrite: true, Safety: SafetyClass.Gated)],
			_ => [new ConsoleSendOption($"SEND {raw.Command:X4}", raw.Command, IsWrite: true, Safety: raw.Safety)],
		};

		var sentence = raw.Safety switch
		{
			SafetyClass.Read => "Sending the word as listed is a read (always allowed).",
			SafetyClass.Write => "Sending the word as listed is a write.",
			SafetyClass.Gated => "Sending the word as listed is a gated write — needs DevMode or verified firmware.",
			SafetyClass.Dangerous => "Refused in the console — Dangerous commands run only from their dedicated pages with a consent token.",
			SafetyClass.Blocked => "Blocked — type the exact command word to unlock it for this session; sends then attach the override token.",
			_ => "GET probing only — the send carries the DevTools probe flag; write attempts are refused.",
		};
		var description = $"Raw command '{raw.Name}' — no typed binding ({VendorShort(raw.Vendor)}:{raw.Command:X4}, class {raw.Safety}). {sentence}";

		return new CatalogRowVm(
			raw.Name,
			"Raw command",
			raw.Vendor,
			VendorShort(raw.Vendor),
			FormattableString.Invariant($"{raw.Command:X4}"),
			raw.Safety,
			raw.Safety.ToString(),
			"—",
			raw.Note ?? string.Empty,
			description,
			options,
			raw.Safety == SafetyClass.Blocked ? ((GaiaVendor, ushort)?)(raw.Vendor, raw.Command) : null);
	}

	private static string VendorShort(GaiaVendor vendor) => vendor switch
	{
		GaiaVendor.Sennheiser => "S",
		GaiaVendor.Qualcomm => "Q",
		_ => ((ushort)vendor).ToString("X4", CultureInfo.InvariantCulture),
	};

	private static string DescribeVendor(GaiaVendor vendor) => vendor switch
	{
		GaiaVendor.Sennheiser => "S — Sennheiser (0x0495)",
		GaiaVendor.Qualcomm => "Q — Qualcomm (0x001D)",
		_ => ((ushort)vendor).ToString("X4", CultureInfo.InvariantCulture),
	};
}
