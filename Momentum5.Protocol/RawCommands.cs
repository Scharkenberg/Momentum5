using Momentum5.Core.Settings;

namespace Momentum5.Protocol;

/// <summary>A command with no typed binding (core, hazards, unknowns). Classified by safety only.</summary>
public sealed record RawCommand(string Name, GaiaVendor Vendor, ushort Command, SafetyClass Safety, string? Note = null)
{
	/// <summary>Catalog key: command word without type bits.</summary>
	public ushort CmdBase => GaiaWord.AsCommand(Command);
}

public static class RawCommands
{
	private const GaiaVendor S = GaiaVendor.Sennheiser;
	private const GaiaVendor Q = GaiaVendor.Qualcomm;

	public static IReadOnlyList<RawCommand> All { get; } =
	[
		// Core
		new("ApiVersion", Q, 0x0000, SafetyClass.Read),
		new("Features", Q, 0x0001, SafetyClass.Read),
		new("Features", S, 0x0001, SafetyClass.Read),
		new("FeaturesNext", S, 0x0002, SafetyClass.Read),
		new("RegisterNotification", S, 0x0007, SafetyClass.Read, "ACK 0107; payload [featureId]"),
		new("RegisterNotification", Q, 0x0007, SafetyClass.Read, "accepted on M5 [CAP]"),
		new("QualcommInfo", Q, 0x000C, SafetyClass.Read, "selector 01..08 seen in app connect sequence"),
		new("VoiceUiGet", Q, 0x0600, SafetyClass.Read),
		new("VoiceUiSet", Q, 0x0601, SafetyClass.Blocked),
		new("VoiceUiInfo", Q, 0x0602, SafetyClass.Read),
		new("EcoMode", S, 0x0609, SafetyClass.Read, "semantics unknown"),

		// Upgrade / transfer / logs
		new("UpgradeTunnel0", Q, 0x0C00, SafetyClass.Dangerous),
		new("UpgradeTunnel1", Q, 0x0C01, SafetyClass.Dangerous),
		new("UpgradeTunnel2", Q, 0x0C02, SafetyClass.Dangerous),
		new("UpgradeTunnel3", Q, 0x0C03, SafetyClass.Dangerous),
		new("DataTransfer09", Q, 0x0009, SafetyClass.Blocked),
		new("DataTransfer0A", Q, 0x000A, SafetyClass.Blocked),
		new("DataTransfer0D", Q, 0x000D, SafetyClass.Blocked),
		new("PanicLog0", Q, 0x0803, SafetyClass.Blocked),
		new("PanicLog1", Q, 0x0804, SafetyClass.Blocked),
		new("FirmwareUpgrade0", S, 0x0200, SafetyClass.Blocked, "X-12 only"),
		new("FirmwareUpgrade1", S, 0x0201, SafetyClass.Blocked, "X-12 only"),

		// Hazards (semantics unknown) and factory reset
		new("FactoryReset", S, 0x0040, SafetyClass.Dangerous, "X-08: typed RESET + checkbox + consent token"),
		new("Hazard0607", S, 0x0607, SafetyClass.Blocked),
		new("Hazard0613", S, 0x0613, SafetyClass.Blocked),
		new("Hazard0685", S, 0x0685, SafetyClass.Blocked, "notification-typed word"),
		new("DfuFamily0", S, 0x0630, SafetyClass.Dangerous, "community DFU family"),
		new("DfuFamily1", S, 0x0631, SafetyClass.Dangerous),
		new("DfuFamily2", S, 0x0632, SafetyClass.Dangerous),
		new("DfuFamily3", S, 0x0633, SafetyClass.Dangerous),
		new("DfuFamily4", S, 0x0634, SafetyClass.Dangerous),

		// Connection management / MMI
		new("ConnectionsCurrent", S, 0x1413, SafetyClass.Read, "M5 by-MAC family: reply 1513 = 6 B"),
		new("ConnectionsList", S, 0x140D, SafetyClass.Read, "reply 150D"),
		new("ConnectionsRecord", S, 0x140E, SafetyClass.Read, "selector [mac]; reply 150E [mac][flags 3B][name]"),
		new("ConnectionsStatus", S, 0x1414, SafetyClass.Read, "reply 1514 = 7 B"),
		new("ForgetA", S, 0x1403, SafetyClass.Dangerous, "semantics unknown until R-02"),
		new("ForgetB", S, 0x1405, SafetyClass.Dangerous, "semantics unknown until R-02"),
		new("ForgetC", S, 0x1406, SafetyClass.Dangerous, "semantics unknown until R-02"),
		new("MmiConfigSet", S, 0x1600, SafetyClass.Gated),
		new("MmiReset", S, 0x1604, SafetyClass.Blocked),
		// MmiConfigGet (1601) and MmiConfig2 (1605) moved to typed bindings in
		// MmiBindings.cs with X-05 (raw rows exist only for words without a typed binding).

		// Sound extras (read only); EqBandQ (100D) and EqBandFilterType (100F) moved to typed
		// bindings in EqBindings.cs with X-04 (raw rows exist only for words without a typed binding).
		new("PromptLanguageAvailable", S, 0x0808, SafetyClass.Read),
		new("PromptLanguageVersions", S, 0x0809, SafetyClass.Read),

		// Unknown (DevTools probing only)
		new("Unknown0813", S, 0x0813, SafetyClass.Unknown),
		new("Unknown0819", S, 0x0819, SafetyClass.Unknown),
		new("Unknown1011", S, 0x1011, SafetyClass.Unknown),
		new("Unknown1013", S, 0x1013, SafetyClass.Unknown),
		new("Unknown140B", S, 0x140B, SafetyClass.Unknown),
		new("Unknown2003", S, 0x2003, SafetyClass.Unknown),
		new("Unknown2005", S, 0x2005, SafetyClass.Unknown),
		new("Unknown2007", S, 0x2007, SafetyClass.Unknown),
	];
}
