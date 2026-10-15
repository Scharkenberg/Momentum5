using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>Observes every frame crossing the link (TX before the write, RX after deframing).</summary>
public interface IFrameTap
{
	void Record(FrameDirection direction, string transportId, GaiaFrame frame);
}

public sealed class NullFrameTap : IFrameTap
{
	private NullFrameTap()
	{
	}

	public static NullFrameTap Instance { get; } = new();

	public void Record(FrameDirection direction, string transportId, GaiaFrame frame)
	{
	}
}
