using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Tests;

public class NotificationMapTests
{
	[Fact]
	public void RegisterFrameForFeature3()
	{
		var frame = Registration.Frame(GaiaVendor.Sennheiser, 3);
		Assert.Equal(Hex.Parse("FF 03 00 01 04 95 00 07 03"), frame.Encode());
		Assert.Equal(Hex.Parse("FF 03 00 01 00 1D 00 07 03"), Registration.Frame(GaiaVendor.Qualcomm, 3).Encode());
	}

	[Fact]
	public void DefaultsAndColdStart()
	{
		Assert.Equal(Hex.Parse("03 0D 08 0A 0C 0B 02"), Registration.Default);
		Assert.Equal(7, Registration.Frames(Registration.Default).Count);
		var cold = Registration.FramesFor(Registration.OfficialColdStart);
		Assert.Equal(12, cold.Count);
		Assert.Equal(GaiaVendor.Qualcomm, cold[2].Vendor);
		Assert.Equal(GaiaVendor.Qualcomm, Registration.Frames([3], GaiaVendor.Qualcomm)[0].Vendor);
	}

	[Fact]
	public void BatteryPushDecodes()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x0683, Hex.Parse("46"));
		var result = Assert.Single(NotificationMap.Decode(frame));
		var n = Assert.IsType<SettingNotification>(result);
		Assert.Equal(Keys.BatteryPercent.Name, n.Name);
		Assert.Equal(70, n.Value);
	}

	[Fact]
	public void SharedAncTablePushYieldsOnePerKey()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x1A81, Hex.Parse("01 01 03 01 02 00"));
		var all = NotificationMap.Decode(frame).Cast<SettingNotification>().ToDictionary(n => n.Name, n => n.Value);
		Assert.Equal(3, all.Count);
		Assert.Equal((byte)1, all[Keys.AncAntiWind.Name]);
		Assert.True((bool)all[Keys.AncAdaptive.Name]);
		Assert.False((bool)all[Keys.AncComfort.Name]);
	}

	[Fact]
	public void UnknownPushIsKept()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x7E85, Hex.Parse("01"));
		var unknown = Assert.IsType<UnknownNotification>(Assert.Single(NotificationMap.Decode(frame)));
		Assert.Equal((ushort)0x7E85, unknown.Command);
	}

	[Fact]
	public void UndecodablePayloadFallsBackToUnknown()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x0683, Hex.Parse("FF"));
		Assert.IsType<UnknownNotification>(Assert.Single(NotificationMap.Decode(frame)));
	}

	[Fact]
	public void ResponsesAreNotNotifications()
	{
		var frame = new GaiaFrame(GaiaVendor.Sennheiser, 0x0703, Hex.Parse("46"));
		Assert.IsType<UnknownNotification>(Assert.Single(NotificationMap.Decode(frame)));
	}
}
