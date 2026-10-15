using Momentum5.Core.Domain;
using Momentum5.Core.Settings;

namespace Momentum5.Tests;

// Synthetic vectors only: no M5 capture of this family.
[Trait("verify", "hw")]
public class ConnectionBindingTests
{
	[Fact]
	public void PairedInfoDecodes()
	{
		var info = Support.Decode(Keys.PairedInfo(1), "01 02 01 50 69 78 65 6C 00");
		Assert.Equal(new PairedDevice(1, 2, true, "Pixel"), info);
	}

	[Fact]
	public void PairedInfoRejectsWrongIndex() =>
		Assert.False(Support.Of(Keys.PairedInfo(2)).Format.TryDecode(Hex.Parse("01 02 01 50 00"), 2, out _));

	[Fact]
	public void SelectorsCarryTheIndex()
	{
		Assert.Equal(Hex.Parse("01"), Support.Of(Keys.PairedInfo(1)).BuildGetPayload(1));
		Assert.Equal(Hex.Parse("03"), Support.Of(Keys.PairedStatus(3)).BuildGetPayload(3));
		Assert.Equal(Array.Empty<byte>(), Support.Of(Keys.PairedCount).BuildGetPayload(-1));
		Assert.Equal(TimeSpan.FromSeconds(9), Support.Of(Keys.PairedCount).Timeout);
	}

	[Fact]
	public void ConnectPairedIsAGatedAction()
	{
		var b = Support.Of(Keys.ConnectPaired(2));
		Assert.Null(b.GetCmd);
		Assert.Equal((ushort?)0x1402, b.SetCmd);
		Assert.Equal(SafetyClass.Gated, b.Safety);
		Assert.Equal(Hex.Parse("02"), Support.Set(Keys.ConnectPaired(2), true));
	}
}
