namespace Momentum5.Client;

public sealed class SafetyDeniedException(GateDecision decision) : Exception(decision?.Reason ?? "Denied by safety gate.")
{
	public GateDecision Decision { get; } = decision ?? throw new ArgumentNullException(nameof(decision));
}

/// <summary>The connected device is not a MOMENTUM 5 (model gate).</summary>
public sealed class UnsupportedDeviceException(string model) : Exception($"Unsupported device '{model}'.")
{
	public string Model { get; } = model;
}

public sealed class NotConnectedException() : InvalidOperationException("No active headset session.");

/// <summary>A reply arrived but its payload did not decode.</summary>
public sealed class GaiaDecodeException(string setting, string payloadHex)
	: Exception($"Could not decode {setting} from payload [{payloadHex}].")
{
	public string Setting { get; } = setting;
}
