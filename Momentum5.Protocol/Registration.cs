namespace Momentum5.Protocol;

public readonly record struct RegistrationEntry(GaiaVendor Vendor, byte Feature);

/// <summary>RegisterNotification (0007) frames.</summary>
public static class Registration
{
	public const ushort RegisterCommand = 0x0007;

	/// <summary>Features we register by default (power, anc, userEq, deviceManagement, transparency, mmi, versions).</summary>
	public static IReadOnlyList<byte> Default { get; } = [3, 13, 8, 10, 12, 11, 2];

	/// <summary>The official app's cold-start order on M5 fw 6.21.5 [CAP2].</summary>
	public static IReadOnlyList<RegistrationEntry> OfficialColdStart { get; } =
	[
		new(GaiaVendor.Sennheiser, 0x03),
		new(GaiaVendor.Sennheiser, 0x04),
		new(GaiaVendor.Qualcomm, 0x07),
		new(GaiaVendor.Sennheiser, 0x0A),
		new(GaiaVendor.Sennheiser, 0x02),
		new(GaiaVendor.Qualcomm, 0x06),
		new(GaiaVendor.Sennheiser, 0x0D),
		new(GaiaVendor.Sennheiser, 0x0C),
		new(GaiaVendor.Sennheiser, 0x1B),
		new(GaiaVendor.Sennheiser, 0x0B),
		new(GaiaVendor.Sennheiser, 0x08),
		new(GaiaVendor.Sennheiser, 0x10),
	];

	public static GaiaFrame Frame(GaiaVendor vendor, byte feature) => new(vendor, RegisterCommand, new[] { feature });

	/// <summary>Frames for the given features; <paramref name="vendor"/> allows the Qualcomm fallback.</summary>
	public static IReadOnlyList<GaiaFrame> Frames(IEnumerable<byte> features, GaiaVendor vendor = GaiaVendor.Sennheiser) =>
		[.. features.Select(f => Frame(vendor, f))];

	public static IReadOnlyList<GaiaFrame> FramesFor(IEnumerable<RegistrationEntry> entries) =>
		[.. entries.Select(e => Frame(e.Vendor, e.Feature))];
}
