using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Pages.Advanced;
using Momentum5.App.Shell;
using Momentum5.Client;
using Momentum5.Core.Transport;
using Momentum5.Protocol;

namespace Momentum5.App.ViewModels;

/// <summary>
/// X-08 factory reset state: the arming checkbox unlocks the button, the typed-phrase consent dialog (X-02)
/// issues the single-use <see cref="ConsentToken"/> bound to S:0040, the frame goes through the live session
/// pipeline, and afterwards the app-side device-bound state is cleared through
/// <see cref="StartupController.UpdateAsync"/> (last device forgotten; EQ presets are kept) and the re-pair
/// guide shows (<see cref="ResetDone"/> stays set for the cached page instance). Plain
/// <see cref="ObservableObject"/> — the session comes from <see cref="ConnectionSupervisor"/> (the
/// SettingsViewModel/ConsoleViewModel precedent; no headset snapshot reads are needed).
/// </summary>
public sealed partial class FactoryResetViewModel : ObservableObject
{
	/// <summary>The raw Sennheiser factory-reset word (S:0040, Dangerous class — needs a consent token).</summary>
	public const ushort ResetCommand = 0x0040;

	/// <summary>The phrase the consent dialog requires (the bucket's "typed RESET").</summary>
	public const string ConfirmPhrase = "RESET";

	private readonly ConnectionSupervisor _supervisor;
	private readonly ConsentDialogService _dialog;
	private readonly StartupController _startup;
	private readonly AppLog _log;
	private readonly IUiDispatcher _dispatcher;

	public FactoryResetViewModel(ConnectionSupervisor supervisor, ConsentDialogService dialog, StartupController startup, AppLog log, IUiDispatcher dispatcher)
	{
		_supervisor = supervisor;
		_dialog = dialog;
		_startup = startup;
		_log = log;
		_dispatcher = dispatcher;

		ApplySession();
		_supervisor.StateChanged += OnSupervisorStateChanged;
	}

	/// <summary>True while a session is live (the reset needs a connected device).</summary>
	[ObservableProperty]
	public partial bool IsConnected { get; private set; }

	/// <summary>Session line ("Not connected." or "Connected · M5AEBT Black · firmware 6.21.5 · Verified").</summary>
	[ObservableProperty]
	public partial string SessionText { get; private set; } = "Not connected.";

	/// <summary>
	/// The arming checkbox. Nullable because <c>CheckBox.IsChecked</c> is <c>bool?</c> (exact-type TwoWay
	/// x:Bind); <c>IsThreeState</c> stays off, so the user only ever produces true/false — null counts as false.
	/// </summary>
	[ObservableProperty]
	public partial bool? Acknowledged { get; set; }

	/// <summary>True while the consent dialog and the reset send are in flight.</summary>
	[ObservableProperty]
	public partial bool IsResetting { get; private set; }

	/// <summary>Gate refusal, headset rejection, timeout or link drop; null while healthy.</summary>
	[ObservableProperty]
	public partial string? Warning { get; private set; }

	/// <summary>Affirmative status line (dialog cancelled, command acknowledged); null while silent.</summary>
	[ObservableProperty]
	public partial string? StatusNote { get; private set; }

	/// <summary>True once the reset flow ran — keeps the re-pair guide visible on the cached page.</summary>
	[ObservableProperty]
	public partial bool ResetDone { get; private set; }

	/// <summary>
	/// Runs the full flow: typed-RESET consent dialog → S:0040 with the token → app-side cleanup (last device
	/// cleared, presets kept) → re-pair guide. Per the bucket, every failed send path still surfaces a warning
	/// and CONTINUES into the local cleanup: the user confirmed a destructive reset, and a link that dies right
	/// after the send is the expected power-cycle behavior on a real device.
	/// </summary>
	public async Task ResetAsync()
	{
		if (IsResetting || ResetDone)
		{
			return;
		}

		Warning = null;
		StatusNote = null;
		var session = _supervisor.Current;
		if (session is null)
		{
			Warning = "Not connected — connect the headphones before resetting them.";
			return;
		}

		IsResetting = true;
		try
		{
			await RunResetAsync().ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			// Defensive: the dialog (e.g. another ContentDialog is already open) or the pipeline threw.
			Warning = $"The reset flow failed: {ex.Message}";
			_log.Write($"factory reset: failed: {ex.Message}");
		}
		finally
		{
			IsResetting = false;
		}
	}

