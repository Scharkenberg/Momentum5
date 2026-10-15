using System;
using System.Threading;
using System.Threading.Tasks;
using Momentum5.Core.Settings;

namespace Momentum5.App.Services;

public enum QuickAction
{
	Anc,
	Transparency,
	Immersive,
}

/// <summary>The few toggles shared by tray menu, hotkeys and zones; all go through <see cref="ISettingService"/> like the pages.</summary>
public static class QuickActions
{
	public static string Id(QuickAction action) => action switch
	{
		QuickAction.Anc => "toggleAnc",
		QuickAction.Transparency => "toggleTransparency",
		_ => "toggleImmersive",
	};

	public static bool IsOn(AppServices services, QuickAction action)
	{
		var snap = services.Store.Current;
		return action switch
		{
			QuickAction.Anc => NoiseControl.TryGetMode(snap, out var m) && m != NoiseMode.Off,
			QuickAction.Transparency => NoiseControl.IsTransparencyOn(snap),
			_ => snap.TryGet(Keys.ImmersiveAudio, out var v) && v,
		};
	}

	public static bool IsAvailable(AppServices services, QuickAction action) =>
		action == QuickAction.Immersive
			? services.Store.Current.TryGet(Keys.ImmersiveAudio, out _)
			: NoiseControl.IsAvailable(services.Store.Current);

	public static async Task ToggleAsync(AppServices services, QuickAction action)
	{
		ArgumentNullException.ThrowIfNull(services);
		var snapshot = services.Store.Current;
		if (snapshot.Session is not { Trust: not TrustTier.Unknown } || !IsAvailable(services, action))
		{
			services.ReportError("Connect the headset first.");
			return;
		}

		await SetAsync(services, action, !IsOn(services, action)).ConfigureAwait(true);
	}

	public static async Task SetAsync(AppServices services, QuickAction action, bool value)
	{
		try
		{
			switch (action)
			{
				case QuickAction.Anc:
					await NoiseControl.SetAncAsync(services, value).ConfigureAwait(true);
					break;
				case QuickAction.Transparency:
					await NoiseControl.SetTransparencyAsync(services, value).ConfigureAwait(true);
					break;
				default:
					await services.Settings.SetAsync(Keys.ImmersiveAudio, value, CancellationToken.None).ConfigureAwait(true);
					break;
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			services.ReportError($"{action}: {ex.Message}");
		}
	}
}
