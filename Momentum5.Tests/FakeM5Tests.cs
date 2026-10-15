using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;
using Momentum5.Simulation;

namespace Momentum5.Tests;

public class FakeM5Tests
{
	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private static GaiaFrame Parse(byte[] bytes)
	{
		var deframer = new GaiaDeframer();
		deframer.Append(bytes);
		Assert.True(deframer.TryRead(out var frame));
		return frame;
	}

	[Fact]
	public async Task Golden_gets_are_answered_byte_identical_once_state_is_primed()
	{
		var frames = FixtureLoader.LoadAll();
		await using var rig = await Rig.StartAsync();
		var matched = 0;

		for (var i = 0; i + 1 < frames.Count; i++)
		{
			FixtureFrame tx = frames[i], rx = frames[i + 1];
			if (!tx.IsTx || rx.IsTx || rx.ReplyTo is null)
			{
				continue;
			}

			var request = Parse(tx.Bytes);
			var response = Parse(rx.Bytes);
			if (GaiaWord.Kind(response.Command) != GaiaMessageKind.Response)
			{
				continue;
			}

			var primed = false;
			foreach (var b in GaiaCatalog.Registry.ByCommand(request.Vendor, request.Command))
			{
				if (b.GetCmd is { } get && GaiaWord.AsCommand(get) == GaiaWord.AsCommand(request.Command)
					&& b.TryParseGet(request.Payload.Span, out var index)
					&& b.TryDecodeResponse(response.Payload.Span, index, out var value) && value is not null)
				{
					rig.Fake.Prime(b.Name, b.Indexed ? index : -1, value);
					primed = true;
				}
			}

			if (!primed)
			{
				continue;
			}

			await rig.Client.WriteAsync(tx.Bytes, Ct);
			var reply = await rig.ReadAsync();
			Assert.Equal(Convert.ToHexString(rx.Bytes), Convert.ToHexString(reply.Encode()));
			matched++;
		}

		Assert.True(matched >= 10, $"only {matched} golden GETs could be replayed");
	}

	[Fact]
	public async Task Get_after_set_returns_the_new_value_and_refreshes_the_gain_table()
	{
		await using var rig = await Rig.StartAsync();

		var ack = await rig.SetAsync(Keys.EqBand(2), 3.5);

		Assert.Equal(GaiaWord.ResponseOf(0x1001), ack.Command);
		Assert.Empty(ack.Payload.ToArray());
		Assert.Equal(3.5, await rig.GetAsync(Keys.EqBand(2)));
		Assert.Equal(3.5, (await rig.GetAsync(Keys.EqAllGains))[2]);
	}

	[Fact]
	public async Task Defaults_match_a_fresh_M5()
	{
		await using var rig = await Rig.StartAsync();

		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
		Assert.Equal(new FirmwareVersion(6, 21, 5), await rig.GetAsync(Keys.Firmware));
		Assert.Equal(70, await rig.GetAsync(Keys.BatteryPercent));
		Assert.True(await rig.GetAsync(Keys.AncEnabled));
		Assert.Equal(SoundMode.GraphicEq, await rig.GetAsync(Keys.SoundMode));
		Assert.Equal(16000, await rig.GetAsync(Keys.EqBandFreq(7)));
	}

	[Fact]
	public async Task Unknown_command_is_answered_with_error_00()
	{
		await using var rig = await Rig.StartAsync();

		var reply = await rig.RequestAsync(0x7E00);

		Assert.Equal(GaiaWord.ErrorOf(0x7E00), reply.Command);
		Assert.Equal("00", Rig.PayloadHex(reply));
	}

	[Fact]
	public async Task Malformed_set_is_answered_with_error_05()
	{
		await using var rig = await Rig.StartAsync();

		var reply = await rig.RequestAsync(0x1800);

		Assert.Equal(GaiaWord.ErrorOf(0x1800), reply.Command);
		Assert.Equal("05", Rig.PayloadHex(reply));
	}

