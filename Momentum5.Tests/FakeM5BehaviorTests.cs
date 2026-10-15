using Microsoft.Extensions.Time.Testing;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Simulation;

namespace Momentum5.Tests;

public class FakeM5BehaviorTests
{
	private static readonly byte[] ModelRequest = Hex.Parse("ff 03 00 00 04 95 12 06");
	private static readonly byte[] CustomModelReply = Hex.Parse("ff 03 00 02 04 95 13 06 41 00");

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	// ---- quirks ----

	[Fact]
	public async Task Anti_wind_two_is_accepted_but_reads_back_one()
	{
		await using var rig = await Rig.StartAsync();

		var ack = await rig.SetAsync(Keys.AncAntiWind, (byte)2);

		Assert.Equal(GaiaWord.ResponseOf(0x1A00), ack.Command);
		var table = await rig.RequestAsync(0x1A01);
		Assert.Equal("010103000200", Rig.PayloadHex(table));
		Assert.Equal((byte)1, await rig.GetAsync(Keys.AncAntiWind));
	}

	[Fact]
	public async Task Full_table_write_to_1A00_is_rejected()
	{
		await using var rig = await Rig.StartAsync();

		var reply = await rig.RequestAsync(0x1A00, "010003000200");

		Assert.Equal(GaiaWord.ErrorOf(0x1A00), reply.Command);
		Assert.Equal("05", Rig.PayloadHex(reply));
	}

	[Fact]
	public async Task Band_beyond_the_table_is_error_83()
	{
		await using var rig = await Rig.StartAsync();

		var get = await rig.RequestAsync(0x1002, "08");
		var set = await rig.SetAsync(Keys.EqBand(8), 1.0);

		Assert.Equal(GaiaWord.ErrorOf(0x1002), get.Command);
		Assert.Equal("83", Rig.PayloadHex(get));
		Assert.Equal(GaiaWord.ErrorOf(0x1001), set.Command);
		Assert.Equal("83", Rig.PayloadHex(set));
	}

	[Fact]
	public async Task Transparency_write_moves_the_anc_level()
	{
		await using var rig = await Rig.StartAsync();

		await rig.SetAsync(Keys.TransparencyStatus, true);

		Assert.Equal(100, await rig.GetAsync(Keys.AncLevel));
	}

	[Fact]
	public async Task Coupling_can_be_switched_off()
	{
		await using var rig = await Rig.StartAsync(new FakeM5Options { CoupleLevelToTransparency = false });

		await rig.SetAsync(Keys.TransparencyStatus, true);

		Assert.Equal(0, await rig.GetAsync(Keys.AncLevel));
		Assert.True(await rig.GetAsync(Keys.TransparencyStatus));
	}

	[Fact]
	public async Task Power_cycle_turns_transparency_off_and_drops_registrations()
	{
		await using var rig = await Rig.StartAsync();
		await rig.RequestAsync(Registration.RegisterCommand, "0C");
		await rig.SetAsync(Keys.TransparencyStatus, true);
		await rig.ReadAsync(); // 1885 push

		rig.Fake.PowerCycle();

		Assert.False(await rig.GetAsync(Keys.TransparencyStatus));
		Assert.Equal(0, await rig.GetAsync(Keys.AncLevel));
		Assert.False(rig.Fake.IsRegistered(GaiaVendor.Sennheiser, 0x0C));
	}

	[Fact]
	public async Task Power_cycle_reset_can_be_switched_off()
	{
		await using var rig = await Rig.StartAsync(new FakeM5Options { TransparencyResetsOnPowerCycle = false });
		await rig.SetAsync(Keys.TransparencyStatus, true);

		rig.Fake.PowerCycle();

		Assert.True(await rig.GetAsync(Keys.TransparencyStatus));
	}

	// ---- knobs ----

	[Fact]
	public async Task Refuse_writes_answers_sets_with_error_01_and_keeps_state()
	{
		await using var rig = await Rig.StartAsync(new FakeM5Options { RefuseWrites = true });

		var reply = await rig.SetAsync(Keys.BassBoost, true);

		Assert.Equal(GaiaWord.ErrorOf(0x1008), reply.Command);
		Assert.Equal("01", Rig.PayloadHex(reply));
		Assert.False(await rig.GetAsync(Keys.BassBoost));
	}

	[Fact]
	public async Task Model_and_firmware_can_be_overridden()
	{
		await using var rig = await Rig.StartAsync(new FakeM5Options { Model = "M4AEBT Black", Firmware = new FirmwareVersion(6, 22, 0) });

		Assert.Equal("M4AEBT Black", await rig.GetAsync(Keys.Model));
		Assert.Equal("000600160000", Rig.PayloadHex(await rig.RequestAsync(0x1201)));
		Assert.Equal("0000000000", await rig.GetAsync(Keys.Serial));
	}

