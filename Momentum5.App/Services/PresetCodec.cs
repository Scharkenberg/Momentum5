using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Momentum5.Client;

namespace Momentum5.App.Services;

/// <summary>Share format for EQ presets: <c>{"schema":1,"presets":[...]}</c>; also accepts a bare array or a single preset.</summary>
public static class PresetCodec
{
	public const int MaxBands = 16;

	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};

	private sealed record Envelope(int Schema, List<EqPreset> Presets);

	public static string Serialize(IEnumerable<EqPreset> presets)
	{
		ArgumentNullException.ThrowIfNull(presets);
		return JsonSerializer.Serialize(new Envelope(1, [.. presets]), Options);
	}

	/// <exception cref="FormatException">Not valid preset JSON.</exception>
	public static IReadOnlyList<EqPreset> Parse(string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			List<EqPreset>? list = root.ValueKind switch
			{
				JsonValueKind.Array => root.Deserialize<List<EqPreset>>(Options),
				JsonValueKind.Object when root.TryGetProperty("presets", out var arr) => arr.Deserialize<List<EqPreset>>(Options),
				JsonValueKind.Object => [root.Deserialize<EqPreset>(Options)!],
				_ => null,
			};

			if (list is null || list.Count == 0)
			{
				throw new FormatException("No presets found.");
			}

			foreach (var p in list)
			{
				Validate(p);
			}

			return list;
		}
		catch (JsonException ex)
		{
			throw new FormatException($"Not valid preset JSON: {ex.Message}", ex);
		}
	}

	private static void Validate(EqPreset? p)
	{
		if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60)
		{
			throw new FormatException("A preset needs a name (1-60 characters).");
		}

		if (p.Gains is null || p.Gains.Length is 0 or > MaxBands || p.Gains.Any(g => !double.IsFinite(g)))
		{
			throw new FormatException($"Preset '{p.Name}' has invalid gains.");
		}

		if (!Enum.IsDefined(p.Mode))
		{
			throw new FormatException($"Preset '{p.Name}' has an unknown sound mode.");
		}
	}
}
