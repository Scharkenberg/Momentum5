using System.Globalization;
using System.Text.Json;
using Momentum5.Protocol;

namespace Momentum5.Tests;

/// <param name="Source">file name (+ index) for diagnostics</param>
/// <param name="IsTx">host to headset</param>
/// <param name="Bytes">full GAIA frame</param>
/// <param name="Golden">true when the bytes were recorded verbatim (not re-encoded from payloads)</param>
/// <param name="ReplyTo">for RX rows of request/response fixtures: the request command word</param>
internal sealed record FixtureFrame(string Source, bool IsTx, byte[] Bytes, bool Golden, ushort? ReplyTo);

internal static class FixtureLoader
{
	public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures");

	public static IReadOnlyList<FixtureFrame> LoadAll()
	{
		var all = new List<FixtureFrame>();
		var aria = Path.Combine(Root, "aria-m5");
		if (Directory.Exists(aria))
		{
			foreach (var f in Directory.EnumerateFiles(aria, "*.json").Order(StringComparer.Ordinal))
			{
				all.AddRange(LoadAria(f));
			}
		}

		var caps = Path.Combine(Root, "captures");
		if (Directory.Exists(caps))
		{
			foreach (var f in Directory.EnumerateFiles(caps, "frames.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
			{
				all.AddRange(LoadCapture(f));
			}
		}

		return all;
	}

	private static ushort ParseCmd(string s)
	{
		var t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
		return ushort.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	}

	private static string? Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

	/// <summary>Aria fixtures: schema A (tx/rx full frames) and schema B (request|payload + response, payload only).</summary>
	private static List<FixtureFrame> LoadAria(string path)
	{
		var name = Path.GetFileName(path);
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var list = new List<FixtureFrame>();
		var i = 0;
		foreach (var ex in doc.RootElement.GetProperty("exchanges").EnumerateArray())
		{
			var src = $"{name}#{i++}";
			if (ex.TryGetProperty("private_fields_omitted", out _))
			{
				continue;
			}

			var cmd = ParseCmd(ex.GetProperty("command").GetString()!);

			var tx = Str(ex, "tx");
			if (tx is not null)
			{
				list.Add(new FixtureFrame(src, true, Hex.Parse(tx), true, null));
				var rx = Str(ex, "rx");
				if (!string.IsNullOrEmpty(rx))
				{
					list.Add(new FixtureFrame(src, false, Hex.Parse(rx), true, cmd));
				}

				continue;
			}

			var req = Str(ex, "request") ?? Str(ex, "payload") ?? string.Empty;
			var resp = Str(ex, "response") ?? string.Empty;
			list.Add(new FixtureFrame(src, true,
				new GaiaFrame(GaiaVendor.Sennheiser, cmd, Hex.Parse(req)).Encode(), false, null));
			list.Add(new FixtureFrame(src, false,
				new GaiaFrame(GaiaVendor.Sennheiser, GaiaWord.ResponseOf(cmd), Hex.Parse(resp)).Encode(), false, cmd));
		}

		return list;
	}

	/// <summary>Own captures: frames.json rows {dir, frame, ...}.</summary>
	private static List<FixtureFrame> LoadCapture(string path)
	{
		var label = Path.GetFileName(Path.GetDirectoryName(path)) ?? "capture";
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var list = new List<FixtureFrame>();
		var i = 0;
		foreach (var row in doc.RootElement.EnumerateArray())
		{
			var src = $"{label}#{i++}";
			list.Add(new FixtureFrame(src, Str(row, "dir") == "tx", Hex.Parse(Str(row, "frame")!), true, null));
		}

		return list;
	}
}
