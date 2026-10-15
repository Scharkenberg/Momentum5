# Firmware package format (R-09)

Byte-level spec of the encrypted firmware packages that Sennheiser Smart Control /
Smart Control Plus distributes for MOMENTUM-class headphones (M5 device codename
`herakles-saturn`), plus the decrypt recipe implemented by `tools/fw/decrypt.py`.
This document is the reference for **X-10 (PackagePrep.cs)**.

Fact tags: **[REF]** = verified from `kero3009destiny/sennheiser-fw-RE` `dec.py`
(commit `103822bd`, 2025-11-02, cloned to `/tmp/fw-re` during R-09; single-file
reference implementation, cross-checked against PyCryptodome 3.23 semantics);
**[ERRATA R-03]** = recorded in `docs/plan/ERRATA.md`; **[INFER]** = naming-based
inference, verify on first real package (G4); **[EXT]** = robustness extension of
`tools/fw/decrypt.py` beyond the reference (does not change the algorithm).

## 1. Package acquisition

| Path | Detail |
|---|---|
| HTTP host | `https://firmware.s-consumer-cloud.com` **[ERRATA R-03]** |
| URL pattern | `/files/momentum5/<PackageDir>/<version>/herakles-saturn-<MMM-mmm-ppp>-dfu-encrypted-<kind>.bin.zip.enc` **[ERRATA R-03]** |
| Known example | `…/files/momentum5/Momentum5_VP_DE/6.21.5/herakles-saturn-006-021-005-dfu-encrypted-vp-de.bin.zip.enc` (voice-prompt pack, German) **[ERRATA R-03; digits [INFER]: 6.21.5 → `006-021-005`]** |
| Redirect entry | `GET /redirect/getSmartControlPlus.html` (handoff page); app API routes: `/product/firmware/gaia/update-info`, `/release-notes`, `/update-progress` **[ERRATA R-03]**. The metadata URL that yields the exact `<PackageDir>` + file list for the `full-en` image was **not found statically** (Flutter AOT) — capture it in R-04-style snoop when needed. |
| App cache | Android app cache dir of `com.sonova.chb.control` (root pull, gitignored `research/root/`): cached `…-dfu-encrypted-full-en.bin/.enc` entries **plus `.md5` sidecars** for firmware 6.21.1 and 6.21.5 **[ERRATA R-03]** |
| Support site | Sennheiser support hub publishes release notes; the binary comes from the same host (no user-visible direct download for M5) **[INFER]** |

`<kind>` observed/expected: `full-en` (full application image, embedded English
voice prompts) and `vp-<locale>` (voice-prompt pack, e.g. `vp-de`) — one package
per `Momentum5_VP_<LOCALE>` directory **[ERRATA R-03, [INFER]]**.

`tools/fw/decrypt.py` makes **no network calls**; acquisition is X-10's job.

## 2. Container layout

Canonical 3-layer package (what `…-dfu-encrypted-<kind>.bin.zip.enc` is) **[REF]**:

```
<image>.bin.zip.enc                ← what is downloaded / cached
└─ AES-256-CTR ciphertext          whole file, no header/salt/magic/skip [REF]
   <image>.zip                     ← plaintext of the .enc layer
   ├─ <image>.bin                  DFU image (see §5)
   └─ <image>.bin.md5              bare 32-hex-digit lowercase MD5 digest of <image>.bin [REF]
```

with `<image>` = `herakles-saturn-<MMM-mmm-ppp>-dfu-encrypted-<kind>.bin`
(reference derives it by stripping `.zip.enc` from the package filename **[REF]**).

Alternative 2-layer form seen in the app cache **[ERRATA R-03, [EXT]]**:
`…-full-en.bin.enc` + sibling `…-full-en.bin.md5` — decrypt yields the raw image
directly (no ZIP wrapper). `tools/fw/decrypt.py` auto-detects both: ZIP magic
`50 4B 03 04` (PK\x03\x04) at plaintext offset 0 selects the 3-layer flow, anything
else falls back to the 2-layer flow with a warning.

