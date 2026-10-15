using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Protocol;

public sealed record ProfileDecision(bool Supported, TrustTier Trust, string Reason);

/// <summary>The one supported product: model gate and firmware trust tiers (ref/04).</summary>
public static class M5Profile
{
	public const string ModelPrefix = "M5AEBT";

	/// <summary>Firmware versions confirmed on hardware. Add a line after the V-01 matrix passes on a new one.</summary>
	public static IReadOnlyList<FirmwareVersion> Verified { get; } = [new FirmwareVersion(6, 21, 5)];

	public static ProfileDecision Evaluate(string model, FirmwareVersion firmware)
	{
		if (!model.StartsWith(ModelPrefix, StringComparison.Ordinal))
		{
			return new ProfileDecision(false, TrustTier.Unknown, $"Unsupported device '{model}'.");
		}

		if (Verified.Contains(firmware))
		{
			return new ProfileDecision(true, TrustTier.Verified, "Verified firmware.");
		}

		foreach (var v in Verified)
		{
			if (firmware.Major == v.Major && firmware.Minor == v.Minor && firmware.Patch > v.Patch)
			{
				return new ProfileDecision(true, TrustTier.Compatible, "Firmware newer than verified; writes allowed, gated features need DevMode.");
			}
		}

		return new ProfileDecision(true, TrustTier.Unknown, "Unverified firmware; read-only.");
	}

	/// <summary>Whether a SET through <paramref name="binding"/> may be sent at this trust tier.</summary>
	public static bool Allows(ISettingBinding binding, TrustTier trust, bool devMode = false)
	{
		ArgumentNullException.ThrowIfNull(binding);
		return binding.Safety switch
		{
			SafetyClass.Read => true,
			SafetyClass.Write => devMode || trust >= binding.MinTrust,
			SafetyClass.Gated => devMode || trust == TrustTier.Verified,
			_ => false,
		};
	}
}
