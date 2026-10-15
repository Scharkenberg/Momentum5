#pragma warning disable CA5351 // MD5 is mandated by the Qualcomm upgrade protocol / package manifest, not used for security
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Momentum5.Client;

/// <summary>Failure classes of <see cref="PackagePrep"/> — each maps to one recovery hint on the firmware page (X-13).</summary>
public enum FirmwarePackageFailure
{
	/// <summary>The package could not be acquired or staged: DNS/connect/HTTP status, timeout, the size cap, or a cache write failure.</summary>
	Download,

	/// <summary>The AES-CTR pass itself failed (empty package or a cryptography backend error) — practically unreachable with the fixed key.</summary>
	Decrypt,

	/// <summary>The decrypted plaintext is not a ZIP: no PK\x03\x04 magic, a corrupt archive, or a CRC failure while reading an entry. Also raised for the 2-layer app-cache form (see the <see cref="PackagePrep"/> remarks).</summary>
	NotAZip,

	/// <summary>The ZIP contains no image entry, or several with no unique full-* application image.</summary>
	MissingImage,

	/// <summary>No usable MD5 manifest: no *.md5 entry, or its content is not a bare 32-hex digest.</summary>
	MissingMd5,

	/// <summary>The manifest digest does not equal MD5(image) — the R-09 exit-1 reject: the image must never reach the GAIA upgrade tunnel.</summary>
	Md5Mismatch,
}

/// <summary>
/// Typed error for firmware package preparation (house rule: pages surface exceptions).
/// <see cref="Reason"/> selects the recovery text; the message is user-presentable.
/// </summary>
public sealed class FirmwarePackageException(FirmwarePackageFailure reason, string message, Exception? innerException = null)
	: Exception(message, innerException)
{
	/// <summary>Failure class; see <see cref="FirmwarePackageFailure"/>.</summary>
	public FirmwarePackageFailure Reason { get; } = reason;
}

/// <summary>Result of <see cref="PackagePrep.PrepareAsync"/> — the verified image the X-11 upgrade state machine transfers.</summary>
/// <param name="PlainImagePath">Full path of the staged, decrypted image inside the cache directory.</param>
/// <param name="InnerImageName">File name of the inner image, e.g. <c>herakles-saturn-006-021-005-dfu-encrypted-full-en.bin</c> (URL file name minus <c>.zip.enc</c>).</param>
/// <param name="ImageBytes">Length of the staged image in bytes.</param>
/// <param name="Md5Verified">Always true on success — a mismatch throws <see cref="FirmwarePackageException"/> with <see cref="FirmwarePackageFailure.Md5Mismatch"/> instead.</param>
/// <param name="DecryptedFrom">The package URL this image was prepared from (also on cache hits).</param>
/// <param name="PreparedAtUtc">When the image was written (file mtime on cache hits).</param>
public sealed record PreparedPackage(
	string PlainImagePath,
	string InnerImageName,
	long ImageBytes,
	bool Md5Verified,
	string DecryptedFrom,
	DateTimeOffset PreparedAtUtc);

