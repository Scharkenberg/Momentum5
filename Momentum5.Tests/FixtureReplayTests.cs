using Momentum5.Protocol;

namespace Momentum5.Tests;

public class FixtureReplayTests
{
	private static GaiaFrame Single(byte[] bytes)
	{
		var d = new GaiaDeframer { MaxPayload = ushort.MaxValue };
		d.Append(bytes);
		Assert.True(d.TryRead(out var frame), "frame did not deframe");
		Assert.False(d.TryRead(out _), "trailing frame");
		Assert.Equal(0, d.BufferedBytes);
		return frame;
	}

	private static void AssertNoFailures(List<string> failures)
	{
		Assert.True(failures.Count == 0,
			$"{failures.Count} failure(s):\n" + string.Join('\n', failures.Take(20)));
	}

	[Fact]
	public void FixturesAreDiscovered()
	{
		var all = FixtureLoader.LoadAll();
		TestContext.Current.SendDiagnosticMessage($"fixture frames: {all.Count} (golden {all.Count(f => f.Golden)})");
		Assert.True(all.Count(f => f.Golden && f.IsTx) >= 100, "expected Aria hardware fixture TX rows");
		Assert.Contains(all, f => f.Source.StartsWith("2026-", StringComparison.Ordinal));
	}

	[Fact]
	public void GoldenTxReencodesIdentically()
	{
		var failures = new List<string>();
		foreach (var f in FixtureLoader.LoadAll().Where(f => f.IsTx))
		{
			try
			{
				var re = Single(f.Bytes).Encode();
				if (!re.AsSpan().SequenceEqual(f.Bytes))
				{
					failures.Add($"{f.Source}: {Convert.ToHexString(f.Bytes)} != {Convert.ToHexString(re)}");
				}
			}
			catch (Exception e)
			{
				failures.Add($"{f.Source}: {e.Message}");
			}
		}

		AssertNoFailures(failures);
	}

	[Fact]
	public void RxDeframesAndMatchesRequest()
	{
		var failures = new List<string>();
		foreach (var f in FixtureLoader.LoadAll().Where(f => !f.IsTx))
		{
			try
			{
				var frame = Single(f.Bytes);
				var (baseCmd, kind) = frame.Classify();
				if (kind == GaiaMessageKind.Command)
				{
					failures.Add($"{f.Source}: RX classified as Command");
				}
				else if (f.ReplyTo is { } req)
				{
					if (baseCmd != req)
					{
						failures.Add($"{f.Source}: base {baseCmd:X4} != request {req:X4}");
					}
					else if (kind is not (GaiaMessageKind.Response or GaiaMessageKind.Error))
					{
						failures.Add($"{f.Source}: kind {kind}");
					}
					else if (kind == GaiaMessageKind.Response && frame.Command != (req | 0x0100))
					{
						failures.Add($"{f.Source}: cmd {frame.Command:X4}");
					}
				}
			}
			catch (Exception e)
			{
				failures.Add($"{f.Source}: {e.Message}");
			}
		}

		AssertNoFailures(failures);
	}
}
