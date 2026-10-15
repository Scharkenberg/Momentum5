using System.Buffers;
using System.Buffers.Binary;

namespace Momentum5.Protocol;

/// <summary>GAIA v3 frame: FF | VER | LEN u16BE (payload only) | VENDOR u16BE | COMMAND u16BE | PAYLOAD.</summary>
public readonly record struct GaiaFrame(GaiaVendor Vendor, ushort Command, ReadOnlyMemory<byte> Payload, byte Version = 3)
{
	public const int HeaderSize = 8;
	public const int MaxPayloadLength = ushort.MaxValue;

	public int EncodedLength => HeaderSize + Payload.Length;

	public byte[] Encode()
	{
		var buffer = new byte[EncodedLength];
		Write(buffer);
		return buffer;
	}

	public void WriteTo(IBufferWriter<byte> writer)
	{
		ArgumentNullException.ThrowIfNull(writer);
		Write(writer.GetSpan(EncodedLength));
		writer.Advance(EncodedLength);
	}

	private void Write(Span<byte> destination)
	{
		if (Payload.Length > MaxPayloadLength)
		{
			throw new ArgumentOutOfRangeException(nameof(Payload), Payload.Length, "GAIA payload exceeds 65535 bytes.");
		}

		destination[0] = 0xFF;
		destination[1] = Version;
		BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)Payload.Length);
		BinaryPrimitives.WriteUInt16BigEndian(destination[4..], (ushort)Vendor);
		BinaryPrimitives.WriteUInt16BigEndian(destination[6..], Command);
		Payload.Span.CopyTo(destination[HeaderSize..]);
	}
}
