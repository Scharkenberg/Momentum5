using System.Globalization;
using System.Text.Json;
using Momentum5.Core.Domain;

namespace Momentum5.Client;

/// <summary>
/// Outcome of <see cref="UpdateCheck.CheckAsync"/> (firmware stage 0, X-09). The record is the single return
/// shape — network/parse failures never throw, they land in <see cref="RawNote"/> with
/// <see cref="IsUpdateAvailable"/> false.
/// </summary>
/// <param name="IsUpdateAvailable">True only when a published firmware strictly newer than <paramref name="CurrentFirmware"/> was recognized.</param>
/// <param name="LatestFirmware">The published firmware parsed from the metadata; null when the metadata carried no usable version field.</param>
/// <param name="CurrentFirmware">The device firmware the check compared against — the value read via GAIA 0x1201 (<c>Keys.Firmware</c>).</param>
/// <param name="ChangelogUrl">Release-notes link from the metadata (matched by notesUrl/releaseNotesUrl/changelogUrl/releaseNotes/notes/changelog); null when absent. The <c>/release-notes</c> route itself is NOT synthesized — its response shape is unknown (R-03).</param>
/// <param name="PackageUrl">Download link for the encrypted package (matched by packageUrl/fileUrl/downloadUrl/package/url); null when absent — feeds <see cref="PackagePrep.PrepareAsync"/> (X-10).</param>
/// <param name="PackageBytes">Declared package size in bytes when the metadata carries one (packageBytes/packageSize/fileSize/sizeBytes/size).</param>
/// <param name="CheckedAtUtc">When the check ran (UTC).</param>
/// <param name="RawNote">Diagnostic note: null exactly when an update is available; otherwise one sentence explaining why not (network/timeout/HTTP error, unparseable metadata, or up-to-date).</param>
public sealed record FirmwareUpdateInfo(
	bool IsUpdateAvailable,
	FirmwareVersion? LatestFirmware,
	FirmwareVersion CurrentFirmware,
	Uri? ChangelogUrl,
	Uri? PackageUrl,
	long? PackageBytes,
	DateTimeOffset CheckedAtUtc,
	string? RawNote);

/// <summary>Tuning knobs for <see cref="UpdateCheck"/>. Defaults target the production host from ERRATA R-03.</summary>
public sealed record UpdateCheckOptions
{
	/// <summary>
	/// Consumer-cloud firmware host (ERRATA R-03: <c>https://firmware.s-consumer-cloud.com</c>). Must be absolute;
	/// the check path <c>/product/firmware/gaia/update-info</c> is appended to it. A path prefix survives (reverse proxy).
	/// </summary>
	public Uri BaseAddress { get; init; } = new("https://firmware.s-consumer-cloud.com/");

	/// <summary>Model id the metadata is queried for; "M5AEBT" is the MOMENTUM 5 family model id (R-02/R-03).</summary>
	public string ModelId { get; init; } = "M5AEBT";

	/// <summary>Whole-request timeout (applied as <see cref="HttpClient.Timeout"/>). Must be positive or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>; anything else throws at check time (misuse, not a check failure).</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Optional <see cref="HttpMessageHandler"/> factory — the injection point that makes the check mockable:
	/// every <see cref="UpdateCheck.CheckAsync"/> call builds a short-lived <see cref="HttpClient"/> over a handler
	/// from this factory (tests, snooping proxies). When null, a process-wide pooled <c>SocketsHttpHandler</c> is shared.
	/// </summary>
	public Func<HttpMessageHandler>? HandlerFactory { get; init; }
}