	private async Task RunResetAsync()
	{
		var request = new ConsentRequest(
			Title: "Factory reset",
			Consequence: "Every pairing is forgotten, all settings on the headphones revert to their defaults, and this app forgets the last device. The headphones restart in pairing mode. This cannot be undone.",
			ConfirmPhrase: ConfirmPhrase,
			Vendor: GaiaVendor.Sennheiser,
			Command: ResetCommand,
			Context: "Factory reset");

		ConsentToken? token;
		try
		{
			token = await _dialog.ConfirmAsync(request).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			Warning = $"The confirmation dialog could not be shown: {ex.Message}";
			_log.Write($"factory reset: dialog failed: {ex.Message}");
			return;
		}

		if (token is null)
		{
			StatusNote = "Cancelled — nothing was sent to the headphones.";
			_log.Write("factory reset: confirmation cancelled");
			return;
		}

		// Re-read the session: the link may have dropped while the dialog was open.
		var session = _supervisor.Current;
		if (session is null)
		{
			Warning = "The connection was lost before the reset could be sent — nothing was sent to the headphones.";
			_log.Write("factory reset: session gone before send");
			return;
		}

		try
		{
			await session.Pipeline.ExecuteAsync(
				GaiaVendor.Sennheiser,
				ResetCommand,
				ReadOnlyMemory<byte>.Empty,
				new CommandOptions { Consent = token }).ConfigureAwait(true);
			StatusNote = "Reset command acknowledged — the headphones power off and back on.";
			_log.Write("factory reset: S:0040 executed (acknowledged)");
		}
		catch (SafetyDeniedException ex)
		{
			Warning = $"Refused by the safety gate: {ex.Message}";
			_log.Write($"factory reset: denied: {ex.Message}");
		}
		catch (GaiaProtocolException ex)
		{
			Warning = $"The headphones rejected the reset command: {ex.Message}";
			_log.Write($"factory reset: rejected: {ex.Message}");
		}
		catch (TimeoutException ex)
		{
			Warning = "No reply to the reset command — the outcome is unknown; the headphones may have reset and power-cycled already. Continuing with the local cleanup.";
			_log.Write($"factory reset: timeout: {ex.Message}");
		}
		catch (NotConnectedException ex)
		{
			Warning = "The link dropped right after the reset command — this is expected: the headphones power-cycle during a reset. Continuing with the local cleanup.";
			_log.Write($"factory reset: session gone after send: {ex.Message}");
		}
		catch (LinkException ex)
		{
			Warning = "The link dropped right after the reset command — this is expected: the headphones power-cycle during a reset. Continuing with the local cleanup.";
			_log.Write($"factory reset: link lost after send: {ex.Message}");
		}

		// Post-reset local cleanup (keep presets): clear the device-bound state through the single settings
		// writer. PresetStore is deliberately untouched — EQ presets are app-side and survive the reset.
		try
		{
			await _startup.UpdateAsync(settings => settings with { LastDeviceAddress = null }).ConfigureAwait(true);
			_log.Write("factory reset: app-side last device cleared (presets kept)");
		}
		catch (Exception ex)
		{
			Warning = $"The headphones were reset, but the app could not forget the last device: {ex.Message}";
			_log.Write($"factory reset: local cleanup failed: {ex.Message}");
		}

		ResetDone = true;
	}

	private void OnSupervisorStateChanged(object? sender, SessionState state) => _dispatcher.Post(ApplySession);

	private void ApplySession()
	{
		var session = _supervisor.Current;
		IsConnected = session is not null;
		SessionText = session is null
			? "Not connected."
			: $"Connected · {session.Info.Model} · firmware {session.Info.Firmware} · {session.Info.Trust}";
	}
}
