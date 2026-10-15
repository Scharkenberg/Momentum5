using Momentum5.Protocol;

namespace Momentum5.Tests;

public class GaiaFormatterTests
{
	private static string Describe(string hex, FrameDirection dir)
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse(hex));
		Assert.True(d.TryRead(out var f));
		return GaiaFormatter.Describe(f, dir);
	}

	[Fact]
	public void ModelTxRx()
	{
		Assert.Equal("TX S:1206 Model  [FF 03 00 00 04 95 12 06]", Describe("FF 03 00 00 04 95 12 06", FrameDirection.Tx));
		var rx = Describe("FF 03 00 0D 04 95 13 06 4D 35 41 45 42 54 20 42 6C 61 63 6B 00", FrameDirection.Rx);
		Assert.StartsWith("RX S:1206 Model=\"M5AEBT Black\"  [FF 03 00 0D", rx);
	}

	[Fact]
	public void FirmwareRx() =>
		Assert.StartsWith("RX S:1201 Firmware=6.21.5  [", Describe("FF 03 00 06 04 95 13 01 00 06 00 15 00 05", FrameDirection.Rx));

	[Fact]
	public void BatteryRx() =>
		Assert.StartsWith("RX S:0603 BatteryPercent=30  [", Describe("FF 03 00 01 04 95 07 03 1E", FrameDirection.Rx));

	[Fact]
	public void AncRxShowsAllKeys()
	{
		var rx = Describe("FF 03 00 06 04 95 1B 01 01 00 03 00 02 00", FrameDirection.Rx);
		Assert.Contains("AncAntiWind=0", rx);
		Assert.Contains("AncAdaptive=False", rx);
		Assert.Contains("AncComfort=False", rx);
	}

	[Fact]
	public void SetTxAndAck()
	{
		Assert.StartsWith("TX S:1A00 AncAntiWind = 1  [", Describe("FF 03 00 02 04 95 1A 00 01 01", FrameDirection.Tx));
		Assert.StartsWith("RX S:1A00 AncAntiWind ACK  [", Describe("FF 03 00 00 04 95 1B 00", FrameDirection.Rx));
	}

	[Fact]
	public void ErrorAndUnknown()
	{
		var err = Describe("FF 03 00 01 04 95 13 86 05", FrameDirection.Rx);
		Assert.StartsWith("RX S:1206 Model ERROR 05 (", err);
		Assert.StartsWith("TX S:7E00 ?  [", Describe("FF 03 00 00 04 95 7E 00", FrameDirection.Tx));
		Assert.StartsWith("RX S:0603 push BatteryPercent=70", Describe("FF 03 00 01 04 95 06 83 46", FrameDirection.Rx));
	}
}
