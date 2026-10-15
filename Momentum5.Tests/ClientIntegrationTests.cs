using Momentum5.Client;
using Momentum5.Core.Domain;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Simulation;

namespace Momentum5.Tests;

public class ClientIntegrationTests
{
	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	[Fact]
	public async Task Handshake_reaches_ready_and_registers_the_cold_start_list()
	{
		await using var rig = await ClientRig.StartAsync();

		var info = rig.Supervisor.Current!.Info;
		Assert.Equal(SessionPhase.Ready, rig.Supervisor.State.Phase);
		Assert.Equal("M5AEBT Black", info.Model);
		Assert.Equal("6.21.5", info.Firmware);
		Assert.Equal(TrustTier.Verified, info.Trust);
		Assert.NotEmpty(info.Features);
		foreach (var entry in Registration.OfficialColdStart)
		{
			Assert.True(rig.Fake.IsRegistered(entry.Vendor, entry.Feature), $"{entry.Vendor}:{entry.Feature:X2} not registered");
		}
	}

	[Fact]
	public async Task Refresh_all_fills_the_snapshot()
	{
		await using var rig = await ClientRig.StartAsync();

		await rig.Service.RefreshAllAsync(Ct);

		var snapshot = rig.Store.Current;
		Assert.Equal(70, snapshot.Get(Keys.BatteryPercent));
		Assert.True(snapshot.Get(Keys.AncEnabled));
		Assert.Equal(8, snapshot.Get(Keys.EqConfig)!.Bands);
		Assert.Equal(90, snapshot.Get(Keys.EqBandFreq(0)));
		Assert.Equal(16000, snapshot.Get(Keys.EqBandFreq(7)));
		Assert.Equal(8, snapshot.Get(Keys.EqAllGains)!.Count);
	}

	[Fact]
	public async Task One_verified_set_per_binding_group()
	{
		await using var rig = await ClientRig.StartAsync();

		Assert.True((await rig.Service.SetAsync(Keys.BatteryProtection, true, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.AncEnabled, false, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.TransparencyLevel, 50, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.EqBand(2), 3.5, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.BassBoost, true, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.PromptMode, PromptMode.Off, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.ImmersiveAudio, true, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.AutoAnswer, true, Ct)).Verified);
		Assert.True((await rig.Service.SetAsync(Keys.SmartPause, true, Ct)).Verified);

		var snapshot = rig.Store.Current;
		Assert.False(snapshot.Get(Keys.AncEnabled));
		Assert.Equal(3.5, snapshot.Get(Keys.EqBand(2)));
		Assert.Equal(PromptMode.Off, snapshot.Get(Keys.PromptMode));
	}

	[Fact]
	public async Task Wind_mode_two_is_reported_unverified_with_the_read_back_value()
	{
		await using var rig = await ClientRig.StartAsync();

		var result = await rig.Service.SetAsync(Keys.AncAntiWind, (byte)2, Ct);

		Assert.False(result.Verified);
		Assert.Equal((byte)1, result.Actual);
		Assert.Equal((byte)1, rig.Store.Current.Get(Keys.AncAntiWind));
	}

	[Fact]
	public async Task Eq_batch_writes_all_bands_and_reads_them_back()
	{
		await using var rig = await ClientRig.StartAsync();

		var result = await rig.Service.SetEqAsync([1, 2, 3, 4, -1, -2, -3, -4], Ct);

		Assert.True(result.Verified);
		Assert.Equal(new double[] { 1, 2, 3, 4, -1, -2, -3, -4 }, result.Actual);
		Assert.Equal(8, rig.Tap.Frames.Count(f => f.Direction == FrameDirection.Tx && f.Frame.Command == 0x1001));
	}

	[Fact]
	public async Task Pushes_flow_into_the_state_store_even_in_a_storm()
	{
		await using var rig = await ClientRig.StartAsync();

		for (var level = 1; level <= 100; level++)
		{
			rig.Fake.Notify(Keys.BatteryPercent, level);
		}

		await rig.WaitUntilAsync(() => rig.Store.Current.Get(Keys.BatteryPercent) == 100);
	}

	[Fact]
	public async Task Unknown_firmware_is_read_only()
	{
		await using var rig = await ClientRig.StartAsync(new FakeM5Options { Firmware = new FirmwareVersion(6, 99, 0) });

		Assert.Equal(SessionPhase.ReadOnly, rig.Supervisor.State.Phase);
		Assert.Equal(70, await rig.Service.GetAsync(Keys.BatteryPercent, Ct));
		await Assert.ThrowsAsync<SafetyDeniedException>(async () => await rig.Service.SetAsync(Keys.BassBoost, true, Ct));
		Assert.False(rig.Tap.SentCommand(GaiaVendor.Sennheiser, 0x1008));
	}

	[Fact]
	public async Task M4_model_is_refused()
	{
		await using var rig = await ClientRig.StartAsync(new FakeM5Options { Model = "M4AEBT Black" });

		Assert.Equal(SessionPhase.Unsupported, rig.Supervisor.State.Phase);
		Assert.Null(rig.Supervisor.Current);
	}

	[Fact]
	public async Task Supervisor_reconnects_ten_times_with_clean_handshakes()
	{
		await using var rig = await ClientRig.StartAsync();

		for (var cycle = 1; cycle <= 10; cycle++)
		{
			var before = rig.ReadyCount;
			rig.Transport.LastLink!.InjectEof();
			await rig.WaitUntilAsync(() => rig.ReadyCount > before);
			Assert.Equal(70, await rig.Service.GetAsync(Keys.BatteryPercent, Ct));
		}

		Assert.Equal(11, rig.ReadyCount);
		Assert.True(rig.Fake.IsRegistered(GaiaVendor.Sennheiser, 0x03));
	}

	[Theory]
	[InlineData(LoopbackChunking.SingleByte)]
	[InlineData(LoopbackChunking.Random)]
	public async Task Chunked_links_work_end_to_end(LoopbackChunking chunking)
	{
		await using var rig = await ClientRig.StartAsync(loop: new LoopbackOptions { Chunking = chunking, Seed = 5 });

		await rig.Service.RefreshAsync(SettingGroup.Eq, Ct);

		Assert.Equal(8, rig.Store.Current.Get(Keys.EqConfig)!.Bands);
	}

	[Fact]
	public async Task Corrupt_and_garbage_bytes_are_survived()
	{
		await using var rig = await ClientRig.StartAsync();

		rig.Fake.InjectGarbage();
		rig.Fake.CorruptNextResponse();

		Assert.Equal("M5AEBT Black", await rig.Service.GetAsync(Keys.Model, Ct));
		Assert.Equal(70, await rig.Service.GetAsync(Keys.BatteryPercent, Ct));
	}

	[Fact]
	public async Task Blocked_and_dangerous_commands_never_reach_the_wire()
	{
		await using var rig = await ClientRig.StartAsync();
		var pipeline = rig.Supervisor.Current!.Pipeline;

		foreach (var safety in new[] { SafetyClass.Blocked, SafetyClass.Dangerous })
		{
			var row = RawCommands.All.First(r => r.Safety == safety);
			await Assert.ThrowsAsync<SafetyDeniedException>(async () =>
				await pipeline.ExecuteAsync(row.Vendor, row.Command, ReadOnlyMemory<byte>.Empty, null, Ct));
			Assert.False(rig.Tap.SentCommand(row.Vendor, row.Command), $"{row.Name} was sent");
		}
	}
}
