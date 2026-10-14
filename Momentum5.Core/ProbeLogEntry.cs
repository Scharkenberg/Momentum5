// Momentum5.Core/ProbeLogEntry.cs
namespace Momentum5.Core.Protocol;

public sealed record ProbeLogEntry(
	DateTimeOffset Timestamp,
	string Source,
	string Direction,
	byte[] Payload);