using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Momentum5.Core.Settings;

namespace Momentum5.Client;

/// <summary>Deterministic MAC pseudonymisation: the same address always maps to the same locally administered address.</summary>
public static partial class MacAnonymizer
{
	public static ulong Map(ulong address)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"momentum5-anon:{address:X12}"));
		hash[0] = (byte)((hash[0] | 0x02) & 0xFE);
		ulong v = 0;
		for (var i = 0; i < 6; i++)
		{
			v = (v << 8) | hash[i];
		}

		return v;
	}

	public static string Format(ulong address)
	{
		var hex = address.ToString("X12", CultureInfo.InvariantCulture);
		return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
	}

	/// <summary>Replaces every colon/dash separated MAC in <paramref name="text"/>.</summary>
	public static string Scrub(string text) =>
		MacPattern().Replace(text, m => Format(Map(ulong.Parse(m.Value.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture))));

	[GeneratedRegex("(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}")]
	private static partial Regex MacPattern();
}

/// <summary>Diagnostics zip: last 24 h of frames, snapshot, settings and environment. Serial, name and MACs are masked when anonymising.</summary>
public static class ExportBundle
{
	public static async Task CreateAsync(string zipPath, string logDirectory, HeadsetSnapshot snapshot, AppSettings settings, bool anonymize, TimeProvider? time = null, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(zipPath);
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(settings);
		var now = (time ?? TimeProvider.System).GetUtcNow();
		var cutoff = now.AddHours(-24);
		await using var file = File.Create(zipPath);
		using var zip = new ZipArchive(file, ZipArchiveMode.Create);

		var frames = new StringBuilder();
		if (Directory.Exists(logDirectory))
		{
			foreach (var path in Directory.EnumerateFiles(logDirectory, "frames-*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
			{
				if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
				{
					continue;
				}

				foreach (var line in await ReadLinesAsync(path, ct).ConfigureAwait(false))
				{
					if (IsRecent(line, cutoff))
					{
						frames.AppendLine(anonymize ? MacAnonymizer.Scrub(line) : line);
					}
				}
			}
		}

		Add(zip, "frames.jsonl", frames.ToString());
		Add(zip, "snapshot.json", SnapshotJson(snapshot, anonymize));
		Add(zip, "settings.json", SettingsJson(settings, anonymize));
		Add(zip, "env.txt", $"os: {RuntimeInformation.OSDescription}\nruntime: {RuntimeInformation.FrameworkDescription}\narch: {RuntimeInformation.ProcessArchitecture}\nutc: {now:O}\nanonymized: {anonymize}\n");
	}

	public static string SettingsJson(AppSettings settings, bool anonymize)
	{
		var s = anonymize && settings.LastDeviceAddress is { } a ? settings with { LastDeviceAddress = MacAnonymizer.Map(a) } : settings;
		return JsonSerializer.Serialize(s, ClientJsonContext.Default.AppSettings);
	}

	public static string SnapshotJson(HeadsetSnapshot snapshot, bool anonymize)
	{
		using var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			w.WriteStartObject();
			if (snapshot.Session is { } session)
			{
				w.WriteString("model", session.Model);
				w.WriteString("firmware", session.Firmware);
				w.WriteString("serial", anonymize ? "masked" : session.Serial);
				w.WriteString("trust", session.Trust.ToString());
				w.WriteString("transport", anonymize ? MacAnonymizer.Scrub(session.TransportId) : session.TransportId);
			}

			w.WriteStartObject("values");
			foreach (var ((name, index), value) in snapshot.Entries.OrderBy(e => e.Key.Name, StringComparer.Ordinal).ThenBy(e => e.Key.Index))
			{
				var text = value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
				if (anonymize && name is "LocalName" or "Serial")
				{
					text = "masked";
				}

				w.WriteString(index == 0 ? name : $"{name}[{index}]", anonymize ? MacAnonymizer.Scrub(text) : text);
			}

			w.WriteEndObject();
			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static async Task<List<string>> ReadLinesAsync(string path, CancellationToken ct)
	{
		await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(fs);
		var lines = new List<string>();
		while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
		{
			lines.Add(line);
		}

		return lines;
	}

	private static bool IsRecent(string line, DateTimeOffset cutoff)
	{
		try
		{
			using var doc = JsonDocument.Parse(line);
			return !doc.RootElement.TryGetProperty("ts", out var ts)
				|| !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
				|| t >= cutoff;
		}
		catch (JsonException)
		{
			return true;
		}
	}

	private static void Add(ZipArchive zip, string name, string content)
	{
		var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
		using var s = entry.Open();
		s.Write(Encoding.UTF8.GetBytes(content));
	}
}