/// <summary>
/// Firmware stage 0 (X-09): read-only version check against the Sennheiser consumer-cloud update metadata.
/// The app API routes live under the firmware host (ERRATA R-03): <c>/product/firmware/gaia/update-info</c>
/// (metadata) and <c>/release-notes</c> (changelog). This class queries the former and answers available/not
/// plus whatever changelog/package links the metadata carries. Strictly read-only: no transfer code —
/// <see cref="PackagePrep"/> (X-10) owns the download and the GAIA upgrade tunnel is untouched.
///
/// The QUERY STRING IS A BEST-EFFORT GUESS: the exact update-info request format was not recoverable
/// statically from the Flutter AOT app (ERRATA R-03 "metadata URL path not found"). This class sends
/// <c>{BaseAddress}/product/firmware/gaia/update-info?model={ModelId}&amp;current={Major.Minor.Patch}</c>
/// and parses the JSON body defensively by common field names; tighten both once an R-04/R-06 capture shows
/// the real request/response pair.
///
/// <see cref="CheckAsync"/> NEVER throws for network or parse failures — the failure is reported through
/// <see cref="FirmwareUpdateInfo.RawNote"/> with <see cref="FirmwareUpdateInfo.IsUpdateAvailable"/> false.
/// Only caller cancellation (<see cref="OperationCanceledException"/> on the token) and options misuse
/// (relative <see cref="UpdateCheckOptions.BaseAddress"/>, invalid <see cref="UpdateCheckOptions.Timeout"/>)
/// propagate as exceptions.
/// </summary>
public sealed class UpdateCheck
{
	private const string UpdateInfoRoute = "/product/firmware/gaia/update-info";
	private const int MaxScanDepth = 4;

	// Metadata field names tried, in priority order — the real schema is unknown until an R-06 capture.
	private static readonly string[] VersionFieldNames =
		["version", "latestVersion", "firmwareVersion", "latestFirmware", "latest", "releaseVersion"];
	private static readonly string[] ChangelogFieldNames =
		["notesUrl", "releaseNotesUrl", "changelogUrl", "releaseNotes", "notes", "changelog"];
	private static readonly string[] PackageFieldNames =
		["packageUrl", "fileUrl", "downloadUrl", "package", "url"];
	private static readonly string[] SizeFieldNames =
		["packageBytes", "packageSize", "fileSize", "sizeBytes", "size"];