/// <summary>
/// Firmware stage 1 (X-10): download an encrypted firmware package, decrypt it, verify it, and stage it in the cache.
///
/// Container and algorithm are byte-level specified in docs/fw-format.md (R-09): the whole
/// <c>…-dfu-encrypted-&lt;kind&gt;.bin.zip.enc</c> file is AES-256-CTR ciphertext — fixed key, full-width
/// 128-bit big-endian counter starting at 5, no IV/salt/header, any file length. The plaintext is a ZIP holding
/// the DFU image <c>&lt;image&gt;.bin</c> plus a bare 32-hex MD5 manifest <c>&lt;image&gt;.bin.md5</c>. The BCL has
/// no CTR mode, so the keystream is generated with <see cref="Aes"/> in ECB (the raw block cipher) over the
/// counter blocks 5, 6, 7, … — exactly the docs/fw-format.md §6 construction; the §3.1 test vectors were
/// re-verified against an independent AES during this port (counter 5 → 2a61122d…, 32-byte roundtrip
/// 000102…1f ↔ 2a60102e…9724584).
///
/// Pipeline: 1) download to memory under a size cap; 2) AES-CTR decrypt; 3) ZIP validation (PK\x03\x04 magic;
/// structural and CRC checks come from <see cref="ZipArchive"/>); 4) MD5 gate; 5) write image + digest sidecar
/// to the cache directory. Recoverable failures throw <see cref="FirmwarePackageException"/>; nothing is handed
/// downstream unless the MD5 gate passed (MD5 is a transfer-integrity check, not a security control).
///
/// Cache contract (fw-format.md §6): the verified image is kept at <c>{cacheDirectory}/{image}</c> with an
/// <c>{image}.md5</c> sidecar, where <c>{image}</c> is the package URL's file name minus <c>.zip.enc</c>. A later
/// PrepareAsync for a URL with the same file name short-circuits the download when the cached image still hashes
/// to the sidecar digest. Invalidation: a missing or mismatching pair falls through to a fresh download and
/// overwrite (a half-written cache is therefore self-healing); and because the file name embeds the version
/// triple, a new release naturally maps to a new cache key. The raw <c>.enc</c> is intentionally NOT persisted —
/// it is re-downloadable and only the verified image is consumed by X-11.
///
/// The 2-layer app-cache variant (<c>…-full-en.bin.enc</c> + sibling <c>.md5</c>, fw-format.md §2) decrypts to
/// the raw image with no ZIP wrapper; it only exists as Android app-cache files, never as server URLs, so it is
/// not handled here — a non-ZIP plaintext raises <see cref="FirmwarePackageFailure.NotAZip"/>.
///
/// No UI, no DI, no GAIA/transfer code (X-11 owns the upgrade state machine); X-13 wires the page.
/// </summary>
public sealed class PackagePrep
{
	/// <summary>Default defensive download cap (64 MiB) — real packages are a few MiB.</summary>
	public const long DefaultMaxPackageBytes = 64L * 1024 * 1024;

	// AES-256 key as published by the public research repository
	// kero3009destiny/sennheiser-fw-RE (dec.py, commit 103822bd, 2025-11-02 — "Reverse engineering research
	// from the js Smart Control App. Focuses on firmware decryption etc."). The repo carries no explicit
	// LICENSE; the key is embedded for interoperability research with the explicit authorization of the
	// Momentum5 project sponsor (docs/fw-format.md §4, EU 2009/24/EC art. 6 / DMCA §1201(f)). Do not
	// redistribute decrypted firmware images.
	private const string KeyHex = "ed3b78a474c2acabb812407b97065649bf190e3d758e86f17b920aa41d8c271e";

	private const int AesBlockSize = 16;
	private const byte InitialCounter = 5;
	private const int DownloadTimeoutSeconds = 100;

	// ZIP local-file-header magic (fw-format.md §2): 50 4B 03 04.
	private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

	// Whitespace that can surround a bare digest or separate an md5sum-style "digest  name" line.
	private static readonly char[] DigestSeparators = [' ', '\t', '\r', '\n'];

	private static readonly byte[] Key = Convert.FromHexString(KeyHex);