	[Fact]
	public async Task Per_command_latency_models_the_slow_battery_read()
	{
		var time = new FakeTimeProvider();
		var options = new FakeM5Options { PerCommandLatency = new Dictionary<ushort, TimeSpan> { [0x0603] = TimeSpan.FromSeconds(9) } };
		await using var rig = await Rig.StartAsync(options, time: time);

		await rig.SendAsync(0x0603);
		var (frame, steps) = await rig.ReadAdvancingAsync(TimeSpan.FromSeconds(1));

		Assert.True(steps >= 9, $"answered after only {steps} s");
		Assert.Equal("46", Rig.PayloadHex(frame));
	}

	[Fact]
	public async Task Per_command_latency_leaves_other_commands_fast()
	{
		var time = new FakeTimeProvider();
		var options = new FakeM5Options { PerCommandLatency = new Dictionary<ushort, TimeSpan> { [0x0603] = TimeSpan.FromSeconds(9) } };
		await using var rig = await Rig.StartAsync(options, time: time);

		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
	}

	[Fact]
	public async Task Global_command_latency_applies_to_every_answer()
	{
		var time = new FakeTimeProvider();
		await using var rig = await Rig.StartAsync(new FakeM5Options { CommandLatency = TimeSpan.FromSeconds(2) }, time: time);

		await rig.SendAsync(0x1206);
		var (_, steps) = await rig.ReadAdvancingAsync(TimeSpan.FromSeconds(1));

		Assert.True(steps >= 2);
	}

	[Fact]
	public async Task Busy_window_drops_commands_until_it_ends()
	{
		var time = new FakeTimeProvider();
		await using var rig = await Rig.StartAsync(new FakeM5Options { BusyWindow = TimeSpan.FromSeconds(5) }, time: time);

		await rig.SendAsync(0x1206);
		Assert.Null(await rig.TryReadAsync(TimeSpan.FromMilliseconds(300)));

		time.Advance(TimeSpan.FromSeconds(6));
		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
	}

	[Fact]
	public async Task Power_cycle_restarts_the_busy_window()
	{
		var time = new FakeTimeProvider();
		await using var rig = await Rig.StartAsync(new FakeM5Options { BusyWindow = TimeSpan.FromSeconds(5) }, time: time);
		time.Advance(TimeSpan.FromSeconds(6));
		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));

		rig.Fake.PowerCycle();
		await rig.SendAsync(0x1206);

		Assert.Null(await rig.TryReadAsync(TimeSpan.FromMilliseconds(300)));
	}

	[Fact]
	public async Task Corrupt_next_response_costs_the_deframer_two_bytes()
	{
		await using var rig = await Rig.StartAsync();
		rig.Fake.CorruptNextResponse();

		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
		Assert.Equal(2, rig.DiscardedBytes);
		Assert.Equal(70, await rig.GetAsync(Keys.BatteryPercent));
		Assert.Equal(2, rig.DiscardedBytes);
	}

	[Fact]
	public async Task Injected_garbage_is_skipped_by_the_deframer()
	{
		await using var rig = await Rig.StartAsync();
		rig.Fake.InjectGarbage();

		Assert.Equal("M5AEBT Black", await rig.GetAsync(Keys.Model));
		Assert.Equal(3, rig.DiscardedBytes);
	}

	[Fact]
	public async Task Replay_returns_recorded_bytes_and_falls_back_for_other_requests()
	{
		var options = new FakeM5Options { Replay = [new ReplayExchange(ModelRequest, CustomModelReply)] };
		await using var rig = await Rig.StartAsync(options);

		await rig.Client.WriteAsync(ModelRequest, Ct);
		var first = await rig.ReadAsync();
		await rig.Client.WriteAsync(ModelRequest, Ct);
		var second = await rig.ReadAsync();

		Assert.Equal(Convert.ToHexString(CustomModelReply), Convert.ToHexString(first.Encode()));
		Assert.Equal(Convert.ToHexString(CustomModelReply), Convert.ToHexString(second.Encode()));
		Assert.Equal(new FirmwareVersion(6, 21, 5), await rig.GetAsync(Keys.Firmware));
	}

	[Fact]
	public void Knobs_default_to_a_healthy_device()
	{
		var o = new FakeM5Options();

		Assert.False(o.RefuseWrites);
		Assert.Equal(TimeSpan.Zero, o.BusyWindow);
		Assert.Equal(TimeSpan.Zero, o.CommandLatency);
		Assert.Empty(o.PerCommandLatency);
		Assert.Empty(o.Replay);
	}
}