	// Shared pooled handler so repeated checks do not exhaust sockets; factory handlers are disposed per call.
	private static readonly SocketsHttpHandler SharedHandler = new()
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(2),
	};

	private readonly UpdateCheckOptions _options;
	private readonly TimeProvider _time;

	/// <summary>Creates the check; <paramref name="time"/> stamps <see cref="FirmwareUpdateInfo.CheckedAtUtc"/> (tests inject a fake).</summary>
	public UpdateCheck(UpdateCheckOptions? options = null, TimeProvider? time = null)
	{
		options ??= new UpdateCheckOptions();
		if (!options.BaseAddress.IsAbsoluteUri)
		{
			throw new ArgumentException("UpdateCheckOptions.BaseAddress must be an absolute URI.", nameof(options));
		}

		_options = options;
		_time = time ?? TimeProvider.System;
	}

	/// <summary>
	/// Checks the cloud metadata against <paramref name="current"/> — the device's firmware read through GAIA
	/// 0x1201 (<c>Keys.Firmware</c>, e.g. 6.21.5). Comparison uses <see cref="FirmwareVersion"/> ordering;
	/// "available" means strictly newer. Never throws for network/parse failures (see the class remarks);
	/// cancellation through <paramref name="ct"/> propagates.
	/// </summary>
	public async Task<FirmwareUpdateInfo> CheckAsync(FirmwareVersion current, CancellationToken ct)
	{
		var checkedAt = _time.GetUtcNow();
		var requestUri = BuildRequestUri(current);
		try
		{
			using var client = new HttpClient(
				_options.HandlerFactory?.Invoke() ?? SharedHandler,
				disposeHandler: _options.HandlerFactory is not null)
			{
				Timeout = _options.Timeout,
			};

			using var response = await client.GetAsync(requestUri, ct).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return Fail(current, checkedAt,
					$"Update metadata unavailable: the server answered HTTP {(int)response.StatusCode} ({response.StatusCode}).");
			}

			var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			using var document = JsonDocument.Parse(body);

			// The schema is a guess: walk string/number properties (a few levels deep) and match common names.
			var fields = new List<(string Name, JsonElement Value)>();
			CollectFields(document.RootElement, fields, 0);
			var latest = ParseLatestVersion(fields, out var sawVersionText);
			var changelog = FirstHttpUrl(fields, ChangelogFieldNames);
			var package = FirstHttpUrl(fields, PackageFieldNames);
			var packageBytes = FirstLong(fields, SizeFieldNames);

			if (latest is null)
			{
				var note = sawVersionText is null
					? "The update metadata contains no recognizable firmware version field (looked for: "
						+ string.Join(", ", VersionFieldNames) + ")."
					: $"The metadata version value '{sawVersionText}' does not parse as a MOMENTUM firmware version (Major.Minor.Patch).";
				return new FirmwareUpdateInfo(false, null, current, changelog, package, packageBytes, checkedAt, note);
			}

			if (latest.Value > current)
			{
				return new FirmwareUpdateInfo(true, latest, current, changelog, package, packageBytes, checkedAt, null);
			}

			return new FirmwareUpdateInfo(false, latest, current, changelog, package, packageBytes, checkedAt,
				$"Firmware {current} is up to date; the latest published version is {latest.Value}.");
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			// HttpClient timeout (the caller token is handled above): TaskCanceledException without a cancelled token.
			return Fail(current, checkedAt, $"The update check timed out after {(int)_options.Timeout.TotalSeconds} s.");
		}
		catch (HttpRequestException ex)
		{
			return Fail(current, checkedAt, $"Network error while checking for firmware updates: {ex.Message}");
		}
		catch (JsonException ex)
		{
			return Fail(current, checkedAt, $"The update metadata is not valid JSON: {ex.Message}");
		}
	}

	private Uri BuildRequestUri(FirmwareVersion current)
	{
		var builder = new UriBuilder(_options.BaseAddress)
		{
			Path = _options.BaseAddress.AbsolutePath.TrimEnd('/') + UpdateInfoRoute,
			Query = "model=" + Uri.EscapeDataString(_options.ModelId)
				+ "&current=" + Uri.EscapeDataString(current.ToString()),
		};
		return builder.Uri;
	}

	private static FirmwareUpdateInfo Fail(FirmwareVersion current, DateTimeOffset checkedAt, string note) =>
		new(false, null, current, null, null, null, checkedAt, note);

	// First version-ish value that parses as Major.Minor.Patch (candidate order = priority); sawVersionText
	// remembers the first version-ish text for the failure note.
	private static FirmwareVersion? ParseLatestVersion(List<(string Name, JsonElement Value)> fields, out string? sawVersionText)
	{
		sawVersionText = null;
		foreach (var candidate in VersionFieldNames)
		{
			foreach (var field in fields)
			{
				if (!string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase)
					|| field.Value.ValueKind != JsonValueKind.String
					|| field.Value.GetString() is not { } text)
				{
					continue;
				}

				var trimmed = text.Trim();
				sawVersionText ??= trimmed;
				if (FirmwareVersion.TryParse(trimmed, out var parsed))
				{
					return parsed;
				}
			}
		}

		return null;
	}

	private static Uri? FirstHttpUrl(List<(string Name, JsonElement Value)> fields, string[] candidates)
	{
		foreach (var candidate in candidates)
		{
			foreach (var field in fields)
			{
				if (!string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase)
					|| field.Value.ValueKind != JsonValueKind.String
					|| field.Value.GetString() is not { } text)
				{
					continue;
				}

				if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
					&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
				{
					return uri;
				}
			}
		}

		return null;
	}

	private static long? FirstLong(List<(string Name, JsonElement Value)> fields, string[] candidates)
	{
		foreach (var candidate in candidates)
		{
			foreach (var field in fields)
			{
				if (!string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (field.Value.ValueKind == JsonValueKind.Number
					&& field.Value.TryGetInt64(out var numeric)
					&& numeric >= 0)
				{
					return numeric;
				}

				if (field.Value.ValueKind == JsonValueKind.String
					&& field.Value.GetString() is { } textual
					&& long.TryParse(textual, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
					&& parsed >= 0)
				{
					return parsed;
				}
			}
		}

		return null;
	}

	// Flattens the document into (name, string|number) pairs a few levels deep, so both flat and
	// {"data": {...}}-style metadata are covered without committing to a schema.
	private static void CollectFields(JsonElement element, List<(string Name, JsonElement Value)> fields, int depth)
	{
		if (depth > MaxScanDepth)
		{
			return;
		}

		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject())
				{
					if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
					{
						fields.Add((property.Name, property.Value));
					}
					else
					{
						CollectFields(property.Value, fields, depth + 1);
					}
				}

				break;

			case JsonValueKind.Array:
				foreach (var item in element.EnumerateArray())
				{
					CollectFields(item, fields, depth + 1);
				}

				break;
		}
	}
}
