using Momentum5.Protocol;

namespace Momentum5.Tests;

public class GaiaWordTests
{
	[Fact]
	public void Algebra()
	{
		Assert.Equal(0x1306, GaiaWord.ResponseOf(0x1206));
		Assert.Equal(0x1386, GaiaWord.ErrorOf(0x1206));
		Assert.Equal(0x1A83, GaiaWord.NotificationOf(0x1A03));
		Assert.Equal(0x0703, GaiaWord.ResponseOf(0x0603));
		Assert.Equal(0x1206, GaiaWord.AsCommand(0x1386));
		Assert.Equal(0x1206, GaiaWord.AsCommand(0x1306));
		Assert.Equal(9, GaiaWord.Feature(0x1206));
		Assert.Equal(6, GaiaWord.Number(0x1206));
	}

	[Theory]
	[InlineData(0x0602, GaiaMessageKind.Command)]
	[InlineData(0x1A83, GaiaMessageKind.Notification)]
	[InlineData(0x0302, GaiaMessageKind.Response)]
	[InlineData(0x1386, GaiaMessageKind.Error)]
	public void KindOf(int word, GaiaMessageKind kind) =>
		Assert.Equal(kind, GaiaWord.Kind((ushort)word));

	[Fact]
	public void SendableOnlyCommands()
	{
		Assert.True(GaiaWord.IsSendable(0x1206));
		Assert.False(GaiaWord.IsSendable(0x1306));
		Assert.False(GaiaWord.IsSendable(0x1386));
		Assert.False(GaiaWord.IsSendable(0x1A83));
	}

	[Fact]
	public void ErrorFrameClassifiesAndParses()
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse("FF 03 00 01 04 95 13 86 05"));
		Assert.True(d.TryRead(out var frame));
		var (baseCmd, kind) = frame.Classify();
		Assert.Equal(0x1206, baseCmd);
		Assert.Equal(GaiaMessageKind.Error, kind);

		var ex = GaiaProtocolException.FromFrame(frame);
		Assert.NotNull(ex);
		Assert.Equal(5, ex.Reason);
		Assert.Equal(GaiaErrorReason.MalformedPayload, ex.KnownReason);
		Assert.Equal(GaiaVendor.Sennheiser, ex.Vendor);
		Assert.Equal(0x1206, ex.Command);
	}

	[Fact]
	public void NonErrorFrameIsNotException()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x1306, default);
		Assert.Null(GaiaProtocolException.FromFrame(frame));
	}
}
