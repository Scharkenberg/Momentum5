using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Momentum5.Protocol;

namespace Momentum5.Client;

internal sealed record TapRecord(string Ts, string Dir, string Transport, string Hex, string Text, int Length);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(TapRecord))]
internal sealed partial class TapJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Frames as JSON lines in <c>frames-yyyyMMdd[-NN].jsonl</c>. Bounded queue (oldest dropped, counted), background writer,
/// flush after every drained batch, rotation at <c>maxFileBytes</c>, newest <c>keepFiles</c> files kept.
/// </summary>
public sealed class JsonlFrameTap : IFrameTap, IAsyncDisposable
{
	public const long DefaultMaxFileBytes = 10L * 1024 * 1024;
	public const int DefaultKeepFiles = 5;
	private const int QueueCapacity = 4096;

	private readonly string _directory;
	private readonly TimeProvider _time;
	private readonly long _maxFileBytes;
	private readonly int _keepFiles;
	private readonly Channel<Entry> _channel;
	private readonly Task _writer;
	private long _dropped;

	public JsonlFrameTap(string? directory = null, TimeProvider? time = null, long maxFileBytes = DefaultMaxFileBytes, int keepFiles = DefaultKeepFiles)
	{
		_directory = directory ?? DefaultDirectory;
		_time = time ?? TimeProvider.System;
		_maxFileBytes = maxFileBytes;
		_keepFiles = Math.Max(1, keepFiles);
		_channel = Channel.CreateBounded<Entry>(
			new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
			_ => Interlocked.Increment(ref _dropped));
		_writer = Task.Run(WriteLoopAsync);
	}

	public static string DefaultDirectory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momentum5", "logs");

	public long DroppedCount => Interlocked.Read(ref _dropped);

	public void Record(FrameDirection direction, string transportId, GaiaFrame frame) =>
		_channel.Writer.TryWrite(new Entry(_time.GetUtcNow(), direction, transportId, frame));

	/// <summary>Drains the queue, flushes and closes the file.</summary>
	public async ValueTask DisposeAsync()
	{
		_channel.Writer.TryComplete();
		await _writer.ConfigureAwait(false);
	}

	private async Task WriteLoopAsync()
	{
		StreamWriter? writer = null;
		var openDay = string.Empty;
		long size = 0;
		var sequence = 0;
		try
		{
			Directory.CreateDirectory(_directory);
			while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (_channel.Reader.TryRead(out var entry))
				{
					var day = TimeZoneInfo.ConvertTime(entry.Time, _time.LocalTimeZone).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
					if (writer is null || day != openDay || size >= _maxFileBytes)
					{
						if (writer is not null)
						{
							await writer.DisposeAsync().ConfigureAwait(false);
							sequence = day == openDay ? sequence + 1 : 0;
						}

						openDay = day;
						(writer, size, sequence) = Open(day, sequence);
						Prune();
					}

					var line = Serialize(entry);
					await writer.WriteLineAsync(line).ConfigureAwait(false);
					size += Encoding.UTF8.GetByteCount(line) + 2;
				}

				if (writer is not null)
				{
					await writer.FlushAsync().ConfigureAwait(false);
				}
			}
		}
		catch (IOException)
		{
			// logging must never take the app down; remaining frames are discarded
		}
		catch (UnauthorizedAccessException)
		{
		}
		finally
		{
			if (writer is not null)
			{
				await writer.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private (StreamWriter Writer, long Size, int Sequence) Open(string day, int sequence)
	{
		while (true)
		{
			var name = sequence == 0 ? $"frames-{day}.jsonl" : $"frames-{day}-{sequence:00}.jsonl";
			var path = Path.Combine(_directory, name);
			var length = File.Exists(path) ? new FileInfo(path).Length : 0;
			if (length >= _maxFileBytes)
			{
				sequence++;
				continue;
			}

			var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
			return (new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" }, length, sequence);
		}
	}

	private void Prune()
	{
		var stale = new DirectoryInfo(_directory)
			.EnumerateFiles("frames-*.jsonl")
			.OrderByDescending(f => f.LastWriteTimeUtc)
			.ThenByDescending(f => f.Name, StringComparer.Ordinal)
			.Skip(_keepFiles);
		foreach (var file in stale)
		{
			try
			{
				file.Delete();
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private static string Serialize(Entry entry)
	{
		var bytes = entry.Frame.Encode();
		var record = new TapRecord(
			entry.Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
			entry.Direction == FrameDirection.Tx ? "tx" : "rx",
			entry.Transport,
			Convert.ToHexString(bytes),
			GaiaFormatter.Describe(entry.Frame, entry.Direction),
			bytes.Length);
		return JsonSerializer.Serialize(record, TapJsonContext.Default.TapRecord);
	}

	private readonly record struct Entry(DateTimeOffset Time, FrameDirection Direction, string Transport, GaiaFrame Frame);
}
