using Momentum5.Core.Domain;

namespace Momentum5.Core.Settings;

public static partial class Keys
{
	public static readonly SettingKey<byte> PairedCount = new(nameof(PairedCount));
	public static readonly SettingKey<byte> OwnIndex = new(nameof(OwnIndex));
	public static readonly SettingKey<byte> MaxConnections = new(nameof(MaxConnections));

	public static SettingKey<PairedDevice> PairedInfo(int index) => new(nameof(PairedInfo), index);

	public static SettingKey<byte[]> PairedStatus(int index) => new(nameof(PairedStatus), index);

	/// <summary>Action: connect paired device <paramref name="index"/>. No read-back; completion arrives as a push.</summary>
	public static SettingKey<bool> ConnectPaired(int index) => new(nameof(ConnectPaired), index);
}
