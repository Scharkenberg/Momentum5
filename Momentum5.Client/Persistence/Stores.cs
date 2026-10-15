using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Momentum5.Client;

public static class ClientPaths
{
	public static string AppData { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momentum5");
}

/// <summary>Serialized load/save with atomic replace; a corrupt or unreadable file yields defaults and a warning.</summary>
internal sealed class JsonFileStore<T>(string filePath, JsonTypeInfo<T> typeInfo, Func<T> defaults, Action<string>? warn)
	where T : class
{
	private readonly Lock _gate = new();

	public string FilePath => filePath;

	public T Load()
	{
		lock (_gate)
		{
			if (!File.Exists(filePath))
			{
				return defaults();
			}

			try
			{
				return JsonSerializer.Deserialize(File.ReadAllText(filePath), typeInfo) ?? defaults();
			}
			catch (JsonException ex)
			{
				warn?.Invoke($"{filePath} is corrupt ({ex.Message}); using defaults.");
			}
			catch (IOException ex)
			{
				warn?.Invoke($"{filePath} could not be read ({ex.Message}); using defaults.");
			}
			catch (UnauthorizedAccessException ex)
			{
				warn?.Invoke($"{filePath} could not be read ({ex.Message}); using defaults.");
			}

			return defaults();
		}
	}

	public void Save(T value)
	{
		ArgumentNullException.ThrowIfNull(value);
		lock (_gate)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
			var temp = filePath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(value, typeInfo));
			if (File.Exists(filePath))
			{
				File.Replace(temp, filePath, null);
			}
			else
			{
				File.Move(temp, filePath);
			}
		}
	}
}

/// <summary><c>%LOCALAPPDATA%\Momentum5\settings.json</c> (path injectable).</summary>
public sealed class AppSettingsStore(string? path = null, Action<string>? warn = null)
{
	private readonly JsonFileStore<AppSettings> _store = new(
		path ?? Path.Combine(ClientPaths.AppData, "settings.json"),
		ClientJsonContext.Default.AppSettings,
		static () => new AppSettings(),
		warn);

	public string FilePath => _store.FilePath;

	public AppSettings Load() => _store.Load();

	public void Save(AppSettings settings) => _store.Save(settings);

	public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.Run(_store.Load, ct);

	public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.Run(() => _store.Save(settings), ct);
}

/// <summary><c>%LOCALAPPDATA%\Momentum5\presets.json</c> (path injectable).</summary>
public sealed class PresetStore(string? path = null, Action<string>? warn = null)
{
	private readonly JsonFileStore<PresetFile> _store = new(
		path ?? Path.Combine(ClientPaths.AppData, "presets.json"),
		ClientJsonContext.Default.PresetFile,
		static () => new PresetFile(AppSettings.CurrentSchemaVersion, []),
		warn);

	public string FilePath => _store.FilePath;

	public IReadOnlyList<EqPreset> Load() => _store.Load().Presets;

	public void Save(IEnumerable<EqPreset> presets)
	{
		ArgumentNullException.ThrowIfNull(presets);
		_store.Save(new PresetFile(AppSettings.CurrentSchemaVersion, [.. presets]));
	}

	public Task<IReadOnlyList<EqPreset>> LoadAsync(CancellationToken ct = default) => Task.Run(Load, ct);

	public Task SaveAsync(IEnumerable<EqPreset> presets, CancellationToken ct = default) => Task.Run(() => Save(presets), ct);
}
