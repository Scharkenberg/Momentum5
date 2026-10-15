using System.Collections.Concurrent;
using Momentum5.Client;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Simulation;

namespace Momentum5.Tests;

internal sealed class RecordingTap : IFrameTap
{
	public ConcurrentQueue<(FrameDirection Direction, GaiaFrame Frame)> Frames { get; } = new();

	public void Record(FrameDirection direction, string transportId, GaiaFrame frame) => Frames.Enqueue((direction, frame));

	public bool SentCommand(GaiaVendor vendor, ushort command) =>
		Frames.Any(f => f.Direction == FrameDirection.Tx && f.Frame.Vendor == vendor && GaiaWord.AsCommand(f.Frame.Command) == GaiaWord.AsCommand(command));
}

/// <summary>Supervisor + service + FakeM5 over a loopback link, with a 1 ms reconnect ladder.</summary>
internal sealed class ClientRig : IAsyncDisposable
{
	private int _ready;

	private ClientRig(FakeM5 fake, LoopbackTransport transport, HeadsetStateStore store, RecordingTap tap, ConnectionSupervisor supervisor)
	{
		Fake = fake;
		Transport = transport;
		Store = store;
		Tap = tap;
		Supervisor = supervisor;
		Service = new SettingService(supervisor, store);
	}

	public FakeM5 Fake { get; }

	public LoopbackTransport Transport { get; }

	public HeadsetStateStore Store { get; }

	public RecordingTap Tap { get; }

	public ConnectionSupervisor Supervisor { get; }

	public SettingService Service { get; }

	public int ReadyCount => Volatile.Read(ref _ready);

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	/// <summary>Starts the supervisor and waits for Ready, ReadOnly or Unsupported.</summary>
	public static async Task<ClientRig> StartAsync(FakeM5Options? fake = null, LoopbackOptions? loop = null)
	{
		var f = new FakeM5(fake);
		var transport = new LoopbackTransport(loop);
		f.Attach(transport);
		var store = new HeadsetStateStore();
		var tap = new RecordingTap();
		var supervisor = new ConnectionSupervisor(
			[transport],
			store,
			new SupervisorOptions { Tap = tap, Ladder = [TimeSpan.FromMilliseconds(1)], JitterFraction = 0 });
		var rig = new ClientRig(f, transport, store, tap, supervisor);
		supervisor.SessionReady += (_, _) => Interlocked.Increment(ref rig._ready);
		supervisor.SetTarget(new DeviceIdentity(1, "sim", null, true, true));
		supervisor.Start();
		await rig.WaitUntilAsync(() => supervisor.State.Phase is SessionPhase.Ready or SessionPhase.ReadOnly or SessionPhase.Unsupported);
		return rig;
	}

	public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			Assert.True(Environment.TickCount64 < deadline, $"Timed out; supervisor state: {Supervisor.State}");
			await Task.Delay(5, Ct);
		}
	}

	public ValueTask DisposeAsync() => Supervisor.DisposeAsync();
}