	// Shared pooled handler so repeated downloads do not exhaust sockets; factory handlers are disposed per call.
	private static readonly SocketsHttpHandler SharedHandler = new()
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(2),
	};

	private readonly string _cacheDirectory;
	private readonly Func<HttpMessageHandler>? _handlerFactory;
	private readonly long _maxPackageBytes;
	private readonly TimeProvider _time;

	/// <summary>
	/// Prepares packages for <paramref name="cacheDirectory"/> (created idempotently on first use).
	/// <paramref name="handlerFactory"/> optionally injects the HTTP handler per download (tests/snooping);
	/// <paramref name="maxPackageBytes"/> caps downloads (keep it below <see cref="int.MaxValue"/>);
	/// <paramref name="time"/> stamps <see cref="PreparedPackage.PreparedAtUtc"/>.
	/// </summary>
	public PackagePrep(
		string cacheDirectory,
		Func<HttpMessageHandler>? handlerFactory = null,
		long maxPackageBytes = DefaultMaxPackageBytes,
		TimeProvider? time = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxPackageBytes, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPackageBytes, int.MaxValue);

		_cacheDirectory = cacheDirectory;
		_handlerFactory = handlerFactory;
		_maxPackageBytes = maxPackageBytes;
		_time = time ?? TimeProvider.System;
	}

	/// <summary>
	/// Download → decrypt → unzip → MD5-verify → cache. Entry selection inside the ZIP: any single
	/// <c>*.bin</c> entry is the image; when several exist the <c>full-*</c> application image wins over
	/// voice-prompt packs and any remaining ambiguity is an error. The digest comes from the image's own
	/// <c>*.bin.md5</c> manifest (or the single <c>*.md5</c> entry). On success the image is written to
	/// <c>{cacheDirectory}/{InnerImageName}</c> with an <c>.md5</c> sidecar; a still-valid cached pair for the
	/// same URL file name short-circuits the download. Recoverable failures throw
	/// <see cref="FirmwarePackageException"/>; caller cancellation propagates.
	/// </summary>
	public async Task<PreparedPackage> PrepareAsync(Uri packageUrl, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(packageUrl);
		ct.ThrowIfCancellationRequested();

		var packageName = GetPackageFileName(packageUrl);
		var imageName = DeriveImageName(packageName);
		var imagePath = Path.Combine(_cacheDirectory, imageName);
		var md5Path = imagePath + ".md5";

		Directory.CreateDirectory(_cacheDirectory);

		var cached = await TryLoadCachedAsync(imagePath, md5Path, packageUrl, ct).ConfigureAwait(false);
		if (cached is not null)
		{
			return cached;
		}

		var ciphertext = await DownloadAsync(packageUrl, ct).ConfigureAwait(false);

		byte[] plaintext;
		try
		{
			plaintext = DecryptCtr(ciphertext);
		}
		catch (CryptographicException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Decrypt,
				$"The AES-CTR decrypt pass failed: {ex.Message}", ex);
		}

		if (plaintext.Length == 0)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Decrypt,
				"The downloaded package is empty — nothing to decrypt.", null);
		}

		if (plaintext.Length < ZipMagic.Length || !plaintext.AsSpan(0, ZipMagic.Length).SequenceEqual(ZipMagic))
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.NotAZip,
				$"The decrypted plaintext does not start with the ZIP magic (PK\\x03\\x04): {packageUrl}. "
				+ "Not a 3-layer package — the 2-layer app-cache .bin.enc form is not supported for URL downloads.",
				null);
		}

		byte[] image;
		string manifest;
		try
		{
			// In-memory zip: ZipArchive checks entry structure and CRC32 while the entries are read.
			using var archive = new ZipArchive(new MemoryStream(plaintext, writable: false), ZipArchiveMode.Read);
			var (imageEntry, md5Entry) = FindEntries(archive);
			image = await ReadEntryBytesAsync(imageEntry, ct).ConfigureAwait(false);
			manifest = await ReadEntryTextAsync(md5Entry, ct).ConfigureAwait(false);
		}
		catch (InvalidDataException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.NotAZip,
				$"The decrypted package is a corrupt ZIP: {ex.Message}", ex);
		}
		catch (NotSupportedException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.NotAZip,
				$"The decrypted package uses an unsupported ZIP feature: {ex.Message}", ex);
		}
		catch (IOException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.NotAZip,
				$"Reading the decrypted package failed: {ex.Message}", ex);
		}

		if (!TryNormalizeDigest(manifest, out var expected))
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.MissingMd5,
				$"The MD5 manifest '{manifest.Trim()}' does not contain a bare 32-hex digest.", null);
		}

		if (!VerifyMd5(image, manifest))
		{
			var actual = Convert.ToHexString(MD5.HashData(image)).ToLowerInvariant();
			throw new FirmwarePackageException(FirmwarePackageFailure.Md5Mismatch,
				$"MD5 mismatch: the inner image hashes to {actual} but the manifest says {expected}.", null);
		}

		try
		{
			await File.WriteAllBytesAsync(imagePath, image, ct).ConfigureAwait(false);
			await File.WriteAllTextAsync(md5Path, expected + "\n", ct).ConfigureAwait(false);
		}
		catch (IOException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The verified image could not be written to the cache ({imagePath}): {ex.Message}", ex);
		}
		catch (UnauthorizedAccessException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The verified image could not be written to the cache ({imagePath}): {ex.Message}", ex);
		}

		return new PreparedPackage(imagePath, imageName, image.Length, true, packageUrl.ToString(), _time.GetUtcNow());
	}

	/// <summary>
	/// MD5 gate (shared with X-12/X-13): compares MD5(<paramref name="image"/>) against
	/// <paramref name="hex"/> case-insensitively. <paramref name="hex"/> may be a bare 32-hex digest with
	/// surrounding whitespace or a trailing newline, or an md5sum-style line (only the first token counts).
	/// Returns false — never throws — when the text does not reduce to 32 hex digits or the digest differs.
	/// </summary>
	public static bool VerifyMd5(ReadOnlySpan<byte> image, string hex)
	{
		ArgumentNullException.ThrowIfNull(hex);
		if (!TryNormalizeDigest(hex, out var digest))
		{
			return false;
		}

		return string.Equals(Convert.ToHexString(MD5.HashData(image)), digest, StringComparison.OrdinalIgnoreCase);
	}

	private async Task<byte[]> DownloadAsync(Uri packageUrl, CancellationToken ct)
	{
		try
		{
			using var client = new HttpClient(
				_handlerFactory?.Invoke() ?? SharedHandler,
				disposeHandler: _handlerFactory is not null)
			{
				Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds),
			};

			using var response = await client
				.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, ct)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				throw new FirmwarePackageException(FirmwarePackageFailure.Download,
					$"The firmware server answered HTTP {(int)response.StatusCode} ({response.StatusCode}) for {packageUrl}.", null);
			}

			var declared = response.Content.Headers.ContentLength;
			if (declared.GetValueOrDefault() > _maxPackageBytes)
			{
				throw new FirmwarePackageException(FirmwarePackageFailure.Download,
					$"The package declares {declared} bytes, over the {_maxPackageBytes}-byte cap.", null);
			}

			// Stream to memory under the cap — packages are a few MiB; the cap bounds hostile responses.
			await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			using var payload = new MemoryStream(declared is > 0 and <= int.MaxValue ? (int)declared.Value : 64 * 1024);
			var buffer = new byte[64 * 1024];
			long total = 0;
			int read;
			while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
			{
				total += read;
				if (total > _maxPackageBytes)
				{
					throw new FirmwarePackageException(FirmwarePackageFailure.Download,
						$"The package exceeded the {_maxPackageBytes}-byte download cap.", null);
				}

				payload.Write(buffer.AsSpan(0, read));
			}

			return payload.ToArray();
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException ex)
		{
			// HttpClient timeout (the caller token is handled above).
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The package download timed out after {DownloadTimeoutSeconds} s.", ex);
		}
		catch (HttpRequestException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The package download failed: {ex.Message}", ex);
		}
		catch (IOException ex)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The package download failed: {ex.Message}", ex);
		}
	}

	/// <summary>
	/// AES-256-CTR over the whole package: keystream block i = AES-ECB-encrypt(key, big-endian(5 + i)),
	/// plaintext = ciphertext XOR keystream, and the final partial block XORs a truncated keystream
	/// (fw-format.md §3). Encrypt and decrypt are the same operation (CTR is symmetric).
	/// </summary>
	private static byte[] DecryptCtr(ReadOnlySpan<byte> ciphertext)
	{
		var output = new byte[ciphertext.Length];
		using var aes = Aes.Create();
		aes.Key = Key;
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;

		var counter = new byte[AesBlockSize];
		counter[^1] = InitialCounter; // 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 05
		var keystream = new byte[AesBlockSize];
		for (var offset = 0; offset < ciphertext.Length; offset += AesBlockSize)
		{
			// ECB over one whole counter block = the raw AES block cipher = one keystream block.
			if (aes.EncryptEcb(counter, keystream, PaddingMode.None) != keystream.Length)
			{
				throw new FirmwarePackageException(FirmwarePackageFailure.Decrypt,
					"The AES keystream transform failed to produce a block.", null);
			}

			var chunk = Math.Min(AesBlockSize, ciphertext.Length - offset);
			for (var i = 0; i < chunk; i++)
			{
				output[offset + i] = (byte)(ciphertext[offset + i] ^ keystream[i]);
			}

			IncrementCounter(counter);
		}

		return output;
	}

	// Full-width 128-bit big-endian +1 with carry across the whole block (NOT a nonce + split counter).
	private static void IncrementCounter(Span<byte> counter)
	{
		for (var i = counter.Length - 1; i >= 0; i--)
		{
			counter[i]++;
			if (counter[i] != 0)
			{
				break; // no carry beyond this byte
			}
		}
	}

	private static (ZipArchiveEntry Image, ZipArchiveEntry Md5) FindEntries(ZipArchive archive)
	{
		var images = new List<ZipArchiveEntry>();
		var manifests = new List<ZipArchiveEntry>();
		foreach (var entry in archive.Entries)
		{
			var name = entry.Name;
			if (name.Length == 0)
			{
				continue; // directory entry
			}

			if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
			{
				images.Add(entry);
			}
			else if (name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase))
			{
				manifests.Add(entry);
			}
		}

		if (images.Count == 0)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.MissingImage,
				"The package ZIP contains no *.bin image entry.", null);
		}

		ZipArchiveEntry image;
		if (images.Count == 1)
		{
			image = images[0];
		}
		else
		{
			// Several images: the full-<locale> application image wins over vp-<locale> prompt packs (fw-format.md §1).
			var full = images.Where(e => e.Name.Contains("full-", StringComparison.OrdinalIgnoreCase)).ToList();
			if (full.Count != 1)
			{
				var names = string.Join(", ", images.Select(e => e.Name));
				throw new FirmwarePackageException(FirmwarePackageFailure.MissingImage,
					$"The package ZIP contains several image entries ({names}); refusing to pick one.", null);
			}

			image = full[0];
		}

		// Digest resolution: the image's own sidecar first, then a single *.md5 manifest (reference order, fw-format.md §2).
		var sidecarName = image.Name + ".md5";
		ZipArchiveEntry? manifest = null;
		foreach (var candidate in manifests)
		{
			if (string.Equals(candidate.Name, sidecarName, StringComparison.OrdinalIgnoreCase))
			{
				manifest = candidate;
				break;
			}
		}

		if (manifest is null && manifests.Count == 1)
		{
			manifest = manifests[0];
		}

		if (manifest is null)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.MissingMd5,
				manifests.Count == 0
					? "The package ZIP contains no *.md5 manifest entry."
					: "The package ZIP contains no manifest matching the image entry and more than one *.md5 entry.",
				null);
		}

		return (image, manifest);
	}

	private static async Task<PreparedPackage?> TryLoadCachedAsync(string imagePath, string md5Path, Uri packageUrl, CancellationToken ct)
	{
		if (!File.Exists(imagePath) || !File.Exists(md5Path))
		{
			return null;
		}

		try
		{
			var image = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
			var digest = await File.ReadAllTextAsync(md5Path, ct).ConfigureAwait(false);
			if (image.Length > 0 && VerifyMd5(image, digest))
			{
				return new PreparedPackage(
					imagePath,
					Path.GetFileName(imagePath),
					image.Length,
					true,
					packageUrl.ToString(),
					File.GetLastWriteTimeUtc(imagePath));
			}
		}
		catch (IOException)
		{
			// unreadable cache → treat as absent and re-prepare below
		}
		catch (UnauthorizedAccessException)
		{
		}

		return null; // missing, stale or corrupt cache → re-download and overwrite
	}

	private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry, CancellationToken ct)
	{
		await using var stream = entry.Open();
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
		return buffer.ToArray();
	}

	private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken ct)
	{
		await using var stream = entry.Open();
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
	}

	// <image> = package file name minus ".zip.enc" (fw-format.md §2); a bare ".enc" is also accepted.
	private static string DeriveImageName(string packageFileName)
	{
		if (packageFileName.EndsWith(".zip.enc", StringComparison.OrdinalIgnoreCase))
		{
			return packageFileName[..^".zip.enc".Length];
		}

		if (packageFileName.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
		{
			return packageFileName[..^".enc".Length];
		}

		return packageFileName;
	}

	private static string GetPackageFileName(Uri packageUrl)
	{
		var last = packageUrl.Segments[^1];
		if (last.Length == 0 || last[^1] == '/')
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The package URL carries no file name: {packageUrl}.", null);
		}

		if (last.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new FirmwarePackageException(FirmwarePackageFailure.Download,
				$"The package URL file name '{last}' contains characters that cannot be used in a cache file name.", null);
		}

		return last;
	}

	// Reduces a manifest/sidecar to the bare lowercase digest; md5sum-style "<digest>  <name>" keeps only the first token.
	private static bool TryNormalizeDigest(string text, out string digest)
	{
		digest = string.Empty;
		var span = text.AsSpan().Trim();
		var separator = span.IndexOfAny(DigestSeparators);
		if (separator >= 0)
		{
			span = span[..separator];
		}

		if (span.Length != 32)
		{
			return false;
		}

		var chars = new char[32];
		for (var i = 0; i < span.Length; i++)
		{
			var c = span[i];
			if (c is >= '0' and <= '9' or >= 'a' and <= 'f')
			{
				chars[i] = c;
			}
			else if (c is >= 'A' and <= 'F')
			{
				chars[i] = (char)(c - 'A' + 'a');
			}
			else
			{
				return false;
			}
		}

		digest = new string(chars);
		return true;
	}
}
