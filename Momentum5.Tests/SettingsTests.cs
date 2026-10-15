using Momentum5.Core.Settings;

namespace Momentum5.Tests;

public sealed class SettingsTests
{
	private static readonly SettingKey<int> Eq0 = new("eq.gain", 0);
	private static readonly SettingKey<int> Eq1 = new("eq.gain", 1);

	[Fact]
	public void KeyEqualityIncludesIndex()
	{
		Assert.Equal(new SettingKey<int>("eq.gain", 0), Eq0);
		Assert.NotEqual(Eq0, Eq1);
		Assert.Equal(-1, new SettingKey<bool>("x").Index);
	}

	[Fact]
	public void WithReturnsNewInstanceAndLeavesOriginalUntouched()
	{
		var a = HeadsetSnapshot.Empty;
		var b = a.With(Eq0, 5);
		Assert.NotSame(a, b);
		Assert.Equal(0, a.Count);
		Assert.Equal(1, b.Count);
		Assert.Equal(5, b.Get(Eq0));
		Assert.False(a.TryGet(Eq0, out _));
	}

	[Fact]
	public void IndexedKeysAreIndependent()
	{
		var s = HeadsetSnapshot.Empty.With(Eq0, 1).With(Eq1, 2);
		Assert.Equal(1, s.Get(Eq0));
		Assert.Equal(2, s.Get(Eq1));
	}

	[Fact]
	public void SessionSurvivesWith()
	{
		var info = new SessionInfo("M5AEBT Black", "6.21.5", "SN", TrustTier.Verified, "loopback");
		var s = HeadsetSnapshot.Empty.WithSession(info).With(Eq0, 3);
		Assert.Equal(info, s.Session);
		Assert.Null(HeadsetSnapshot.Empty.Session);
	}

	[Fact]
	public void MissingKeyYieldsDefault() => Assert.Equal(0, HeadsetSnapshot.Empty.Get(Eq0));
}
