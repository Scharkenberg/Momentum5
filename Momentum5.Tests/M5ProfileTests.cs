using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Tests;

public class M5ProfileTests
{
	[Theory]
	[InlineData(6, 21, 5, TrustTier.Verified)]
	[InlineData(6, 21, 6, TrustTier.Compatible)]
	[InlineData(6, 21, 40, TrustTier.Compatible)]
	[InlineData(6, 22, 0, TrustTier.Unknown)]
	[InlineData(6, 21, 4, TrustTier.Unknown)]
	[InlineData(7, 21, 5, TrustTier.Unknown)]
	public void FirmwareTiers(int major, int minor, int patch, TrustTier expected)
	{
		var d = M5Profile.Evaluate("M5AEBT Black", new FirmwareVersion(major, minor, patch));
		Assert.True(d.Supported);
		Assert.Equal(expected, d.Trust);
	}

	[Theory]
	[InlineData("M5AEBT Black", true)]
	[InlineData("M5AEBT", true)]
	[InlineData("M4AEBT", false)]
	[InlineData("m5aebt", false)]
	[InlineData("", false)]
	public void ModelGate(string model, bool supported) =>
		Assert.Equal(supported, M5Profile.Evaluate(model, new FirmwareVersion(6, 21, 5)).Supported);

	[Fact]
	public void AllowsFollowsSafetyAndTrust()
	{
		var read = Support.Of(Keys.Model);
		var write = Support.Of(Keys.AncLevel);
		var gated = Support.Of(Keys.LowLatency);
		Assert.True(M5Profile.Allows(read, TrustTier.Unknown));
		Assert.False(M5Profile.Allows(write, TrustTier.Unknown));
		Assert.True(M5Profile.Allows(write, TrustTier.Unknown, devMode: true));
		Assert.True(M5Profile.Allows(write, TrustTier.Compatible));
		Assert.False(M5Profile.Allows(gated, TrustTier.Compatible));
		Assert.True(M5Profile.Allows(gated, TrustTier.Compatible, devMode: true));
		Assert.True(M5Profile.Allows(gated, TrustTier.Verified));
	}
}
