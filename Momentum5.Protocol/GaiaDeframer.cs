using System.Buffers.Binary;

namespace Momentum5.Protocol;

/// <summary>Incremental GAIA deframer with resync on garbage. Not thread-safe by design.</summary>
public sealed class GaiaDeframer
{
	private byte[] _buffer = new byte[1024];
	private int _start;
	private int _end;

	public int MaxPayload { get; init; } = 1024;

	public long DiscardedBytes { get; private set; }

	public int Resyncs { get; private set; }

	public int BufferedBytes => _end - _start;

	public void Append(ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}

		if (_buffer.Length - _end < data.Length)
		{
			var live = _end - _start;
			if (_start > 0)
			{
				Buffer.BlockCopy(_buffer, _start, _buffer, 0, live);
				_start = 0;
				_end = live;
			}

			if (_buffer.Length - _end < data.Length)
			{
				Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _end + data.Length));
			}
		}

		data.CopyTo(_buffer.AsSpan(_end));
		_end += data.Length;
	}

	public bool TryRead(out GaiaFrame frame)
	{
		while (true)
		{
			var span = _buffer.AsSpan(_start, _end - _start);
			var ff = span.IndexOf((byte)0xFF);
			if (ff < 0)
			{
				if (!span.IsEmpty)
				{
					Drop(span.Length);
				}

				break;
			}

			if (ff > 0)
			{
				Drop(ff);
				continue;
			}

			if (span.Length < 2)
			{
				break;
			}

			if (span[1] is not (3 or 4))
			{
				Drop(1);
				continue;
			}

			if (span.Length < 4)
			{
				break;
			}

			var length = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
			if (length > MaxPayload)
			{
				Drop(1);
				continue;
			}

			var total = GaiaFrame.HeaderSize + length;
			if (span.Length < total)
			{
				break;
			}

			frame = new GaiaFrame(
				(GaiaVendor)BinaryPrimitives.ReadUInt16BigEndian(span[4..]),
				BinaryPrimitives.ReadUInt16BigEndian(span[6..]),
				span.Slice(GaiaFrame.HeaderSize, length).ToArray(),
				span[1]);
			_start += total;
			if (_start == _end)
			{
				_start = _end = 0;
			}

			return true;
		}

		frame = default;
		return false;
	}

	private void Drop(int count)
	{
		_start += count;
		DiscardedBytes += count;
		Resyncs++;
		if (_start == _end)
		{
			_start = _end = 0;
		}
	}
}
