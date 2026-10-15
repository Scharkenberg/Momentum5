using System;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Client;

public enum GateVerdict
{
	Allow,
	RequireConsent,
	Deny,
}

public sealed record GateDecision(GateVerdict Verdict, string? Reason = null)
{
	public static GateDecision Allowed { get; } = new(GateVerdict.Allow);

	public bool IsAllowed => Verdict == GateVerdict.Allow;

	public static GateDecision Deny(string reason) => new(GateVerdict.Deny, reason);

	public static GateDecision Consent(string reason) => new(GateVerdict.RequireConsent, reason);
}

/// <summary>Single-use permission for one Dangerous command (issued by Track X). Expires when <see cref="ExpiresAt"/> passes.</summary>
public sealed record ConsentToken(GaiaVendor Vendor, ushort Command, Guid Id)
{
	public static ConsentToken For(GaiaVendor vendor, ushort command) => new(vendor, GaiaWord.AsCommand(command), Guid.NewGuid());

	/// <summary>X-01: tokens are time-limited; null only for the legacy parameterless factory.</summary>
	public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Per-command, per-session override for a Blocked command (DevTools only).</summary>
public sealed record OverrideToken(GaiaVendor Vendor, ushort Command);

public interface ISafetyGate
{
	/// <summary>
	/// Decides whether <paramref name="command"/> (a command-typed word) may be sent. <paramref name="isWrite"/> is false
	/// only for DevTools GET-style probing of Unknown commands. A consent token is consumed when it yields Allow.
	/// </summary>
	GateDecision Check(
		GaiaVendor vendor,
		ushort command,
		bool isWrite,
		TrustTier trust,
		bool devMode,
		ConsentToken? token = null,
		OverrideToken? ov = null);
}

/// <summary>Rules of ref/04.</summary>
public sealed class SafetyGate : ISafetyGate
{
	private readonly Lock _gate = new();
	private readonly HashSet<Guid> _consumed = [];
	private readonly TimeProvider _time;

	/// <param name="time">Clock for consent-token expiry (X-01); defaults to the system clock.</param>
	public SafetyGate(TimeProvider? time = null)
	{
		_time = time ?? TimeProvider.System;
	}

	public GateDecision Check(
		GaiaVendor vendor,
		ushort command,
		bool isWrite,
		TrustTier trust,
		bool devMode,
		ConsentToken? token = null,
		OverrideToken? ov = null)
	{
		if (!GaiaWord.IsSendable(command))
		{
			return GateDecision.Deny("Not a command-typed word.");
		}

		var cmd = GaiaWord.AsCommand(command);
		var safety = GaiaCatalog.SafetyOf(vendor, command);
		switch (safety)
		{
			case SafetyClass.Read:
				return GateDecision.Allowed;

			case SafetyClass.Write:
			case SafetyClass.Gated:
				return CheckWrite(vendor, cmd, safety, trust, devMode);

			case SafetyClass.Dangerous:
				return CheckDangerous(vendor, cmd, devMode, token);

			case SafetyClass.Blocked:
				return devMode && ov is not null && ov.Vendor == vendor && GaiaWord.AsCommand(ov.Command) == cmd
					? GateDecision.Allowed
					: GateDecision.Deny("Blocked command (DevTools override required).");

			default:
				return !isWrite && devMode
					? GateDecision.Allowed
					: GateDecision.Deny("Unknown command: probing is only allowed from DevTools, never writes.");
		}
	}

	private static GateDecision CheckWrite(GaiaVendor vendor, ushort cmd, SafetyClass safety, TrustTier trust, bool devMode)
	{
		var setters = GaiaCatalog.Lookup(vendor, cmd)?.Bindings
			.Where(b => b.SetCmd is { } s && GaiaWord.AsCommand(s) == cmd)
			.ToList();
		var allowed = setters is { Count: > 0 }
			? setters.Exists(b => M5Profile.Allows(b, trust, devMode))
			: devMode || (safety == SafetyClass.Gated ? trust == TrustTier.Verified : trust >= TrustTier.Compatible);
		if (allowed)
		{
			return GateDecision.Allowed;
		}

		return GateDecision.Deny(trust == TrustTier.Unknown
			? "Read-only: firmware is not verified (enable DevMode to override)."
			: "Needs DevMode: gated feature on a not fully verified firmware.");
	}

	private GateDecision CheckDangerous(GaiaVendor vendor, ushort cmd, bool devMode, ConsentToken? token)
	{
		if (!devMode)
		{
			return GateDecision.Deny("Dangerous command: DevMode required.");
		}

		if (token is null || token.Vendor != vendor || GaiaWord.AsCommand(token.Command) != cmd)
		{
			return GateDecision.Consent("Dangerous command: a matching consent token is required.");
		}

		if (token.ExpiresAt is { } expiry && _time.GetUtcNow() > expiry)
		{
			return GateDecision.Deny("Consent token expired — confirm the action again.");
		}

		lock (_gate)
		{
			return _consumed.Add(token.Id)
				? GateDecision.Allowed
				: GateDecision.Deny("Consent token was already used.");
		}
	}
}
