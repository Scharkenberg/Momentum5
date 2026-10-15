using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Tests;

public class IdentityBindingTests
{
	[Fact]
	public void GoldenDecodes()
	{
		Assert.Equal("M5AEBT Black", Support.Decode(Keys.Model, "4D 35 41 45 42 54 20 42 6C 61 63 6B 00"));
		Assert.Equal(new FirmwareVersion(6, 21, 5), Support.Decode(Keys.Firmware, "00 06 00 15 00 05"));
		Assert.Equal(30, Support.Decode(Keys.BatteryPercent, "1E"));
	}

	[Fact]
	public void TimeoutsAndPushWords()
	{
		Assert.Equal(TimeSpan.FromSeconds(9), Support.Of(Keys.BatteryPercent).Timeout);
		Assert.Equal((ushort?)0x0683, Support.Of(Keys.BatteryPercent).PushCmd);
		Assert.Equal((ushort?)0x0682, Support.Of(Keys.ChargingState).PushCmd);
		Assert.Equal(TimeSpan.FromSeconds(10), Support.Of(Keys.Firmware).Timeout);
		Assert.Equal(TimeSpan.FromSeconds(5), Support.Of(Keys.Model).Timeout);
		Assert.Equal(GaiaVendor.Qualcomm, Support.Of(Keys.Serial).Vendor);
	}

	[Fact]
	public void AutoOffEncodesAndDecodes()
	{
		var b = Support.Of(Keys.AutoOff);
		Assert.Equal((ushort?)0x0600, b.SetCmd);
		Assert.Equal((ushort?)0x0601, b.GetCmd);
		Assert.Equal(Hex.Parse("00"), b.BuildGetPayload(-1));
		Assert.Equal(Hex.Parse("000E10"), Support.Set(Keys.AutoOff, AutoOffTimer.Minutes60));
		Assert.Equal(Hex.Parse("000000"), Support.Set(Keys.AutoOff, AutoOffTimer.Never));
		Assert.Equal(AutoOffTimer.Minutes60, Support.Decode(Keys.AutoOff, "00 0E 10"));
		Assert.Equal(AutoOffTimer.Minutes15, Support.Decode(Keys.AutoOff, "03 84"));
		Assert.False(b.Format.TryDecode(Hex.Parse("0001"), -1, out _));
	}

	[Fact]
	public void LocalNameLimits()
	{
		Assert.True(Support.TrySet(Keys.LocalName, "Sixteen bytes!!!"));
		Assert.False(Support.TrySet(Keys.LocalName, "Seventeen bytes!!!"));
		Assert.False(Support.TrySet(Keys.LocalName, string.Empty));
		Assert.False(Support.TrySet(Keys.LocalName, "a\0b"));
		Assert.Equal(Hex.Parse("4142"), Support.Set(Keys.LocalName, "AB"));
		Assert.Equal(SafetyClass.Gated, Support.Of(Keys.LocalName).Safety);
	}

	[Fact]
	public void BatteryProtectionRoundTrip()
	{
		Assert.True(Support.Decode(Keys.BatteryProtection, "01"));
		Assert.Equal(Hex.Parse("01"), Support.Set(Keys.BatteryProtection, true));
	}
}
