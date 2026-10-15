using Momentum5.Core.Settings;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Protocol;

public sealed record CatalogMatch(IReadOnlyList<ISettingBinding> Bindings, RawCommand? Raw);

/// <summary>Single source of truth for known commands: typed bindings plus raw rows.</summary>
public static class GaiaCatalog
{
	private static readonly Lazy<State> Data = new(Build);

	public static BindingRegistry Registry => Data.Value.Registry;

	public static IReadOnlyList<ISettingBinding> Bindings => Data.Value.Registry.All;

	public static IReadOnlyList<RawCommand> Raw => RawCommands.All;

	public static int EntryCount => Bindings.Count + Raw.Count;

	public static IEnumerable<ISettingBinding> ByGroup(SettingGroup group) => Bindings.Where(b => b.Group == group);

	/// <summary>Finds bindings and/or the raw row for a command word (type bits ignored).</summary>
	public static CatalogMatch? Lookup(GaiaVendor vendor, ushort command)
	{
		var bindings = Registry.ByCommand(vendor, command);
		var raw = Data.Value.RawByKey.GetValueOrDefault((vendor, GaiaWord.AsCommand(command)));
		return bindings.Count == 0 && raw is null ? null : new CatalogMatch(bindings, raw);
	}

	/// <summary>Safety class for sending <paramref name="command"/>: SET word → binding safety, GET word → Read, raw → its class.</summary>
	public static SafetyClass SafetyOf(GaiaVendor vendor, ushort command)
	{
		var match = Lookup(vendor, command);
		if (match is null)
		{
			return SafetyClass.Unknown;
		}

		var cmd = GaiaWord.AsCommand(command);
		var worst = (SafetyClass?)null;
		foreach (var b in match.Bindings)
		{
			SafetyClass? s = null;
			if (b.SetCmd is { } set && GaiaWord.AsCommand(set) == cmd)
			{
				s = b.Safety;
			}
			else if (b.GetCmd is { } get && GaiaWord.AsCommand(get) == cmd)
			{
				s = SafetyClass.Read;
			}

			if (s is { } value && (worst is null || value > worst))
			{
				worst = value;
			}
		}

		if (match.Raw is { } raw && (worst is null || raw.Safety > worst))
		{
			worst = raw.Safety;
		}

		return worst ?? SafetyClass.Read;
	}

	private static State Build()
	{
		var registry = new BindingRegistry();
		foreach (var list in new[] { IdentityBindings.All, AncBindings.All, EqBindings.All, ToggleBindings.All, ConnectionBindings.All })
		{
			foreach (var b in list)
			{
				registry.Register(b);
			}
		}

		var raw = new Dictionary<(GaiaVendor, ushort), RawCommand>();
		foreach (var r in RawCommands.All)
		{
			raw.TryAdd((r.Vendor, r.CmdBase), r);
		}

		return new State(registry, raw);
	}

	private sealed record State(BindingRegistry Registry, Dictionary<(GaiaVendor, ushort), RawCommand> RawByKey);
}
