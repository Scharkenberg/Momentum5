using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Momentum5.Core.Domain;

namespace Momentum5.Protocol.Bindings;

/// <summary>Payload codec. Decoding never throws: wrong length or value returns false.</summary>
public interface ICodec<T>
{
	bool TryDecode(ReadOnlySpan<byte> payload, out T value);

	byte[] Encode(T value);
}

public static class Codecs
{
	public static ICodec<bool> Bool { get; } = new BoolCodec(false);

	/// <summary>Bool whose wire value is the opposite of the domain value.</summary>
	public static ICodec<bool> InvertedBool { get; } = new BoolCodec(true);

	public static ICodec<byte> U8 { get; } = new U8Codec(byte.MinValue, byte.MaxValue);

	public static ICodec<int> Level0to100 { get; } = new LevelCodec();

	public static ICodec<sbyte> I8 { get; } = new I8Codec();

	public static ICodec<ushort> U16BE { get; } = new U16Codec();

	public static ICodec<uint> U32BE { get; } = new U32Codec();

	/// <summary>UTF-8, NUL-terminated on the wire (cut at the first NUL when decoding).</summary>
	public static ICodec<string> CString { get; } = new CStringCodec();

	public static ICodec<FirmwareVersion> VersionTriple { get; } = new VersionCodec();

	public static ICodec<byte[]> RawBytes { get; } = new RawCodec();

	public static ICodec<byte> U8Range(byte min, byte max) => new U8Codec(min, max);

	public static ICodec<TEnum> ByteEnum<TEnum>()
		where TEnum : struct, Enum => new ByteEnumCodec<TEnum>();

	private sealed class BoolCodec(bool inverted) : ICodec<bool>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out bool value)
		{
			if (payload.Length == 1 && payload[0] <= 1)
			{
				value = (payload[0] == 1) != inverted;
				return true;
			}

			value = false;
			return false;
		}

		public byte[] Encode(bool value) => [(byte)(value != inverted ? 1 : 0)];
	}

	private sealed class U8Codec(byte min, byte max) : ICodec<byte>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out byte value)
		{
			if (payload.Length == 1 && payload[0] >= min && payload[0] <= max)
			{
				value = payload[0];
				return true;
			}

			value = 0;
			return false;
		}

		public byte[] Encode(byte value) => [Math.Clamp(value, min, max)];
	}

	private sealed class LevelCodec : ICodec<int>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out int value)
		{
			if (payload.Length == 1 && payload[0] <= 100)
			{
				value = payload[0];
				return true;
			}

			value = 0;
			return false;
		}

		public byte[] Encode(int value) => [(byte)Math.Clamp(value, 0, 100)];
	}

	private sealed class I8Codec : ICodec<sbyte>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out sbyte value)
		{
			if (payload.Length == 1)
			{
				value = unchecked((sbyte)payload[0]);
				return true;
			}

			value = 0;
			return false;
		}

		public byte[] Encode(sbyte value) => [unchecked((byte)value)];
	}

	private sealed class U16Codec : ICodec<ushort>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out ushort value)
		{
			if (payload.Length == 2)
			{
				value = BinaryPrimitives.ReadUInt16BigEndian(payload);
				return true;
			}

			value = 0;
			return false;
		}

		public byte[] Encode(ushort value)
		{
			var b = new byte[2];
			BinaryPrimitives.WriteUInt16BigEndian(b, value);
			return b;
		}
	}

	private sealed class U32Codec : ICodec<uint>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out uint value)
		{
			if (payload.Length == 4)
			{
				value = BinaryPrimitives.ReadUInt32BigEndian(payload);
				return true;
			}

			value = 0;
			return false;
		}

		public byte[] Encode(uint value)
		{
			var b = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(b, value);
			return b;
		}
	}

	private sealed class CStringCodec : ICodec<string>
	{
		private static readonly UTF8Encoding Strict = new(false, true);

		public bool TryDecode(ReadOnlySpan<byte> payload, out string value)
		{
			var end = payload.IndexOf((byte)0);
			var text = end >= 0 ? payload[..end] : payload;
			try
			{
				value = Strict.GetString(text);
				return true;
			}
			catch (DecoderFallbackException)
			{
				value = string.Empty;
				return false;
			}
		}

		public byte[] Encode(string value)
		{
			var body = Encoding.UTF8.GetBytes(value);
			var result = new byte[body.Length + 1];
			body.CopyTo(result, 0);
			return result;
		}
	}

	private sealed class VersionCodec : ICodec<FirmwareVersion>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out FirmwareVersion value)
		{
			if (payload.Length == 6)
			{
				value = new FirmwareVersion(
					BinaryPrimitives.ReadUInt16BigEndian(payload),
					BinaryPrimitives.ReadUInt16BigEndian(payload[2..]),
					BinaryPrimitives.ReadUInt16BigEndian(payload[4..]));
				return true;
			}

			value = default;
			return false;
		}

		public byte[] Encode(FirmwareVersion value)
		{
			var b = new byte[6];
			BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value.Major);
			BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)value.Minor);
			BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)value.Patch);
			return b;
		}
	}

	private sealed class RawCodec : ICodec<byte[]>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out byte[] value)
		{
			value = payload.ToArray();
			return true;
		}

		public byte[] Encode(byte[] value) => [.. value];
	}

	private sealed class ByteEnumCodec<TEnum> : ICodec<TEnum>
		where TEnum : struct, Enum
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, out TEnum value)
		{
			if (payload.Length == 1)
			{
				var candidate = (TEnum)Enum.ToObject(typeof(TEnum), payload[0]);
				if (Enum.IsDefined(candidate))
				{
					value = candidate;
					return true;
				}
			}

			value = default;
			return false;
		}

		public byte[] Encode(TEnum value) => [Convert.ToByte(value, CultureInfo.InvariantCulture)];
	}
}
