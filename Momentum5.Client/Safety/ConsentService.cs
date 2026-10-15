using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Momentum5.Core.Safety;
using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>
/// Issues single-use, command-bound, time-limited <see cref="ConsentToken"/>s for Dangerous commands (X-01,
/// ref/04) and per-command, per-session <see cref="OverrideToken"/>s for DevTools Blocked commands. Tokens are
/// consumed by <see cref="SafetyGate"/> when the pipeline sends the frame; this service only issues and audits.
/// The consequence hash makes the audit trail tamper-evident: the UI passes the exact consequence text the user
/// confirmed, and the hash recorded here must match the one shown at execution time.
/// </summary>
public sealed class ConsentService : IConsentAudit
{
	/// <summary>Default token lifetime: 5 minutes after issue.</summary>
	public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly List<ConsentAuditEntry> _entries = [];

	public ConsentService(TimeProvider? time = null)
	{
		_time = time ?? TimeProvider.System;
	}

	/// <summary>Raised (issuing thread) when a token is issued; feeds the DevTools audit view.</summary>
	public event EventHandler<ConsentAuditEntry>? Issued;

	public IReadOnlyList<ConsentAuditEntry> Entries
	{
		get
		{
			lock (_gate)
			{
				return [.. _entries];
			}
		}
	}

	/// <summary>Issues a consent token bound to one command word; expires after <paramref name="lifetime"/> (default 5 min).</summary>
	public ConsentToken Issue(GaiaVendor vendor, ushort command, string consequenceText, TimeSpan? lifetime = null)
	{
		if (string.IsNullOrWhiteSpace(consequenceText))
		{
			throw new ArgumentException("The consequence text shown to the user is required.", nameof(consequenceText));
		}

		var expires = _time.GetUtcNow() + (lifetime ?? DefaultLifetime);
		var token = new ConsentToken(vendor, GaiaWord.AsCommand(command), Guid.NewGuid())
		{
			ExpiresAt = expires,
		};

		var entry = new ConsentAuditEntry(token.Id, (int)vendor, token.Command, Hash(consequenceText), _time.GetUtcNow(), expires);
		lock (_gate)
		{
			_entries.Add(entry);
		}

		Issued?.Invoke(this, entry);
		return token;
	}

	/// <summary>Per-command, per-session DevTools override for a Blocked command (no lifetime, no consent flow).</summary>
	public static OverrideToken CreateOverride(GaiaVendor vendor, ushort command) => new(vendor, GaiaWord.AsCommand(command));

	/// <summary>Stable hash of the consequence text (SHA-256, first 16 hex chars) for the audit trail.</summary>
	public static string Hash(string consequenceText)
	{
		ArgumentNullException.ThrowIfNull(consequenceText);
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(consequenceText));
		return Convert.ToHexString(bytes)[..16];
	}
}
