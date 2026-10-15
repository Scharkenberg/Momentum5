using Momentum5.Core.Settings;

namespace Momentum5.Tests;

public class AncBindingTests
{
	[Fact]
	public void TableDecodesPerKey()
	{
		Assert.Equal((byte)0, Support.Decode(Keys.AncAntiWind, "01 00 03 00 02 00"));
		Assert.False(Support.Decode(Keys.AncAdaptive, "01 00 03 00 02 00"));
		Assert.False(Support.Decode(Keys.AncComfort, "01 00 03 00 02 00"));
		Assert.Equal((byte)1, Support.Decode(Keys.AncAntiWind, "01 01 03 00 02 00"));
		Assert.True(Support.Decode(Keys.AncAdaptive, "01 00 03 01 02 00"));
	}

	[Fact]
	public void WindValueTwoClampsToOne()
	{
		Assert.Equal((byte)1, Support.Decode(Keys.AncAntiWind, "01 02 03 00 02 00"));
	}

	[Fact]
	public void SetWritesSinglePair()
	{
		Assert.Equal(Hex.Parse("0101"), Support.Set(Keys.AncAntiWind, (byte)1));
		Assert.Equal(Hex.Parse("0102"), Support.Set(Keys.AncAntiWind, (byte)2));
		Assert.Equal(Hex.Parse("0301"), Support.Set(Keys.AncAdaptive, true));
		Assert.Equal((ushort?)0x1A00, Support.Of(Keys.AncAntiWind).SetCmd);
		Assert.Equal((ushort?)0x1A00, Support.Of(Keys.AncAdaptive).SetCmd);
		Assert.False(Support.TrySet(Keys.AncAntiWind, (byte)3));
	}

	[Fact]
	public void ComfortIsNeverWritten()
	{
		Assert.Null(Support.Of(Keys.AncComfort).SetCmd);
		Assert.False(Support.TrySet(Keys.AncComfort, true));
	}

	[Fact]
	public void SharedWordsAndCoupling()
	{
		foreach (var key in new[] { Keys.AncAntiWind.Name, Keys.AncAdaptive.Name, Keys.AncComfort.Name })
		{
			var b = Momentum5.Protocol.GaiaCatalog.Registry.ByName(key)!;
			Assert.Equal((ushort?)0x1A01, b.GetCmd);
			Assert.Equal((ushort?)0x1A81, b.PushCmd);
			Assert.True(b.RefreshTogether);
			Assert.Equal(SettingGroup.Anc, b.Group);
		}

		Assert.Equal(SettingGroup.Transparency, Support.Of(Keys.TransparencyStatus).Group);
		Assert.True(Support.Of(Keys.TransparencyStatus).RefreshTogether);
		Assert.Equal(TimeSpan.FromSeconds(2), Support.Of(Keys.AncAdaptive).BusyGrace);
	}

	[Fact]
	public void SimpleKeys()
	{
		Assert.True(Support.Decode(Keys.AncEnabled, "01"));
		Assert.Equal(100, Support.Decode(Keys.TransparencyLevel, "64"));
		Assert.Equal(Hex.Parse("32"), Support.Set(Keys.AncLevel, 50));
	}
}
