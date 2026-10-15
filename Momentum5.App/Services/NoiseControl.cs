using System;
using System.Threading;
using System.Threading.Tasks;
using Momentum5.Core.Settings;

namespace Momentum5.App.Services;

/// <summary>Official-app model: Off / Adaptive / Custom (slider). Transparency is the slider at its far end, not a separate state.</summary>
public enum NoiseMode
{
	Off = 0,
	Adaptive = 1,
	Custom = 2,
}

public static class NoiseControl
{
	private static NoiseMode s_lastActive = NoiseMode.Adaptive;
	private static NoiseMode s_beforeTransparency = NoiseMode.Off;
	private static int s_levelBefore;

	public static bool IsAvailable(HeadsetSnapshot s) =>
		s.Session is { Trust: not TrustTier.Unknown } && s.TryGet(Keys.AncEnabled, out _);

	public static bool TryGetMode(HeadsetSnapshot s, out NoiseMode mode)
	{
		mode = NoiseMode.Off;
		if (!s.TryGet(Keys.AncEnabled, out var on))
		{
			return false;
		}

		s.TryGet(Keys.AncAdaptive, out var adaptive);
		mode = !on ? NoiseMode.Off : adaptive ? NoiseMode.Adaptive : NoiseMode.Custom;
		return true;
	}

	public static bool IsTransparencyOn(HeadsetSnapshot s) =>
		TryGetMode(s, out var m) && m == NoiseMode.Custom && s.TryGet(Keys.AncLevel, out var level) && level > 50;

	public static async Task SetModeAsync(AppServices services, NoiseMode mode)
	{
		ArgumentNullException.ThrowIfNull(services);
		var s = services.Settings;
		var ct = CancellationToken.None;
		switch (mode)
		{
			case NoiseMode.Off:
				await s.SetAsync(Keys.AncAdaptive, false, ct).ConfigureAwait(true);
				await s.SetAsync(Keys.AncEnabled, false, ct).ConfigureAwait(true);
				break;
			case NoiseMode.Adaptive:
				await s.SetAsync(Keys.AncAdaptive, true, ct).ConfigureAwait(true);
				await s.SetAsync(Keys.AncEnabled, true, ct).ConfigureAwait(true);
				break;
			default:
				await s.SetAsync(Keys.AncAdaptive, false, ct).ConfigureAwait(true);
				await s.SetAsync(Keys.AncEnabled, true, ct).ConfigureAwait(true);
				break;
		}

		if (mode != NoiseMode.Off)
		{
			s_lastActive = mode;
		}

		await s.RefreshAsync(SettingGroup.Anc, ct).ConfigureAwait(true);
	}

	public static Task ToggleAncAsync(AppServices services) =>
		SetAncAsync(services, !(TryGetMode(services.Store.Current, out var m) && m != NoiseMode.Off));

	public static Task SetAncAsync(AppServices services, bool on)
	{
		ArgumentNullException.ThrowIfNull(services);
		if (!on)
		{
			return SetModeAsync(services, NoiseMode.Off);
		}

		return TryGetMode(services.Store.Current, out var m) && m != NoiseMode.Off
			? Task.CompletedTask
			: SetModeAsync(services, s_lastActive);
	}

	/// <summary>On: Custom with the slider at full transparency (previous state remembered). Off: back to what it was.</summary>
	public static async Task SetTransparencyAsync(AppServices services, bool on)
	{
		ArgumentNullException.ThrowIfNull(services);
		var snap = services.Store.Current;
		var ct = CancellationToken.None;
		if (on)
		{
			if (IsTransparencyOn(snap))
			{
				return;
			}

			s_beforeTransparency = TryGetMode(snap, out var m) ? m : NoiseMode.Off;
			s_levelBefore = snap.TryGet(Keys.AncLevel, out var l) && l <= 50 ? l : 0;
			await SetModeAsync(services, NoiseMode.Custom).ConfigureAwait(true);
			await services.Settings.SetAsync(Keys.AncLevel, 100, ct).ConfigureAwait(true);
		}
		else
		{
			if (!IsTransparencyOn(snap))
			{
				return;
			}

			await services.Settings.SetAsync(Keys.AncLevel, s_levelBefore, ct).ConfigureAwait(true);
			if (s_beforeTransparency != NoiseMode.Custom)
			{
				await SetModeAsync(services, s_beforeTransparency).ConfigureAwait(true);
			}
		}

		await services.Settings.RefreshAsync(SettingGroup.Anc, ct).ConfigureAwait(true);
	}

	/// <summary>0 = off, 1 = auto, 2 = max. Auto is written as 2 but reads back as 1, so the app remembers it.</summary>
	public static int GetWindIndex(AppServices services, HeadsetSnapshot s) =>
		!s.TryGet(Keys.AncAntiWind, out var v) ? -1 : v == 0 ? 0 : services.Current.AntiWindAuto ? 1 : 2;

	public static async Task SetWindAsync(AppServices services, int index)
	{
		ArgumentNullException.ThrowIfNull(services);
		if (index is < 0 or > 2)
		{
			return;
		}

		services.UpdateSettings(s => s with { AntiWindAuto = index == 1 });
		byte wire = index switch { 0 => 0, 1 => 2, _ => 1 };
		await services.Settings.SetAsync(Keys.AncAntiWind, wire, CancellationToken.None).ConfigureAwait(true);
		await services.Settings.RefreshAsync(SettingGroup.Anc, CancellationToken.None).ConfigureAwait(true);
	}
}
