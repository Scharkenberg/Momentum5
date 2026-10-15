using System.Buffers;
using Momentum5.Protocol;

namespace Momentum5.Tests;

public class GaiaFrameTests
{
	[Theory]
	[InlineData(GaiaVendor.Sennheiser, (ushort)0x1206, "", "FF 03 00 00 04 95 12 06")]
	[InlineData(GaiaVendor.Sennheiser, (ushort)0x0603, "", "FF 03 00 00 04 95 06 03")]
	[InlineData(GaiaVendor.Sennheiser, (ushort)0x1A02, "32", "FF 03 00 01 04 95 1A 02 32")]
	[InlineData(GaiaVendor.Qualcomm, (ushort)0x0007, "", "FF 03 00 00 00 1D 00 07")]
	public void EncodeMatchesGolden(GaiaVendor vendor, ushort command, string payloadHex, string expectedHex)
	{
		var frame = new GaiaFrame(vendor, command, Hex.Parse(payloadHex));
		Assert.Equal(Hex.Parse(expectedHex), frame.Encode());
		Assert.Equal(frame.Encode().Length, frame.EncodedLength);
	}

	[Fact]
	public void Version4IsEncoded()
	{
		var bytes = new GaiaFrame(GaiaVendor.Sennheiser, 0x1206, default, 4).Encode();
		Assert.Equal(Hex.Parse("FF 04 00 00 04 95 12 06"), bytes);
	}

	[Fact]
	public void OversizePayloadThrows()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x1206, new byte[GaiaFrame.MaxPayloadLength + 1]);
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = frame.Encode(); });
	}

	[Fact]
	public void MaxPayloadEncodes()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x1206, new byte[GaiaFrame.MaxPayloadLength]);
		var bytes = frame.Encode();
		Assert.Equal(GaiaFrame.HeaderSize + GaiaFrame.MaxPayloadLength, bytes.Length);
		Assert.Equal(0xFF, bytes[2]);
		Assert.Equal(0xFF, bytes[3]);
	}

	[Fact]
	public void WriteToEqualsEncode()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x1A02, Hex.Parse("32"));
		var writer = new ArrayBufferWriter<byte>();
		frame.WriteTo(writer);
		Assert.True(writer.WrittenSpan.SequenceEqual(frame.Encode()));
	}
}
