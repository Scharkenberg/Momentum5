namespace Momentum5.Protocol;

public enum GaiaMessageKind
{
	Command = 0,
	Notification = 1,
	Response = 2,
	Error = 3,
}

/// <summary>Command word algebra: (feature&lt;&lt;9) | (type&lt;&lt;7) | number.</summary>
public static class GaiaWord
{
	private const ushort TypeMask = 0x0180;

	public static int Feature(ushort word) => word >> 9;

	public static int Number(ushort word) => word & 0x7F;

	public static GaiaMessageKind Kind(ushort word) => (GaiaMessageKind)((word >> 7) & 3);

	public static ushort AsCommand(ushort word) => (ushort)(word & ~TypeMask);

	public static ushort ResponseOf(ushort command) => (ushort)(AsCommand(command) | 0x0100);

	public static ushort ErrorOf(ushort command) => (ushort)(AsCommand(command) | 0x0180);

	public static ushort NotificationOf(ushort command) => (ushort)(AsCommand(command) | 0x0080);

	public static bool IsSendable(ushort word) => Kind(word) == GaiaMessageKind.Command;
}

public static class GaiaFrameExtensions
{
	public static (ushort BaseCommand, GaiaMessageKind Kind) Classify(this GaiaFrame frame) =>
		(GaiaWord.AsCommand(frame.Command), GaiaWord.Kind(frame.Command));
}
