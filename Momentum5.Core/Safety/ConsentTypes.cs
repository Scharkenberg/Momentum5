using System;

namespace Momentum5.Core.Safety;

/// <summary>
/// One consent decision, for the audit trail (X-01): what was consented to, when it was issued, for which
/// consequence (text hash), and how long it stayed valid. Stored by <c>ConsentService</c>; never contains secrets.
/// </summary>
public sealed record ConsentAuditEntry(
	Guid Id,
	int Vendor,
	int Command,
	string ConsequenceHash,
	DateTimeOffset IssuedAt,
	DateTimeOffset? ExpiresAt);

/// <summary>Source of consent audit entries (implemented by the Client's ConsentService).</summary>
public interface IConsentAudit
{
	/// <summary>All consent decisions of this session, oldest first.</summary>
	IReadOnlyList<ConsentAuditEntry> Entries { get; }
}
