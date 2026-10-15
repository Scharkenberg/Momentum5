#pragma warning disable CA5394 // seeded Random is intentional (reproducible chunking)
using System.Threading.Channels;
using Momentum5.Core.Transport;

namespace Momentum5.Simulation;

/// <summary>One direction of a loopback link: chunking, latency, drop and EOF knobs.</summary>
internal sealed class Pipe(LoopbackOptions options, int seed)
{
	private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
	private readonly Random _rng = new(seed);
	private byte[] _rest = [];
	private int _restPos;
	private int _drop;

	public void Drop(int count) => Interlocked.Add(ref _drop, count);

	public void Complete() => _channel.Writer.TryComplete();

	public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
	{
		if (options.Latency > TimeSpan.Zero)
		{
			await Task.Delay(options.Latency, options.Time ?? TimeProvider.System, ct).ConfigureAwait(false);
		}

		if (ConsumeDrop() || data.IsEmpty)
		{
			return;
		}

		foreach (var chunk in Split(data))
		{
			if (!_channel.Writer.TryWrite(chunk))
			{
				throw new LinkException(BtError.ConnectionAborted, unchecked((int)0x80072745), "Loopback link is closed.");
			}
		}
	}

	public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
	{
		if (_restPos >= _rest.Length)
		{
			if (!await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false) || !_channel.Reader.TryRead(out var next))
			{
				return 0;
			}

			_rest = next;
			_restPos = 0;
		}

		var n = Math.Min(buffer.Length, _rest.Length - _restPos);
		_rest.AsMemory(_restPos, n).CopyTo(buffer);
		_restPos += n;
		return n;
	}

	private bool ConsumeDrop()
	{
		while (true)
		{
			var d = Volatile.Read(ref _drop);
			if (d <= 0)
			{
				return false;
			}

			if (Interlocked.CompareExchange(ref _drop, d - 1, d) == d)
			{
				return true;
			}
		}
	}

	private IEnumerable<byte[]> Split(ReadOnlyMemory<byte> data)
	{
		if (options.Framing == LinkFraming.Datagram || options.Chunking == LoopbackChunking.Whole)
		{
			yield return data.ToArray();
			yield break;
		}

		if (options.Chunking == LoopbackChunking.SingleByte)
		{
			for (var i = 0; i < data.Length; i++)
			{
				yield return [data.Span[i]];
			}

			yield break;
		}

		var pos = 0;
		while (pos < data.Length)
		{
			int size;
			lock (_rng)
			{
				size = _rng.Next(1, data.Length - pos + 1);
			}

			yield return data.Slice(pos, size).ToArray();
			pos += size;
		}
	}
}

internal sealed class LoopbackEnd(Pipe incoming, Pipe outgoing) : ILinkConnection
{
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _disposed;

	public LoopbackEnd? Peer { get; set; }

	public string TransportId => "loopback";

	public Task Closed => _closed.Task;

	public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
	{
		if (Volatile.Read(ref _disposed) != 0)
		{
			return 0;
		}

		var n = await incoming.ReadAsync(buffer, ct).ConfigureAwait(false);
		if (n == 0)
		{
			_closed.TrySetResult();
		}

		return n;
	}

	public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
	{
		if (Volatile.Read(ref _disposed) != 0)
		{
			throw new LinkException(BtError.ConnectionAborted, unchecked((int)0x80072745), "Link is closed.");
		}

		await outgoing.WriteAsync(data, ct).ConfigureAwait(false);
	}

	public void Close()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		outgoing.Complete();
		incoming.Complete();
		_closed.TrySetResult();
		Peer?.MarkPeerClosed();
	}

	public ValueTask DisposeAsync()
	{
		Close();
		return ValueTask.CompletedTask;
	}

	internal void MarkPeerClosed() => _closed.TrySetResult();
}
