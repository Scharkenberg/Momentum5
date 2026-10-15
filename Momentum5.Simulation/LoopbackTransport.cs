using Momentum5.Core.Transport;

namespace Momentum5.Simulation;

public enum LoopbackChunking
{
	Whole,
	SingleByte,
	Random,
}

public enum LoopbackDirection
{
	ClientToDevice,
	DeviceToClient,
}

public sealed record LoopbackOptions
{
	public LoopbackChunking Chunking { get; init; } = LoopbackChunking.Whole;

	public int Seed { get; init; } = 1;

	/// <summary>Delay applied to every write (uses <see cref="Time"/>).</summary>
	public TimeSpan Latency { get; init; }

	public LinkFraming Framing { get; init; } = LinkFraming.ByteStream;

	public TimeProvider? Time { get; init; }
}

/// <summary>A connected client/device pair with fault-injection knobs.</summary>
public sealed class LoopbackLink
{
	private readonly LoopbackEnd _device;
	private readonly Pipe _up;
	private readonly Pipe _down;

	internal LoopbackLink(LoopbackEnd client, LoopbackEnd device, Pipe up, Pipe down)
	{
		Client = client;
		_device = device;
		_up = up;
		_down = down;
	}

	public ILinkConnection Client { get; }

	public ILinkConnection Device => _device;

	/// <summary>Silently drops the next <paramref name="count"/> writes in the given direction.</summary>
	public void DropNext(LoopbackDirection direction, int count = 1) =>
		(direction == LoopbackDirection.ClientToDevice ? _up : _down).Drop(count);

	/// <summary>Device side closes: the client reads 0 and its <c>Closed</c> completes.</summary>
	public void InjectEof() => _device.Close();
}

public sealed class LoopbackTransport(LoopbackOptions? options = null) : ILinkTransport
{
	private readonly LoopbackOptions _options = options ?? new LoopbackOptions();

	public string Id => "loopback";

	public LinkFraming Framing => _options.Framing;

	/// <summary>Device-side hook run for every connection (attach a FakeM5 here).</summary>
	public Func<ILinkConnection, Task>? OnConnect { get; set; }

	public LoopbackLink? LastLink { get; private set; }

	public LoopbackLink CreateLink()
	{
		var up = new Pipe(_options, _options.Seed);
		var down = new Pipe(_options, _options.Seed + 1);
		var client = new LoopbackEnd(incoming: down, outgoing: up);
		var device = new LoopbackEnd(incoming: up, outgoing: down);
		client.Peer = device;
		device.Peer = client;
		return new LoopbackLink(client, device, up, down);
	}

	public ValueTask<ILinkConnection> ConnectAsync(DeviceIdentity target, CancellationToken ct)
	{
		var link = CreateLink();
		LastLink = link;
		if (OnConnect is { } hook)
		{
			_ = hook(link.Device);
		}

		return ValueTask.FromResult(link.Client);
	}
}
