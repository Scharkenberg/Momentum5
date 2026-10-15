#pragma warning disable CA5394 // seeded Random is intentional
using Momentum5.Core.Domain;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Tests;

public class CodecPrimitiveTests
{
	private static readonly Random Rng = new(20261015);

	private static void RoundTrip<T>(ICodec<T> codec, T value)
	{
		Assert.True(codec.TryDecode(codec.Encode(value), out var back));
		Assert.True(Support.Same(value, back), $"{value} != {back}");
	}

	[Fact]
	public void BoolRoundTripsAndRejects()
	{
		RoundTrip(Codecs.Bool, true);
		RoundTrip(Codecs.Bool, false);
		Assert.False(Codecs.Bool.TryDecode(default, out _));
		Assert.False(Codecs.Bool.TryDecode(Hex.Parse("0100"), out _));
		Assert.False(Codecs.Bool.TryDecode(Hex.Parse("02"), out _));
		Assert.Equal(Hex.Parse("00"), Codecs.InvertedBool.Encode(true));
		RoundTrip(Codecs.InvertedBool, true);
		RoundTrip(Codecs.InvertedBool, false);
	}

	[Fact]
	public void U8AndRange()
	{
		for (var i = 0; i < 256; i++)
		{
			RoundTrip(Codecs.U8, (byte)i);
		}

		var range = Codecs.U8Range(0, 4);
		Assert.True(range.TryDecode(Hex.Parse("04"), out var four));
		Assert.Equal(4, four);
		Assert.False(range.TryDecode(Hex.Parse("05"), out _));
		Assert.False(Codecs.U8.TryDecode(Hex.Parse("0001"), out _));
	}

	[Fact]
	public void Level()
	{
		for (var i = 0; i <= 100; i++)
		{
			RoundTrip(Codecs.Level0to100, i);
		}

		Assert.False(Codecs.Level0to100.TryDecode(Hex.Parse("65"), out _));
		Assert.Equal(Hex.Parse("64"), Codecs.Level0to100.Encode(150));
		Assert.Equal(Hex.Parse("00"), Codecs.Level0to100.Encode(-5));
	}

	[Fact]
	public void I8()
	{
		for (var i = sbyte.MinValue; i < sbyte.MaxValue; i++)
		{
			RoundTrip(Codecs.I8, i);
		}

		RoundTrip(Codecs.I8, sbyte.MaxValue);
		Assert.False(Codecs.I8.TryDecode(default, out _));
	}

	[Fact]
	public void U16AndU32()
	{
		for (var i = 0; i < 1000; i++)
		{
			RoundTrip(Codecs.U16BE, (ushort)Rng.Next(0, 65536));
			RoundTrip(Codecs.U32BE, (uint)Rng.NextInt64(0, uint.MaxValue + 1L));
		}

		Assert.False(Codecs.U16BE.TryDecode(Hex.Parse("01"), out _));
		Assert.False(Codecs.U32BE.TryDecode(Hex.Parse("010203"), out _));
		Assert.True(Codecs.U32BE.TryDecode(Hex.Parse("0000BB80"), out var rate));
		Assert.Equal(48000u, rate);
	}

	[Theory]
	[InlineData("M5AEBT Black")]
	[InlineData("")]
	[InlineData("Ünïcode ✓")]
	public void CStringRoundTrips(string text) => RoundTrip(Codecs.CString, text);

	[Fact]
	public void CStringEdgeCases()
	{
		Assert.True(Codecs.CString.TryDecode(Hex.Parse("4D3500"), out var a));
		Assert.Equal("M5", a);
		Assert.True(Codecs.CString.TryDecode(Hex.Parse("4D350000"), out var b));
		Assert.Equal("M5", b);
		Assert.True(Codecs.CString.TryDecode(Hex.Parse("4D35"), out var c));
		Assert.Equal("M5", c);
		Assert.False(Codecs.CString.TryDecode(Hex.Parse("FFFE"), out _));
	}

	[Fact]
	public void VersionTriple()
	{
		RoundTrip(Codecs.VersionTriple, new FirmwareVersion(6, 21, 5));
		Assert.False(Codecs.VersionTriple.TryDecode(Hex.Parse("0006001500"), out _));
		Assert.True(Codecs.VersionTriple.TryDecode(Hex.Parse("000600150005"), out var v));
		Assert.Equal(new FirmwareVersion(6, 21, 5), v);
	}

	[Fact]
	public void RawBytes()
	{
		for (var i = 0; i < 50; i++)
		{
			var data = new byte[Rng.Next(0, 40)];
			Rng.NextBytes(data);
			RoundTrip(Codecs.RawBytes, data);
		}
	}

	[Fact]
	public void ByteEnum()
	{
		RoundTrip(Codecs.ByteEnum<PromptMode>(), PromptMode.Voice);
		Assert.False(Codecs.ByteEnum<PromptMode>().TryDecode(Hex.Parse("03"), out _));
		Assert.False(Codecs.ByteEnum<PromptMode>().TryDecode(default, out _));
	}

	[Fact]
	public void FirmwareVersionOrdering()
	{
		var v = new FirmwareVersion(6, 21, 5);
		Assert.True(v.IsAtLeast(new FirmwareVersion(6, 21, 5)));
		Assert.True(v.IsAtLeast(new FirmwareVersion(6, 20, 9)));
		Assert.False(v.IsAtLeast(new FirmwareVersion(6, 21, 6)));
		Assert.True(new FirmwareVersion(6, 22, 0) > v);
		Assert.True(new FirmwareVersion(5, 99, 99) < v);
		Assert.Equal("6.21.5", v.ToString());
		Assert.True(FirmwareVersion.TryParse("6.21.5", out var parsed));
		Assert.Equal(v, parsed);
		Assert.False(FirmwareVersion.TryParse("6.21", out _));
		Assert.False(FirmwareVersion.TryParse("a.b.c", out _));
	}

	[Fact]
	public void RegistryLookupAndDuplicates()
	{
		var registry = new BindingRegistry();
		var b = Support.Of(Core.Settings.Keys.BatteryPercent);
		registry.Register(b);
		Assert.Throws<ArgumentException>(() => registry.Register(b));
		Assert.Same(b, registry.Find(Core.Settings.Keys.BatteryPercent));
		Assert.Single(registry.ByCommand(GaiaVendor.Sennheiser, 0x0603));
		Assert.Single(registry.ByCommand(GaiaVendor.Sennheiser, 0x0683));
		Assert.Empty(registry.ByCommand(GaiaVendor.Qualcomm, 0x0603));
	}
}
