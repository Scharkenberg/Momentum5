using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.Shell;
using Momentum5.Client;
using Momentum5.Protocol;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// One consent request (X-02): the plain-language consequence the user will see, the phrase they must type
/// (the device or command name), and the GAIA command the issued token will be bound to.
/// </summary>
public sealed record ConsentRequest(
	string Title,
	string Consequence,
	string ConfirmPhrase,
	GaiaVendor Vendor,
	ushort Command,
	string? Context = null)
{
	/// <summary>Short line naming the exact command (shown under the consequence), e.g. "Sennheiser:0607".</summary>
	public string CommandText => $"{Vendor}:{Command:X4}";
}

/// <summary>
/// The typed-consent dialog (X-02). A full-window <see cref="ContentDialog"/> shows the consequence in plain
/// language; the primary button stays locked for <see cref="Cooldown"/> seconds and until the user types the
/// confirm phrase exactly. Confirming issues a single-use <see cref="ConsentToken"/> through
/// <see cref="ConsentService"/> — the audit hash binds the token to the exact consequence text shown here, so
/// what the user confirmed and what the gate later consumes cannot drift apart. Cancelling returns null.
/// </summary>
public sealed class ConsentDialogService(ConsentService consent, AppLog log)
{
	/// <summary>Cooldown before the confirm button unlocks (gives the consequence text time to be read).</summary>
	public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

	/// <summary>Shows the dialog on the UI thread and returns the issued token, or null when cancelled/unavailable.</summary>
	public async Task<ConsentToken?> ConfirmAsync(ConsentRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		var root = App.MainShell?.Content?.XamlRoot;
		if (root is null)
		{
			log.Write("consent dialog unavailable: no shell window");
			return null;
		}

		var input = new TextBox
		{
			PlaceholderText = request.ConfirmPhrase,
			MaxLength = Math.Max(16, request.ConfirmPhrase.Length * 2),
		};
		AutomationProperties.SetName(input, $"Type {request.ConfirmPhrase} exactly to confirm");

		var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
		var countdown = new TextBlock { Opacity = 0.7 };

		var dialog = new ContentDialog
		{
			Title = request.Title,
			FullSizeDesired = true,
			PrimaryButtonText = "I understand — confirm",
			SecondaryButtonText = "Cancel",
			DefaultButton = ContentDialogButton.None,
			IsPrimaryButtonEnabled = false,
			Content = new StackPanel
			{
				Spacing = 12,
				Children =
				{
					new TextBlock
					{
						Text = request.Consequence,
						TextWrapping = TextWrapping.Wrap,
						IsTextSelectionEnabled = true,
					},
					new TextBlock
					{
						Text = string.IsNullOrEmpty(request.Context) ? request.CommandText : $"{request.Context} · {request.CommandText}",
						Opacity = 0.7,
					},
					input,
					hint,
					countdown,
				},
			},
			XamlRoot = root,
		};

		var remaining = (int)Cooldown.TotalSeconds;
		var elapsed = false;
		UpdateGate();

		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		timer.Tick += (_, _) =>
		{
			remaining--;
			if (remaining <= 0)
			{
				elapsed = true;
				timer.Stop();
			}

			UpdateGate();
		};

		input.TextChanged += (_, _) => UpdateGate();
		timer.Start();
		try
		{
			var result = await dialog.ShowAsync();
			if (result != ContentDialogResult.Primary)
			{
				return null;
			}

			try
			{
				return consent.Issue(request.Vendor, request.Command, request.Consequence);
			}
			catch (Exception ex)
			{
				log.Write($"consent issue failed: {ex.Message}");
				return null;
			}
		}
		finally
		{
			timer.Stop();
		}

		void UpdateGate()
		{
			var typed = string.Equals(input.Text.Trim(), request.ConfirmPhrase, StringComparison.Ordinal);
			hint.Text = input.Text.Length == 0
				? $"Type \u201C{request.ConfirmPhrase}\u201D exactly to unlock the confirm button."
				: typed ? "Phrase matches." : "The phrase does not match yet — check spelling and case.";
			countdown.Text = elapsed ? string.Empty : $"Confirm unlocks in {remaining} s — read the text above.";
			dialog.IsPrimaryButtonEnabled = elapsed && typed;
		}
	}
}

/// <summary>
/// Recovery info bar helper (X-02): one consistent way for Advanced pages to surface "if this went wrong,
/// here is how to recover" guidance. Pages own an <see cref="InfoBar"/> element; this helper configures it.
/// </summary>
public static class RecoveryInfo
{
	/// <summary>Opens the bar as a warning with the recovery steps (plain text, wrapped).</summary>
	public static void Show(InfoBar bar, string title, string message)
	{
		ArgumentNullException.ThrowIfNull(bar);
		bar.Title = title;
		bar.Message = message;
		bar.Severity = InfoBarSeverity.Warning;
		bar.IsOpen = true;
	}

	/// <summary>Closes the bar (call when the state that triggered it is healthy again).</summary>
	public static void Dismiss(InfoBar bar)
	{
		ArgumentNullException.ThrowIfNull(bar);
		bar.IsOpen = false;
	}
}
