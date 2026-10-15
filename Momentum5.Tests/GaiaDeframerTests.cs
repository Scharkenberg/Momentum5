#pragma warning disable CA5394 // seeded Random is intentional
using Momentum5.Protocol;

namespace Momentum5.Tests;

public class GaiaDeframerTests
{
	private const string ModelRx = "FF 03 00 0D 04 95 13 06 4D 35 41 45 42 54 20 42 6C 61 63 6B 00";
	private const string BatteryRx = "FF 03 00 02 04 95 07 03 01 50";

	private static List<GaiaFrame> Drain(GaiaDeframer d)
	{
		var list = new List<GaiaFrame>();
		while (d.TryRead(out var f))
		{
			list.Add(f);
		}
		return list;
	}

	internal static bool SameFrame(GaiaFrame a, GaiaFrame b) =>
		a.Vendor == b.Vendor && a.Command == b.Command && a.Version == b.Version
		&& a.Payload.Span.SequenceEqual(b.Payload.Span);

	[Fact]
	public void GoldenResponse()
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse(ModelRx));
		Assert.True(d.TryRead(out var f));
		Assert.Equal(GaiaVendor.Sennheiser, f.Vendor);
		Assert.Equal(0x1306, f.Command);
		Assert.Equal("M5AEBT Black\0", System.Text.Encoding.ASCII.GetString(f.Payload.Span));
		Assert.False(d.TryRead(out _));
		Assert.Equal(0, d.BufferedBytes);
	}

	[Fact]
	public void GarbagePrefixRecovers()
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse("12 34 " + ModelRx));
		var frames = Drain(d);
		Assert.Single(frames);
		Assert.Equal(0x1306, frames[0].Command);
		Assert.Equal(2, d.DiscardedBytes);
	}

	[Fact]
	public void TruncatedFrameWaitsThenCompletes()
	{
		var bytes = Hex.Parse(ModelRx);
		var d = new GaiaDeframer();
		d.Append(bytes.AsSpan(0, 5));
		Assert.False(d.TryRead(out _));
		Assert.Equal(5, d.BufferedBytes);
		d.Append(bytes.AsSpan(5, 9));
		Assert.False(d.TryRead(out _));
		d.Append(bytes.AsSpan(14));
		Assert.True(d.TryRead(out var f));
		Assert.Equal(0x1306, f.Command);
	}

	[Fact]
	public void TwoFramesInOneChunk()
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse(ModelRx + " " + BatteryRx));
		var frames = Drain(d);
		Assert.Equal(2, frames.Count);
		Assert.Equal(0x1306, frames[0].Command);
		Assert.Equal(0x0703, frames[1].Command);
		Assert.Equal(Hex.Parse("01 50"), frames[1].Payload.ToArray());
	}

	[Fact]
	public void BadVersionResyncs()
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse("FF 05 00 00 04 95 12 06 " + BatteryRx));
		var frames = Drain(d);
		Assert.Single(frames);
		Assert.Equal(0x0703, frames[0].Command);
		Assert.True(d.Resyncs > 0);
	}

	[Fact]
	public void OversizeLengthResyncs()
	{
		var d = new GaiaDeframer();
		d.Append(Hex.Parse("FF 03 FF FF 04 95 12 06 " + BatteryRx));
		var frames = Drain(d);
		Assert.Single(frames);
		Assert.Equal(0x0703, frames[0].Command);
		Assert.True(d.Resyncs > 0);
	}

	[Fact]
	public void RandomChunkingYieldsIdenticalSequence()
	{
		var rng = new Random(20261015);
		var expected = new List<GaiaFrame>();
		for (var i = 0; i < 40; i++)
		{
			var payload = new byte[rng.Next(0, 40)];
			rng.NextBytes(payload);
			var vendor = rng.Next(2) == 0 ? GaiaVendor.Sennheiser : GaiaVendor.Qualcomm;
			var version = (byte)(rng.Next(2) == 0 ? 3 : 4);
			expected.Add(new GaiaFrame(vendor, (ushort)rng.Next(0, 0x10000), payload, version));
		}

		var stream = expected.SelectMany(f => f.Encode()).ToArray();

		for (var iter = 0; iter < 10_000; iter++)
		{
			var d = new GaiaDeframer();
			var got = new List<GaiaFrame>();
			var pos = 0;
			while (pos < stream.Length)
			{
				var n = Math.Min(rng.Next(1, 48), stream.Length - pos);
				d.Append(stream.AsSpan(pos, n));
				pos += n;
				got.AddRange(Drain(d));
			}

			Assert.Equal(expected.Count, got.Count);
			for (var i = 0; i < expected.Count; i++)
			{
				Assert.True(SameFrame(expected[i], got[i]), $"iter {iter}, frame {i}");
			}
			Assert.Equal(0, d.DiscardedBytes);
		}
	}
}
