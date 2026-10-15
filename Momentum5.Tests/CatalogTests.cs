using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Tests;

public class CatalogTests
{
	[Fact]
	public void CommandKeysAreUniqueExceptTheSharedAncTable()
	{
		var seen = new Dictionary<(GaiaVendor, ushort), List<string>>();

		void Add(GaiaVendor vendor, ushort word, string who)
		{
			var key = (vendor, GaiaWord.AsCommand(word));
			if (!seen.TryGetValue(key, out var list))
			{
				seen[key] = list = [];
			}

			list.Add(who);
		}

		foreach (var b in GaiaCatalog.Bindings)
		{
			var bases = new[] { b.GetCmd, b.SetCmd, b.PushCmd }
				.Where(w => w.HasValue)
				.Select(w => GaiaWord.AsCommand(w!.Value))
				.Distinct();
			foreach (var word in bases)
			{
				Add(b.Vendor, word, b.Name);
			}
		}

		foreach (var r in GaiaCatalog.Raw)
		{
			Add(r.Vendor, r.CmdBase, r.Name);
		}

		var duplicates = seen.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).OrderBy(k => k.Item2).ToList();
		Assert.Equal(
			new[] { (GaiaVendor.Sennheiser, (ushort)0x1A00), (GaiaVendor.Sennheiser, (ushort)0x1A01) },
			duplicates);
	}

	[Fact]
	public void SendableWordsHaveCommandTypeBits()
	{
		foreach (var b in GaiaCatalog.Bindings)
		{
			if (b.GetCmd is { } get)
			{
				Assert.True(GaiaWord.IsSendable(get), $"{b.Name} GET {get:X4}");
			}

			if (b.SetCmd is { } set)
			{
				Assert.True(GaiaWord.IsSendable(set), $"{b.Name} SET {set:X4}");
			}

			if (b.PushCmd is { } push)
			{
				Assert.Equal(GaiaMessageKind.Notification, GaiaWord.Kind(push));
			}
		}

		foreach (var r in GaiaCatalog.Raw.Where(r => r.Safety is SafetyClass.Read or SafetyClass.Write or SafetyClass.Gated))
		{
			Assert.True(GaiaWord.IsSendable(r.Command), $"{r.Name} {r.Command:X4}");
		}
	}

	[Fact]
	public void SameNumberDifferentVendorsDoNotCollide()
	{
		var s = GaiaCatalog.Lookup(GaiaVendor.Sennheiser, 0x0600);
		var q = GaiaCatalog.Lookup(GaiaVendor.Qualcomm, 0x0600);
		Assert.NotNull(s);
		Assert.NotNull(q);
		Assert.Contains(s.Bindings, b => b.Name == nameof(Keys.AutoOff));
		Assert.Null(s.Raw);
		Assert.Empty(q.Bindings);
		Assert.Equal("VoiceUiGet", q.Raw?.Name);
	}

	[Fact]
	public void LookupIgnoresTypeBits()
	{
		var plain = GaiaCatalog.Lookup(GaiaVendor.Sennheiser, 0x1206);
		var response = GaiaCatalog.Lookup(GaiaVendor.Sennheiser, 0x1306);
		var error = GaiaCatalog.Lookup(GaiaVendor.Sennheiser, 0x1386);
		Assert.NotNull(plain);
		Assert.Equal(plain.Bindings[0].Name, response?.Bindings[0].Name);
		Assert.Equal(plain.Bindings[0].Name, error?.Bindings[0].Name);
		Assert.Null(GaiaCatalog.Lookup(GaiaVendor.Sennheiser, 0x7E00));
	}

	[Theory]
	[InlineData(GaiaVendor.Sennheiser, 0x0040)]
	[InlineData(GaiaVendor.Sennheiser, 0x0607)]
	[InlineData(GaiaVendor.Sennheiser, 0x0613)]
	[InlineData(GaiaVendor.Sennheiser, 0x0685)]
	[InlineData(GaiaVendor.Sennheiser, 0x0200)]
	[InlineData(GaiaVendor.Sennheiser, 0x0201)]
	[InlineData(GaiaVendor.Sennheiser, 0x1604)]
	[InlineData(GaiaVendor.Qualcomm, 0x0009)]
	[InlineData(GaiaVendor.Qualcomm, 0x000A)]
	[InlineData(GaiaVendor.Qualcomm, 0x000D)]
	[InlineData(GaiaVendor.Qualcomm, 0x0803)]
	[InlineData(GaiaVendor.Qualcomm, 0x0804)]
	[InlineData(GaiaVendor.Qualcomm, 0x0601)]
	public void BlockedCommandsAreBlocked(GaiaVendor vendor, int command) =>
		Assert.Equal(SafetyClass.Blocked, GaiaCatalog.SafetyOf(vendor, (ushort)command));

	[Theory]
	[InlineData(GaiaVendor.Sennheiser, 0x1206, SafetyClass.Read)]
	[InlineData(GaiaVendor.Sennheiser, 0x1A00, SafetyClass.Write)]
	[InlineData(GaiaVendor.Sennheiser, 0x1A01, SafetyClass.Read)]
	[InlineData(GaiaVendor.Sennheiser, 0x0817, SafetyClass.Gated)]
	[InlineData(GaiaVendor.Sennheiser, 0x1402, SafetyClass.Gated)]
	[InlineData(GaiaVendor.Sennheiser, 0x1403, SafetyClass.Dangerous)]
	[InlineData(GaiaVendor.Qualcomm, 0x0C00, SafetyClass.Dangerous)]
	[InlineData(GaiaVendor.Sennheiser, 0x0813, SafetyClass.Unknown)]
	[InlineData(GaiaVendor.Sennheiser, 0x7E00, SafetyClass.Unknown)]
	public void SafetyClassification(GaiaVendor vendor, int command, SafetyClass expected) =>
		Assert.Equal(expected, GaiaCatalog.SafetyOf(vendor, (ushort)command));

	[Fact]
	public void EveryBindingRoundTripsItsSamples()
	{
		foreach (var b in GaiaCatalog.Bindings)
		{
			Assert.NotEmpty(b.SampleValues);
			var index = b.Indexed ? 3 : -1;
			foreach (var sample in b.SampleValues)
			{
				if (b.GetCmd is not null || b.PushCmd is not null)
				{
					Assert.True(b.TryEncodeResponse(sample, index, out var response), $"{b.Name}: encode response");
					Assert.True(b.TryDecodeResponse(response, index, out var back), $"{b.Name}: decode response");
					Assert.True(Support.Same(sample, back), $"{b.Name}: response {sample} != {back}");
				}

				if (b.SetCmd is not null)
				{
					Assert.True(b.TryEncodeSet(sample, index, out var set, out var error), $"{b.Name}: encode set ({error})");
					Assert.True(b.TryDecodeSet(set, out var decodedIndex, out var decoded), $"{b.Name}: decode set");
					Assert.Equal(index, decodedIndex);
					Assert.True(Support.Same(sample, decoded), $"{b.Name}: set {sample} != {decoded}");
				}
			}
		}
	}

	[Fact]
	public void CatalogIsLargeEnoughAndReportsCounts()
	{
		Assert.True(GaiaCatalog.EntryCount >= 60, $"only {GaiaCatalog.EntryCount} entries");
		var counts = GaiaCatalog.Bindings.GroupBy(b => b.Safety).ToDictionary(g => g.Key, g => g.Count());
		foreach (var g in GaiaCatalog.Raw.GroupBy(r => r.Safety))
		{
			counts[g.Key] = counts.GetValueOrDefault(g.Key) + g.Count();
		}

		TestContext.Current.SendDiagnosticMessage(
			$"catalog entries: {GaiaCatalog.EntryCount} (bindings {GaiaCatalog.Bindings.Count}, raw {GaiaCatalog.Raw.Count}); "
			+ string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
	}

	[Fact]
	public void ByGroupFindsMembers()
	{
		Assert.Contains(GaiaCatalog.ByGroup(SettingGroup.Eq), b => b.Name == nameof(Keys.BassBoost));
		Assert.NotEmpty(GaiaCatalog.ByGroup(SettingGroup.Anc));
	}
}