MD5 resolution order in `tools/fw/decrypt.py` **[REF + [EXT]]**:
1. explicit `--md5 <file>`;
2. manifest inside the ZIP (`<image>.bin.md5`, or the single `*.md5` entry) — the
   reference reads it from the extracted zip **[REF]**;
3. disk sidecar next to the package: `<file>.enc.md5`, `<image>.bin.md5`, or
   `<image>.bin.zip.md5` — the app-cache layout **[ERRATA R-03]**.

Manifest/sidecar content: the reference compares the whole (whitespace-stripped)
file content, i.e. the canonical form is a **bare digest** **[REF]**; the tool also
accepts `md5sum`-style `<digest>␠␠<filename>` lines **[EXT]**. MD5 here is a
transfer-integrity check, not a security control.

## 3. Decrypt algorithm (byte-level)

```
ciphertext   : the complete .enc file, every byte, arbitrary length (not %16)  [REF]
cipher       : AES-256, CTR mode                                             [REF]
key (hex)    : ed3b78a474c2acabb812407b97065649bf190e3d758e86f17b920aa41d8c271e  [REF]
counter      : one 128-bit integer block, BIG-ENDIAN, initial value 5:
                block[0] = 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 05
                block[1] = … 06, block[2] = … 07, … (full-width, no nonce)   [REF]
                == PyCryptodome Counter.new(128, initial_value=5)
plaintext    : ciphertext XOR keystream; keystream[i] = AES-ECB(key, block[i]) [REF]
verification : MD5(extracted <image>.bin) == digest from <image>.bin.md5       [REF]
```

There is **no** IV/nonce, no salt, no header to skip, no per-package variation —
key and counter are constants for every package and product seen so far **[REF]**.
CTR is symmetric, so "decrypt" and "encrypt" are the same operation.

### 3.1 Test vectors (for X-10's C# port)

AES-256, key above, ECB of the counter blocks (= the raw keystream) —
cross-verified PyCryptodome 3.23 vs `cryptography` 44.0.3 vs the pure-stdlib
backend of `tools/fw/decrypt.py`:

| counter block (hex) | AES-256 output (hex) |
|---|---|
| `00000000000000000000000000000005` | `2a61122d37ac67cc23a6e26087364eeb` |
| `00000000000000000000000000000006` | `31fd8400a33a9080bfd717cb556f5b9b` |
| `00000000000000000000000000000007` | `13571ff6085d789ce440f14f9d390042` |
| `00000000000000000000000000000008` | `f57ba3c70f1f6b2393dad91173eb611d` |

Synthetic package-layer vector (whole-file construction, not a real package):

```
plaintext  : 000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
ciphertext : 2a60102e33a961cb2bafe86b8b3b40e421ec9613b72f8697a7ce0dd049724584
```

A real end-to-end check (bucket done-when): decrypt a genuine package, unzip, and
confirm `MD5(<image>.bin) == <image>.bin.md5` — no real package was available to
R-09, so the reference implementation is the ground truth (mirrored operationally:
1.5 MB random-data synthetic package round-trips byte-identically through both
backends of the tool).

### 3.2 Library equivalents

| Environment | Exact construction |
|---|---|
| PyCryptodome (reference) | `AES.new(key, AES.MODE_CTR, counter=Counter.new(128, initial_value=5))` |
| Python `cryptography` | `Cipher(algorithms.AES(key), modes.CTR((5).to_bytes(16, "big")))` — `modes.CTR` takes the **full 16-byte initial counter block** |
| .NET BCL | no CTR mode — AES-**ECB keystream trick**, see §6 |
| Pure stdlib | `AES-ECB(counter block) XOR data`, counter = big-endian `5 + block_index` (implemented in `tools/fw/decrypt.py`) |

## 4. Key provenance and credit

