using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>Mutable per-connection facts the gate needs; written by the handshake and the app (DevMode).</summary>
public sealed class SessionContext
{
	private int _trust;
	private int _devMode;

	public TrustTier Trust
	{
		get => (TrustTier)Volatile.Read(ref _trust);
		set => Volatile.Write(ref _trust, (int)value);
	}

	public bool DevMode
	{
		get => Volatile.Read(ref _devMode) != 0;
		set => Volatile.Write(ref _devMode, value ? 1 : 0);
	}

	public SessionInfo? Info { get; set; }
}

/// <summary>A live, handshaken session.</summary>
public sealed class ActiveSession(CommandPipeline pipeline, NotificationHub hub, SessionContext context, SessionInfo info)
{
	public CommandPipeline Pipeline { get; } = pipeline;

	public NotificationHub Hub { get; } = hub;

	public SessionContext Context { get; } = context;

	public SessionInfo Info { get; } = info;
}

public interface IActiveSessionProvider
{
	ActiveSession? Current { get; }
}
