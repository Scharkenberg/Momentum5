namespace Momentum5.Core.Transport;

public enum BtError
{
	Unknown = 0,
	ElementNotFound,
	AccessDenied,
	DeviceNotConnected,
	Timeout,
	ConnectionRefused,
	ConnectionAborted,
	ConnectionReset,
	ServiceNotFound,
	DeviceUnavailable,
}

public static class BtErrors
{
	public static BtError FromHResult(int hresult) => unchecked((uint)hresult) switch
	{
		0x80070490 => BtError.ElementNotFound,
		0x80070005 => BtError.AccessDenied,
		0x8007048F => BtError.DeviceNotConnected,
		0x8007274C => BtError.Timeout,
		0x8007274D => BtError.ConnectionRefused,
		0x80072745 => BtError.ConnectionAborted,
		0x80072746 => BtError.ConnectionReset,
		0x8007277C => BtError.ServiceNotFound,
		0x800710DF => BtError.DeviceUnavailable,
		_ => BtError.Unknown,
	};

	public static string UserText(BtError error) => error switch
	{
		BtError.ElementNotFound => "Headset service not found. Make sure the headphones are paired in Windows Settings, switched on, and not in use by another app.",
		BtError.AccessDenied => "Windows denied Bluetooth access. Check Settings > Privacy & security > Bluetooth.",
		BtError.DeviceNotConnected => "The headphones are paired but not connected. Switch them on and wait for the connection.",
		BtError.Timeout => "The headphones did not respond in time. Move closer and try again.",
		BtError.ConnectionRefused => "The headphones refused the connection. Re-pair them in Windows Settings if this persists.",
		BtError.ConnectionAborted => "The connection was aborted.",
		BtError.ConnectionReset => "The connection was reset by the headphones.",
		BtError.ServiceNotFound => "The headphones are still starting up. Try again in a few seconds.",
		BtError.DeviceUnavailable => "Bluetooth appears to be switched off.",
		_ => "Unexpected Bluetooth error.",
	};
}
