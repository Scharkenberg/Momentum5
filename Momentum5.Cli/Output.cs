using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Momentum5.Core.Domain;

namespace Momentum5.Cli;

internal static class Output
{
	public static string FormatAddress(ulong address) =>
		string.Join(':', address.ToString("X12", CultureInfo.InvariantCulture).Chunk(2).Select(c => new string(c)));

	public static bool TryParseAddress(string text, out ulong address)
	{
		var hex = text.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
		address = 0;
		return hex.Length is > 0 and <= 12 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
	}

	/// <summary>Human-readable value text (bool → on/off, enums by name, lists comma-separated).</summary>
	public static string Text(object? value) => value switch
	{
		null => "-",
		bool b => b ? "on" : "off",
		string s => s,
		Enum e => e.ToString(),
		byte[] bytes => Convert.ToHexString(bytes),
		double d => d.ToString("0.0#", CultureInfo.InvariantCulture),
		FirmwareVersion v => $"{v.Major}.{v.Minor}.{v.Patch}",
		EqConfig c => $"{c.Bands} bands, {c.MinDb:0.0}..{c.MaxDb:0.0} dB".Replace(',', ';'),
		IEnumerable items => string.Join(',', items.Cast<object?>().Select(Text)),
		IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
		_ => value.ToString() ?? string.Empty,
	};

	public static void WriteJson(Utf8JsonWriter w, object? value)
	{
		switch (value)
		{
			case null:
				w.WriteNullValue();
				break;
			case bool b:
				w.WriteBooleanValue(b);
				break;
			case string s:
				w.WriteStringValue(s);
				break;
			case Enum e:
				w.WriteStringValue(e.ToString());
				break;
			case byte[] bytes:
				w.WriteStringValue(Convert.ToHexString(bytes));
				break;
			case double d:
				w.WriteNumberValue(d);
				break;
			case byte or sbyte or short or ushort or int or uint or long or ulong:
				w.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
				break;
			case FirmwareVersion v:
				w.WriteStringValue($"{v.Major}.{v.Minor}.{v.Patch}");
				break;
			case EqConfig c:
				w.WriteStartObject();
				w.WriteNumber("bands", c.Bands);
				w.WriteNumber("minDb", c.MinDb);
				w.WriteNumber("maxDb", c.MaxDb);
				w.WriteEndObject();
				break;
			case IEnumerable items:
				w.WriteStartArray();
				foreach (var item in items)
				{
					WriteJson(w, item);
				}

				w.WriteEndArray();
				break;
			default:
				w.WriteStringValue(value.ToString());
				break;
		}
	}

	public static string Json(Action<Utf8JsonWriter> write, bool indented = false)
	{
		using var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
		{
			write(w);
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	public static string Key(string name, int index) => index < 0 ? name : $"{name}[{index}]";
}
