using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>One captured frame. <see cref="Text"/> is formatted lazily so recording stays cheap on the link thread.</summary>
public sealed class TraceEntry(long sequence, DateTimeOffset time, FrameDirection direction, string transport, GaiaFrame frame)
{
	private string? _text;
	private string? _hex;

	public long Sequence { get; } = sequence;

	public DateTimeOffset Time { get; } = time;

	public FrameDirection Direction { get; } = direction;

	public string Transport { get; } = transport;

	public GaiaFrame Frame { get; } = frame;

	/// <summary>"S:0603" style key used by the command filter.</summary>
	public string Command
	{
		get
		{
			var (cmd, _) = Frame.Classify();
			var vendor = Frame.Vendor switch { GaiaVendor.Sennheiser => "S", GaiaVendor.Qualcomm => "Q", _ => ((ushort)Frame.Vendor).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) };
			return $"{vendor}:{cmd:X4}";
		}
	}

	public string Text => _text ??= GaiaFormatter.Describe(Frame, Direction);

	public string Hex => _hex ??= GaiaFormatter.Hex(Frame.Encode());

	public string Line => $"{Time:HH:mm:ss.fff}  {Text}";
}

/// <summary>Ring buffer of the most recent frames for the trace page. Thread-safe; readers poll <see cref="Version"/>.</summary>
public sealed class InMemoryTap(int capacity = InMemoryTap.DefaultCapacity) : IFrameTap
{
	public const int DefaultCapacity = 10_000;

	private readonly TraceEntry?[] _ring = new TraceEntry?[Math.Max(1, capacity)];
	private readonly Lock _gate = new();
	private readonly TimeProvider _time = TimeProvider.System;
	private long _next;
	private long _version;

	public long Version => Interlocked.Read(ref _version);

	public int Capacity => _ring.Length;

	public void Record(FrameDirection direction, string transportId, GaiaFrame frame)
	{
		lock (_gate)
		{
			var seq = _next++;
			_ring[seq % _ring.Length] = new TraceEntry(seq, _time.GetUtcNow().ToLocalTime(), direction, transportId, frame);
			Interlocked.Increment(ref _version);
		}
	}

	/// <summary>Oldest to newest copy of the buffer.</summary>
	public TraceEntry[] Snapshot()
	{
		lock (_gate)
		{
			var count = (int)Math.Min(_next, _ring.Length);
			var result = new TraceEntry[count];
			var start = _next - count;
			for (var i = 0; i < count; i++)
			{
				result[i] = _ring[(start + i) % _ring.Length]!;
			}

			return result;
		}
	}

	public void Clear()
	{
		lock (_gate)
		{
			Array.Clear(_ring);
			_next = 0;
			Interlocked.Increment(ref _version);
		}
	}
}

/// <summary>Fans every frame out to several taps (file log plus in-memory trace).</summary>
public sealed class CompositeFrameTap(params IFrameTap[] taps) : IFrameTap
{
	public void Record(FrameDirection direction, string transportId, GaiaFrame frame)
	{
		foreach (var tap in taps)
		{
			tap.Record(direction, transportId, frame);
		}
	}
}
