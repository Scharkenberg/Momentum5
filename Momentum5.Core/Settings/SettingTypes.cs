namespace Momentum5.Core.Settings;

public sealed record SettingKey<T>(string Name, int Index = -1);

public enum SettingGroup
{
	Identity,
	Power,
	Anc,
	Transparency,
	Eq,
	Sound,
	Spatial,
	Calls,
	Connections,
	Device,
}

public enum SafetyClass
{
	Read,
	Write,
	Gated,
	Dangerous,
	Blocked,
	Unknown,
}

public enum TrustTier
{
	Unknown,
	Compatible,
	Verified,
}

public sealed record SetResult<T>(bool Verified, T Actual);

public sealed record SessionInfo(string Model, string Firmware, string Serial, TrustTier Trust, string TransportId);
