using System.Globalization;

namespace Momentum5.Core.Domain;

public readonly record struct FirmwareVersion(int Major, int Minor, int Patch) : IComparable<FirmwareVersion>
{
	public bool IsAtLeast(FirmwareVersion other) => CompareTo(other) >= 0;

	public int CompareTo(FirmwareVersion other)
	{
		var c = Major.CompareTo(other.Major);
		if (c != 0)
		{
			return c;
		}

		c = Minor.CompareTo(other.Minor);
		return c != 0 ? c : Patch.CompareTo(other.Patch);
	}

	public static bool operator <(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) < 0;

	public static bool operator >(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) > 0;

	public static bool operator <=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) <= 0;

	public static bool operator >=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) >= 0;

	public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

	public static bool TryParse(string? text, out FirmwareVersion version)
	{
		version = default;
		var parts = text?.Split('.');
		if (parts is not { Length: 3 }
			|| !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
			|| !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
			|| !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
		{
			return false;
		}

		version = new FirmwareVersion(major, minor, patch);
		return true;
	}
}
