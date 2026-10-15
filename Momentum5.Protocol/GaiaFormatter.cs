using System.Collections;
using System.Globalization;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Protocol;

public enum FrameDirection
{
	Tx,
	Rx,
}

/// <summary>Pure, catalog-resolved one-line frame descriptions for logs and DevTools.</summary>
public static class GaiaFormatter
{
	public static string Hex(ReadOnlySpan<byte> bytes) =>
		string.Join(' ', Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)));

	public static string Describe(GaiaFrame frame, FrameDirection direction)
	{
		var (cmd, kind) = frame.Classify();
		var vendor = frame.Vendor switch
		{
			GaiaVendor.Sennheiser => "S",
			GaiaVendor.Qualcomm => "Q",
			_ => ((ushort)frame.Vendor).ToString("X4", CultureInfo.InvariantCulture),
		};
		var head = $"{(direction == FrameDirection.Tx ? "TX" : "RX")} {vendor}:{cmd:X4}";
		return $"{head} {Detail(frame, cmd, kind)}  [{Hex(frame.Encode())}]";
	}

	private static string Detail(GaiaFrame frame, ushort cmd, GaiaMessageKind kind)
	{
		var payload = frame.Payload.Span;
		var match = GaiaCatalog.Lookup(frame.Vendor, cmd);
		var name = match?.Bindings.Count > 0 ? match.Bindings[0].Name : match?.Raw?.Name;

		if (kind == GaiaMessageKind.Error)
		{
			var reason = payload.Length > 0 ? payload[0] : (byte)0;
			return $"{name ?? "?"} ERROR {reason:X2} ({GaiaProtocolException.Describe(reason)})";
		}

		if (match is null)
		{
			return payload.IsEmpty ? "?" : $"? {Hex(payload)}";
		}

		switch (kind)
		{
			case GaiaMessageKind.Notification:
			{
				var data = payload.ToArray();
				var parts = match.Bindings
					.Where(b => b.PushCmd == frame.Command)
					.Select(b => b.TryDecodeResponse(data, b.Indexed && data.Length > 0 ? data[0] : -1, out var v)
						? $"{b.Name}={FormatValue(v)}"
						: $"{b.Name}?")
					.ToList();
				return parts.Count > 0 ? "push " + string.Join(' ', parts) : $"push {name ?? "?"} {Hex(payload)}";
			}

			case GaiaMessageKind.Command:
			{
				var setter = match.Bindings.FirstOrDefault(b => b.SetCmd is { } s && GaiaWord.AsCommand(s) == cmd);
				if (setter is not null)
				{
					return setter.TryDecodeSet(payload, out _, out var v) && !setter.Indexed
						? $"{setter.Name} = {FormatValue(v)}"
						: $"{setter.Name} SET {Hex(payload)}".TrimEnd();
				}

				return payload.IsEmpty ? name ?? "?" : $"{name ?? "?"} {Hex(payload)}";
			}

			default:
			{
				var data = payload.ToArray();
				var getters = match.Bindings.Where(b => b.GetCmd is { } g && GaiaWord.AsCommand(g) == cmd).ToList();
				if (getters.Count == 0)
				{
					return payload.IsEmpty ? $"{name ?? "?"} ACK" : $"{name ?? "?"} {Hex(payload)}";
				}

				if (getters.Exists(b => b.Indexed))
				{
					return $"{name ?? "?"} {Hex(payload)}";
				}

				return string.Join(' ', getters.Select(b =>
					b.TryDecodeResponse(data, -1, out var v) ? $"{b.Name}={FormatValue(v)}" : $"{b.Name}?"));
			}
		}
	}

	private static string FormatValue(object? value) => value switch
	{
		null => "null",
		string s => $"\"{s}\"",
		byte[] bytes => Hex(bytes),
		IEnumerable<double> doubles => "[" + string.Join(',', doubles.Select(d => d.ToString("0.0", CultureInfo.InvariantCulture))) + "]",
		IEnumerable and not string => "[" + string.Join(',', ((IEnumerable)value).Cast<object>()) + "]",
		_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
	};
}
