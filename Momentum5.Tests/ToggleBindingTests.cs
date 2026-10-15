using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Tests;

public class ToggleBindingTests
{
	[Fact]
	public void CaptureChecks()
	{
		Assert.True(Support.Decode(Keys.ImmersiveAudio, "01"));
		Assert.True(Support.Decode(Keys.BatteryProtection, "01"));
		Assert.Equal(PromptMode.Voice, Support.Decode(Keys.PromptMode, "02"));
	}

	[Fact]
	public void TouchControlsAreInvertedFromTheWire()
	{
		Assert.False(Support.Decode(Keys.TouchControlsEnabled, "01"));
		Assert.True(Support.Decode(Keys.TouchControlsEnabled, "00"));
		Assert.Equal(Hex.Parse("00"), Support.Set(Keys.TouchControlsEnabled, true));
		Assert.Equal(Hex.Parse("01"), Support.Set(Keys.TouchControlsEnabled, false));
		Assert.Equal((ushort?)0x1606, Support.Of(Keys.TouchControlsEnabled).SetCmd);
	}

	[Fact]
	public void PhoneCallTransparencyRange()
	{
		Assert.Equal(Hex.Parse("02"), Support.Set(Keys.PhoneCallTransparency, (byte)2));
		Assert.Equal((byte)4, Support.Decode(Keys.PhoneCallTransparency, "04"));
		Assert.False(Support.Of(Keys.PhoneCallTransparency).Format.TryDecode(Hex.Parse("05"), -1, out _));
		Assert.Equal((ushort?)0x0805, Support.Of(Keys.PhoneCallTransparency).SetCmd);
	}

	[Fact]
	public void PromptVolumeIsSigned()
	{
		Assert.Equal((sbyte)-20, Support.Decode(Keys.PromptVolume, "EC"));
		Assert.Equal(Hex.Parse("F9"), Support.Set(Keys.PromptVolume, (sbyte)-7));
	}

	[Fact]
	public void PushOnlyAndGated()
	{
		var rate = Support.Of(Keys.SampleRate);
		Assert.Null(rate.GetCmd);
		Assert.Equal((ushort?)0x089A, rate.PushCmd);
		Assert.Equal(48000u, Support.Decode(Keys.SampleRate, "00 00 BB 80"));
		Assert.Equal(SafetyClass.Gated, Support.Of(Keys.LowLatency).Safety);
		Assert.Equal((ushort?)0x0898, Support.Of(Keys.LowLatency).PushCmd);
		Assert.Equal((ushort?)0x0880, Support.Of(Keys.Codec).PushCmd);
	}

	[Fact]
	public void ComfortCallAndFriends()
	{
		Assert.Equal((ushort?)0x0814, Support.Of(Keys.ComfortCall).SetCmd);
		Assert.Equal((ushort?)0x0815, Support.Of(Keys.ComfortCall).GetCmd);
		Assert.Equal(Hex.Parse("01"), Support.Set(Keys.HeadTracking, true));
		Assert.Equal(Hex.Parse("010F"), Support.Decode(Keys.ImmersiveQuery, "01 0F"));
		Assert.Equal(BtCompatibility.Compatibility, Support.Decode(Keys.BtCompatibility, "01"));
	}
}
