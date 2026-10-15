using System.Buffers.Binary;
using System.Threading.Channels;
using Momentum5.Core.Transport;
using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>
/// Frames over an <see cref="ILinkConnection"/>: one read loop, ordered <see cref="Frames"/>, single-writer
/// <see cref="SendAsync"/>. Datagram links carry VENDOR|COMMAND|PAYLOAD (no FF/VER/LEN header). Owns the connection.
/// </summary>
public sealed class GaiaLink : IAsyncDisposable
{
	private readonly ILinkConnection _connection;
	private readonly LinkFraming _framing;
	private readonly IFrameTap _tap;
	private readonly Channel<GaiaFrame> _frames = Channel.CreateUnbounded<GaiaFrame>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly CancellationTokenSource _cts = new();
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task _loop;
	private int _disposed;

	public GaiaLink(ILinkConnection connection, LinkFraming framing, IFrameTap? tap = null)
	{
		ArgumentNullException.ThrowIfNull(connection);
		_connection = connection;
		_framing = framing;
		_tap = tap ?? NullFrameTap.Instance;
		_ = connection.Closed.ContinueWith(_ => _closed.TrySetResult(), TaskScheduler.Default);
		_loop = Task.Run(() => ReadLoopAsync(_cts.Token));
	}

	public string TransportId => _connection.TransportId;

	/// <summary>Received frames in arrival order; completes when the link ends.</summary>
	public ChannelReader<GaiaFrame> Frames => _frames.Reader;

	/// <summary>Completes when the link is lost, the peer closes, or the link is disposed.</summary>
	public Task Closed => _closed.Task;

	/// <summary>Why the read loop ended abnormally (null for a clean EOF or dispose).</summary>
	public Exception? CloseReason { get; private set; }

	public async ValueTask SendAsync(GaiaFrame frame, CancellationToken ct)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		await _writeGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			_tap.Record(FrameDirection.Tx, TransportId, frame);
			var bytes = _framing == LinkFraming.ByteStream ? frame.Encode() : EncodeDatagram(frame);
			await _connection.WriteAsync(bytes, ct).ConfigureAwait(false);
		}
		finally
		{
			_writeGate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		await _cts.CancelAsync().ConfigureAwait(false);
		await _connection.DisposeAsync().ConfigureAwait(false);
		try
		{
			await _loop.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}

		_closed.TrySetResult();
		_writeGate.Dispose();
		_cts.Dispose();
	}

	private static byte[] EncodeDatagram(GaiaFrame frame)
	{
		var bytes = new byte[4 + frame.Payload.Length];
		BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)frame.Vendor);
		BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), frame.Command);
		frame.Payload.Span.CopyTo(bytes.AsSpan(4));
		return bytes;
	}

	private static bool TryUnwrapDatagram(ReadOnlySpan<byte> message, out GaiaFrame frame)
	{
		if (message.Length < 4)
		{
			frame = default;
			return false;
		}

		frame = new GaiaFrame(
			(GaiaVendor)BinaryPrimitives.ReadUInt16BigEndian(message),
			BinaryPrimitives.ReadUInt16BigEndian(message[2..]),
			message[4..].ToArray());
		return true;
	}

	private async Task ReadLoopAsync(CancellationToken ct)
	{
		var buffer = new byte[4096];
		var deframer = new GaiaDeframer();
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var n = await _connection.ReadAsync(buffer, ct).ConfigureAwait(false);
				if (n == 0)
				{
					break;
				}

				if (_framing == LinkFraming.ByteStream)
				{
					deframer.Append(buffer.AsSpan(0, n));
					while (deframer.TryRead(out var frame))
					{
						Publish(frame);
					}
				}
				else if (TryUnwrapDatagram(buffer.AsSpan(0, n), out var datagram))
				{
					Publish(datagram);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
#pragma warning disable CA1031 // any transport failure just ends the link; the reason is kept
		catch (Exception ex)
#pragma warning restore CA1031
		{
			CloseReason = ex;
		}
		finally
		{
			_frames.Writer.TryComplete();
			_closed.TrySetResult();
		}
	}

	private void Publish(GaiaFrame frame)
	{
		_tap.Record(FrameDirection.Rx, TransportId, frame);
		_frames.Writer.TryWrite(frame);
	}
}
