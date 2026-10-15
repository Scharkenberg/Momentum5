using Momentum5.Protocol.Bindings;

namespace Momentum5.Protocol;

public abstract record Notification;

public sealed record SettingNotification(string Name, int Index, object Value, ISettingBinding Binding) : Notification;

/// <summary>A push the catalog does not know (or could not decode). Never dropped: surface it to logs and DevTools.</summary>
public sealed record UnknownNotification(GaiaVendor Vendor, ushort Command, ReadOnlyMemory<byte> Payload) : Notification;

public static class NotificationMap
{
	/// <summary>Decodes a push frame. A shared push (ANC table) yields one notification per key. Always returns at least one item.</summary>
	public static IReadOnlyList<Notification> Decode(GaiaFrame frame)
	{
		var results = new List<Notification>();
		foreach (var binding in GaiaCatalog.Registry.ByCommand(frame.Vendor, frame.Command))
		{
			if (binding.PushCmd != frame.Command)
			{
				continue;
			}

			// By convention an indexed push starts with its index byte (PairedStatus).
			var index = binding.Indexed && frame.Payload.Length > 0 ? frame.Payload.Span[0] : -1;
			if (binding.TryDecodeResponse(frame.Payload.Span, index, out var value) && value is not null)
			{
				results.Add(new SettingNotification(binding.Name, index, value, binding));
			}
		}

		if (results.Count == 0)
		{
			results.Add(new UnknownNotification(frame.Vendor, frame.Command, frame.Payload));
		}

		return results;
	}
}
