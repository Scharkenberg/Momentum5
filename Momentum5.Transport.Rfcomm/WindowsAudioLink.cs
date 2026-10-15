using System.Runtime.InteropServices;
using System.Text;

namespace Momentum5.Transport.Rfcomm;

/// <summary>
/// Windows has no public "disconnect" call for a classic Bluetooth device. The documented way to drop the audio link is to disable its
/// profiles (A2DP, HFP, HSP, AVRCP) with <c>BluetoothSetServiceState</c>; enabling them again reconnects it. Needs hardware verification.
/// </summary>
public static class WindowsAudioLink
{
	private static readonly Guid[] Services =
	[
		new("0000110B-0000-1000-8000-00805F9B34FB"), // A2DP sink
		new("0000111E-0000-1000-8000-00805F9B34FB"), // Hands-free
		new("00001108-0000-1000-8000-00805F9B34FB"), // Headset
		new("0000110E-0000-1000-8000-00805F9B34FB"), // AVRCP
		new("0000110C-0000-1000-8000-00805F9B34FB"), // AVRCP target
	];

	/// <summary>Enables or disables the audio profiles. Returns true when at least one profile changed; <paramref name="detail"/> lists the Win32 codes.</summary>
	public static bool SetEnabled(ulong address, bool enabled, out string detail)
	{
		var info = new BluetoothDeviceInfo
		{
			Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(),
			Address = address,
			Name = string.Empty,
		};

		var sb = new StringBuilder();
		var any = false;
		var lookup = BluetoothGetDeviceInfo(IntPtr.Zero, ref info);
		if (lookup != 0)
		{
			detail = $"BluetoothGetDeviceInfo failed (error {lookup}); the device is not known to Windows.";
			return false;
		}

		foreach (var guid in Services)
		{
			var g = guid;
			var rc = BluetoothSetServiceState(IntPtr.Zero, ref info, ref g, enabled ? 1u : 0u);
			sb.Append(CultureInfo(guid)).Append('=').Append(rc).Append(' ');
			any |= rc == 0;
		}

		detail = sb.ToString().TrimEnd();
		return any;
	}

	private static string CultureInfo(Guid g) => g.ToString("N").Substring(4, 4).ToUpperInvariant();

	[DllImport("bthprops.cpl", SetLastError = true)]
	private static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref BluetoothDeviceInfo info);

	[DllImport("bthprops.cpl", SetLastError = true)]
	private static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BluetoothDeviceInfo info, ref Guid service, uint flags);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct BluetoothDeviceInfo
	{
		public uint Size;
		public ulong Address;
		public uint ClassOfDevice;
		public int Connected;
		public int Remembered;
		public int Authenticated;
		public SystemTime LastSeen;
		public SystemTime LastUsed;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
		public string Name;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemTime
	{
		public ushort Year;
		public ushort Month;
		public ushort DayOfWeek;
		public ushort Day;
		public ushort Hour;
		public ushort Minute;
		public ushort Second;
		public ushort Milliseconds;
	}
}
