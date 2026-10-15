using Momentum5.Client;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Simulation;
using Momentum5.Transport.Rfcomm;

namespace Momentum5.Cli;

/// <summary>Exit codes: 0 ok · 1 connection/device error · 2 usage or invalid value · 3 refused by safety policy.</summary>
internal sealed class CliException(int exitCode, string message) : Exception(message)
{
	public int ExitCode { get; } = exitCode;
}

internal sealed record CliSettings(bool Json, bool Simulate, string? Address, string Name);

/// <summary>A connected, handshaken headset session for one CLI invocation.</summary>
internal sealed class CliSession : IAsyncDisposable
{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

	private CliSession(ConnectionSupervisor supervisor, HeadsetStateStore store, FakeM5? fake)
	{
		Supervisor = supervisor;
		Store = store;
		Fake = fake;
		Service = new SettingService(supervisor, store);
	}

	public ConnectionSupervisor Supervisor { get; }

	public HeadsetStateStore Store { get; }

	public SettingService Service { get; }

	public FakeM5? Fake { get; }

	public ActiveSession Active => Supervisor.Current ?? throw new CliException(1, "The headset session ended.");

	public static async Task<CliSession> OpenAsync(CliSettings settings, CancellationToken ct)
	{
		var store = new HeadsetStateStore();
		ILinkTransport transport;
		DeviceIdentity target;
		FakeM5? fake = null;
		if (settings.Simulate)
		{
			var loopback = new LoopbackTransport();
			fake = new FakeM5();
			fake.Attach(loopback);
			transport = loopback;
			target = new DeviceIdentity(0xA1B2C3D4E5F6, "M5 Simulator", null, true, true);
		}
		else
		{
			transport = new RfcommGaiaTransport();
			target = await DeviceFinder.FindAsync(settings.Address, settings.Name, ct).ConfigureAwait(false)
				?? throw new CliException(1, "No matching paired device. Use --address (see 'm5ctl list').");
		}

		var supervisor = new ConnectionSupervisor([transport], store, new SupervisorOptions());
		var outcome = new TaskCompletionSource<SessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
		supervisor.StateChanged += (_, state) =>
		{
			if (state.Phase is SessionPhase.Ready or SessionPhase.ReadOnly or SessionPhase.Unsupported or SessionPhase.Backoff or SessionPhase.RadioOff)
			{
				outcome.TrySetResult(state);
			}
		};
		supervisor.SetTarget(target);
		supervisor.Start();

		var session = new CliSession(supervisor, store, fake);
		try
		{
			var state = await outcome.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
			if (state.Phase is SessionPhase.Ready or SessionPhase.ReadOnly)
			{
				return session;
			}

			throw new CliException(1, state.Phase == SessionPhase.Unsupported
				? state.Detail ?? "Unsupported device."
				: $"Could not connect: {state.Detail ?? state.Phase.ToString()}");
		}
		catch (TimeoutException)
		{
			await session.DisposeAsync().ConfigureAwait(false);
			throw new CliException(1, $"Connect timeout ({ConnectTimeout.TotalSeconds:0} s).");
		}
		catch
		{
			await session.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public ValueTask DisposeAsync() => Supervisor.DisposeAsync();
}

internal static class DeviceFinder
{
	public static async Task<DeviceIdentity?> FindAsync(string? address, string name, CancellationToken ct)
	{
		using var presence = new ClassicPresence();
		var all = await presence.GetPairedAsync(ct).ConfigureAwait(false);
		if (address is not null)
		{
			if (!Output.TryParseAddress(address, out var wanted))
			{
				throw new CliException(2, $"Invalid --address '{address}'.");
			}

			return all.FirstOrDefault(d => d.Address == wanted);
		}

		var byName = all.Where(d => d.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
			.OrderByDescending(d => d.IsConnected).FirstOrDefault();
		if (byName is not null)
		{
			return byName;
		}

		var connected = all.Where(d => d.IsConnected).ToList();
		return connected.Count == 1 ? connected[0] : null;
	}
}