The key and algorithm were published by the public research repository
`kero3009destiny/sennheiser-fw-RE` (`dec.py`, commit `103822bd`,
2025-11-02 — "Reverse engineering research from the js Smart Control App. Focuses
on firmware decryption etc."). The repo carries **no explicit LICENSE** file
("MIT-ish" per prior agents); the key is embedded in `tools/fw/decrypt.py` for
interoperability research with the explicit authorization of the Momentum5
project sponsor. Interoperability RE on your own devices is the sanctioned use
(see R6 methodology notes, EU 2009/24/EC art. 6 / DMCA §1201(f)); do not
redistribute decrypted firmware images.

## 5. The plaintext image

- `full-<locale>` packages: the **application firmware image** for the DFU
  (device firmware update) flow — filename says `dfu`, and Sennheiser's GAIA
  feature-version DB (R-02/R-03 root pull) shows the Qualcomm `dfu2` feature on
  M5 fw 6.21.5, i.e. the standard Qualcomm GAIA VM-upgrade/DFU tunnel (vendor
  `0x001D`, `Upgrade_VM_Control` / `0x0302` upgrade commands, R2 report §7) is
  the transport **[INFER + R2]**.
- `vp-<locale>` packages: **voice-prompt packs** (the app's prompt-language
  downloads) **[ERRATA R-03 naming]**.
- Internal structure of the `.bin` (DFU container header, image layouts,
  app-vs-prompt sections inside `full-en`) is **not documented by the reference
  repo**; treat it as an opaque blob for transfer, and fingerprint it with
  `python tools/fw/decrypt.py <pkg> --info` (sizes, magic, per-entry CRC-32,
  computed MD5) when the first real package arrives (G4 / X-09). Nothing in the
  decrypt/verify chain depends on the inner structure — the app itself pushes
  the image bytes over GAIA after the MD5 gate **[INFER]**.

## 6. X-10 (PackagePrep.cs) consumption notes

.NET BCL has no CTR mode, so implement AES-CTR as the **ECB keystream stream**
(X-10 bucket wording: "BCL `Aes` ECB keystream"):

```csharp
// Aes in ECB with no padding is the AES block cipher primitive.
using var aes = Aes.Create();
aes.Key = Convert.FromHexString(FwKeyHex);     // 32 bytes => AES-256
aes.Mode = CipherMode.ECB;
aes.Padding = PaddingMode.None;
using var etor = aes.CreateEncryptor();       // keystream generator

// counter block i (16 bytes, big-endian) = 5 + i; increments as ONE 128-bit int
static void Increment(Span<byte> block) {      // big-endian full-width carry
    for (int i = 15; i >= 0; i--)
        if (++block[i] != 0) break;            // carry stops at first non-wrap
}

var counter = new byte[16]; counter[15] = 5;   // 0x00000000000000000000000000000005
// per 16-byte chunk: keystream = etor.TransformFinalBlock(counter, 0, 16);
//                     plaintext[i] = ciphertext[i] ^ keystream[i];
// final partial chunk: XOR only the remaining keystream bytes.
// Counter NEVER resets mid-file; encrypt == decrypt (CTR is symmetric).
```

Pipeline for X-10 (deps: X-09, R-09):

1. Download `…-dfu-encrypted-<kind>.bin.zip.enc` from
   `firmware.s-consumer-cloud.com` (§1) or accept a user-supplied file / app-cache
   path; stream to disk.
2. Decrypt with the fixed construction above (stream in chunks; MD5 of the
   ciphertext is *not* meaningful — the app's own MD5s cover the inner image).
3. Check plaintext magic `50 4B 03 04` → ZIP layer; open with `System.IO.Compression`,
   read `<image>.bin` and `<image>.bin.md5`; verify entry CRC-32 while reading
   (ZIP reader does this on extract).
4. `MD5.HashData(image)` must equal the digest (bare or `md5sum`-style sidecar,
   §2). Mismatch ⇒ reject, never hand anything to the GAIA upgrade tunnel.
5. Keep the raw `.enc` + sidecar in the app cache dir (X-09 cache contract),
   plus the decrypted image for X-11/X-12 (staged GAIA DFU transfer — vendor
   `0x001D`, gated X-track consent).
6. Cross-check the port with the §3.1 vectors; the definitive check remains a
   real package whose MD5 verifies.

`tools/fw/decrypt.py` is the executable spec for all of the above
(exit codes: 0 ok, 1 MD5 mismatch, 2 operational/usage error; `--info` for
read-only container analysis; stdlib-only, optional `cryptography` accelerator,
`MOMENTUM5_FW_PURE_AES=1` forces the pure backend).
