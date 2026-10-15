using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Tests;

public class EqBindingTests
{
	[Fact]
	public void ConfigDecodes() =>
		Assert.Equal(new EqConfig(8, -6.0, 6.0), Support.Decode(Keys.EqConfig, "08 C4 3C 00 00"));

	[Fact]
	public void FrequencyDecodes() =>
		Assert.Equal(8000, Support.Decode(Keys.EqBandFreq(5), "05 1F 40"));

	[Fact]
	public void BandSetEncodesIndexThenTenths()
	{
		Assert.Equal(Hex.Parse("0001"), Support.Set(Keys.EqBand(0), 0.1));
		Assert.Equal(Hex.Parse("03C4"), Support.Set(Keys.EqBand(3), -6.0));
		Assert.Equal((ushort?)0x1001, Support.Of(Keys.EqBand(0)).SetCmd);
		Assert.Equal(Hex.Parse("05"), Support.Of(Keys.EqBand(5)).BuildGetPayload(5));
		Assert.False(Support.TrySet(Keys.EqBand(0), 20.0));
	}

	[Fact]
	public void BandDecodes() => Assert.Equal(0.1, Support.Decode(Keys.EqBand(0), "01"));

	[Fact]
	public void AllGainsDecodeZeros()
	{
		var gains = Support.Decode(Keys.EqAllGains, "00 00 00 00 00 00 00 00");
		Assert.Equal(8, gains.Count);
		Assert.All(gains, g => Assert.Equal(0.0, g));
		Assert.Equal(Hex.Parse("00"), Support.Of(Keys.EqAllGains).BuildGetPayload(-1));
	}

	[Fact]
	public void BandOutOfRangeMapsToTypedError()
	{
		var ex = Support.Of(Keys.EqBand(9)).MapError(0x83, 9);
		var typed = Assert.IsType<EqBandOutOfRangeException>(ex);
		Assert.Equal(9, typed.Band);
		Assert.Null(Support.Of(Keys.EqBand(9)).MapError(0x05, 9));
	}

	[Fact]
	public void SoundModeAndPersonalization()
	{
		Assert.Equal(SoundMode.GraphicEq, Support.Decode(Keys.SoundMode, "00 01"));
		Assert.Equal(SoundMode.Personalization, Support.Decode(Keys.SoundMode, "00 03"));
		Assert.Equal(Hex.Parse("0002"), Support.Set(Keys.SoundMode, SoundMode.SpeechClarity));
		Assert.False(Support.TrySet(Keys.SoundMode, SoundMode.Personalization));
		Assert.False(Support.Of(Keys.SoundMode).Format.TryDecode(Hex.Parse("0004"), -1, out _));
		Assert.Equal((byte)0, Support.Decode(Keys.PersonalizationState, "00"));
	}
}
