namespace Momentum5.Protocol;

public enum GaiaErrorReason : byte
{
	FeatureUnsupported = 0x00,
	OperationUnsupported = 0x01,
	MalformedPayload = 0x05,
	IndexOutOfRange = 0x83,
}

public sealed class GaiaProtocolException : Exception
{
	public GaiaProtocolException(GaiaVendor vendor, ushort command, byte reason)
		: base($"Headset rejected {vendor} command 0x{command:X4}: {Describe(reason)} (0x{reason:X2}).")
	{
		Vendor = vendor;
		Command = command;
		Reason = reason;
	}

	public GaiaVendor Vendor { get; }

	/// <summary>Base (command-typed) word that was rejected.</summary>
	public ushort Command { get; }

	/// <summary>Raw reason byte (unknown values preserved).</summary>
	public byte Reason { get; }

	public GaiaErrorReason? KnownReason => Enum.IsDefined((GaiaErrorReason)Reason) ? (GaiaErrorReason)Reason : null;

	public static string Describe(byte reason) => (GaiaErrorReason)reason switch
	{
		GaiaErrorReason.FeatureUnsupported => "feature not supported",
		GaiaErrorReason.OperationUnsupported => "operation not supported",
		GaiaErrorReason.MalformedPayload => "malformed payload",
		GaiaErrorReason.IndexOutOfRange => "index out of range",
		_ => "unknown reason",
	};

	/// <summary>Builds the exception from an error-typed frame; null when the frame is not an error.</summary>
	public static GaiaProtocolException? FromFrame(GaiaFrame frame)
	{
		var (baseCommand, kind) = frame.Classify();
		return kind == GaiaMessageKind.Error
			? new GaiaProtocolException(frame.Vendor, baseCommand, frame.Payload.Length > 0 ? frame.Payload.Span[0] : (byte)0xFF)
			: null;
	}
}