	[Fact]
	public async Task Registered_feature_receives_a_push_after_a_set()
	{
		await using var rig = await Rig.StartAsync();

		var ack = await rig.RequestAsync(Registration.RegisterCommand, "0D");
		Assert.Equal(GaiaWord.ResponseOf(Registration.RegisterCommand), ack.Command);
		Assert.True(rig.Fake.IsRegistered(GaiaVendor.Sennheiser, 0x0D));

		var setAck = await rig.SetAsync(Keys.AncEnabled, false);
		var push = await rig.ReadAsync();

		Assert.Equal(GaiaWord.ResponseOf(0x1A04), setAck.Command);
		Assert.Equal((ushort)0x1A85, push.Command);
		Assert.Equal("00", Rig.PayloadHex(push));
	}

	[Fact]
	public async Task Unregistered_feature_gets_only_the_ack()
	{
		await using var rig = await Rig.StartAsync();

		await rig.SetAsync(Keys.AncEnabled, false);

		Assert.Null(await rig.TryReadAsync(TimeSpan.FromMilliseconds(300)));
	}

	[Fact]
	public async Task Shared_anc_table_push_carries_all_three_pairs()
	{
		await using var rig = await Rig.StartAsync();
		await rig.RequestAsync(Registration.RegisterCommand, "0D");

		await rig.SetAsync(Keys.AncAdaptive, true);
		var push = await rig.ReadAsync();

		Assert.Equal((ushort)0x1A81, push.Command);
		Assert.Equal("010003010200", Rig.PayloadHex(push));
	}

	[Fact]
	public async Task Core_probes_are_answered()
	{
		await using var rig = await Rig.StartAsync();

		var api = await rig.RequestAsync(0x0000, vendor: GaiaVendor.Qualcomm);
		var features = await rig.RequestAsync(0x0001);
		var bad = await rig.RequestAsync(Registration.RegisterCommand);

		Assert.Equal("0300", Rig.PayloadHex(api));
		Assert.StartsWith("00", Rig.PayloadHex(features), StringComparison.Ordinal);
		Assert.Equal(GaiaWord.ErrorOf(Registration.RegisterCommand), bad.Command);
		Assert.Equal("05", Rig.PayloadHex(bad));
	}

	[Fact]
	public async Task Notify_pushes_a_battery_frame_when_power_is_registered()
	{
		await using var rig = await Rig.StartAsync();
		await rig.RequestAsync(Registration.RegisterCommand, "03");

		Assert.True(rig.Fake.Notify(Keys.BatteryPercent, 55));
		var push = await rig.ReadAsync();

		Assert.Equal("FF030001049506" + "8337", Convert.ToHexString(push.Encode()));
		Assert.Equal(55, rig.Fake.GetState(Keys.BatteryPercent));
	}

	[Fact]
	public async Task Notify_without_registration_only_updates_state()
	{
		await using var rig = await Rig.StartAsync();

		Assert.False(rig.Fake.Notify(Keys.BatteryPercent, 41));
		Assert.Equal(41, await rig.GetAsync(Keys.BatteryPercent));
	}

	[Theory]
	[InlineData(LoopbackChunking.SingleByte)]
	[InlineData(LoopbackChunking.Random)]
	public async Task Requests_survive_chunked_links(LoopbackChunking chunking)
	{
		await using var rig = await Rig.StartAsync(loop: new LoopbackOptions { Chunking = chunking, Seed = 3 });

		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
		Assert.Equal(70, await rig.GetAsync(Keys.BatteryPercent));
	}

	[Fact]
	public async Task Datagram_links_work()
	{
		await using var rig = await Rig.StartAsync(loop: new LoopbackOptions { Framing = LinkFraming.Datagram });

		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
	}

	[Fact]
	public async Task Closing_the_link_ends_the_device_loop()
	{
		var rig = await Rig.StartAsync();
		await rig.DisposeAsync();

		var link = rig.Transport.LastLink!;
		await link.Device.Closed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
	}
}
