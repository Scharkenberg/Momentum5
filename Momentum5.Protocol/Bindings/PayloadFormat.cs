namespace Momentum5.Protocol.Bindings;

public delegate bool DecodeFunc<T>(ReadOnlySpan<byte> payload, int index, out T value);

public delegate bool EncodeFunc<T>(T value, int index, out byte[] payload, out string? error);

/// <summary>
/// Index-aware payload format. For indexed bindings the index byte itself is added/stripped by the binding
/// (GET selector and SET prefix); a format only sees the value bytes plus the index for validation.
/// </summary>
public interface IPayloadFormat<T>
{
	bool TryDecode(ReadOnlySpan<byte> payload, int index, out T value);

	bool TryEncode(T value, int index, out byte[] payload, out string? failure);
}

public static class PayloadFormat
{
	public static IPayloadFormat<T> Of<T>(ICodec<T> codec) => new CodecFormat<T>(codec);

	public static IPayloadFormat<T> Create<T>(DecodeFunc<T> decode, EncodeFunc<T> encode) => new FuncFormat<T>(decode, encode);

	private sealed class CodecFormat<T>(ICodec<T> codec) : IPayloadFormat<T>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, int index, out T value) => codec.TryDecode(payload, out value);

		public bool TryEncode(T value, int index, out byte[] payload, out string? failure)
		{
			payload = codec.Encode(value);
			failure = null;
			return true;
		}
	}

	private sealed class FuncFormat<T>(DecodeFunc<T> decode, EncodeFunc<T> encode) : IPayloadFormat<T>
	{
		public bool TryDecode(ReadOnlySpan<byte> payload, int index, out T value) => decode(payload, index, out value);

		public bool TryEncode(T value, int index, out byte[] payload, out string? failure) => encode(value, index, out payload, out failure);
	}
}
