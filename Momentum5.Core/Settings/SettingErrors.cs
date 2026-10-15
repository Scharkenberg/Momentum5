namespace Momentum5.Core.Settings;

/// <summary>The headset answered 0x83 (index out of range) for an EQ band.</summary>
public sealed class EqBandOutOfRangeException : Exception
{
	public int Band { get; }

	public EqBandOutOfRangeException(int band)
		: base($"EQ band {band} is out of range for this headset.")
	{
		Band = band;
	}
}
