#!/usr/bin/env python3
"""Sennheiser MOMENTUM firmware package decrypt tool (Momentum5 bucket R-09).

Decrypts the encrypted firmware packages that the Sennheiser Smart Control /
Smart Control Plus apps download for MOMENTUM-class headphones, verifies the
MD5 manifest, and unwraps the ZIP container.

ALGORITHM — byte-exact mirror of the reference implementation ``dec.py`` in
the public research repository
https://github.com/kero3009destiny/sennheiser-fw-RE (commit 103822bd, 2025-11,
reverse-engineered from Sennheiser's JavaScript Smart Control app):

    ciphertext   = the COMPLETE ``.enc`` file — no header, no salt, no magic,
                   no bytes skipped; CTR accepts any file length
    cipher       = AES-256 in CTR mode
    key          = fixed 32-byte key (``FW_KEY_HEX`` below)
    counter      = FULL-WIDTH 128-bit big-endian integer block counter with
                   initial value 5 — first counter block is
                   ``00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 05``,
                   then ``...06``, ``...07`` … (one whole 128-bit integer,
                   NOT an 8-byte nonce + 8-byte counter). Equivalent to
                   PyCryptodome ``Counter.new(128, initial_value=5)``.
    plaintext    = ciphertext XOR keystream
                   (keystream block i = AES-ECB-encrypt(counter block 5+i))

The plaintext of a ``<image>.bin.zip.enc`` package is a ZIP archive that
contains the DFU image ``<image>.bin`` plus an MD5 manifest
``<image>.bin.md5`` (bare 32-hex-digit digest). The MD5 of the extracted
image must equal that manifest. The app's HTTP cache also keeps ``.md5``
sidecars next to ``.enc`` files (docs/plan/ERRATA.md R-03); this tool
accepts the explicit ``--md5`` path, the in-zip manifest, or a sidecar
next to the input, in that order.

DEPENDENCIES: Python 3.9+, standard library only. If the optional
third-party package ``cryptography`` is importable it provides a fast
C-backed AES-CTR; otherwise a pure-stdlib AES-CTR implementation in this
file is used (identical output, slower). Set ``MOMENTUM5_FW_PURE_AES=1``
to force the pure backend. This tool makes NO network access.

The AES key below is published in the reference repository above. That repo
carries no explicit LICENSE file; the key is reproduced here for
interoperability research with the project sponsor's explicit authorization
(see docs/fw-format.md, section "Key provenance and credit").

Usage:
    python decrypt.py <package.enc> [-o out.bin] [--md5 sidecar.md5]
    python decrypt.py <package.enc> --info

Exit codes:
    0  success (decryption OK; MD5 verified when a reference MD5 was found)
    1  MD5 verification failed (mismatch) — output still written and flagged
    2  operational error (missing/unreadable file, bad container, bad MD5
       sidecar, wrong key size …). argparse itself exits 2 on usage errors.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import os
import re
import sys
import zipfile
from dataclasses import dataclass
from typing import List, Optional, Tuple

EXIT_OK = 0
EXIT_MISMATCH = 1
EXIT_ERROR = 2

AES_BLOCK = 16
ZIP_LOCAL_MAGIC = b"PK\x03\x04"

# --- published constants -----------------------------------------------------
# 256-bit AES key, as published in kero3009destiny/sennheiser-fw-RE
# <https://github.com/kero3009destiny/sennheiser-fw-RE> (dec.py, commit
# 103822bd, 2025-11-02, "Reverse engineering research from the js Smart
# Control App. Focuses on firmware decryption etc."). The repo has no
# explicit LICENSE; embedded here for interoperability research with the
# explicit authorization of the Momentum5 project sponsor.
FW_KEY_HEX = (
    "ed3b78a474c2acabb812407b97065649"
    "bf190e3d758e86f17b920aa41d8c271e"
)
FW_KEY = bytes.fromhex(FW_KEY_HEX)

# CTR block-counter initial value: the whole 128-bit counter block is the
# big-endian encoding of an integer that starts at 5 and increments by 1
# per 16-byte keystream block (reference: Counter.new(128, initial_value=5)).
CTR_START = 5

# Set MOMENTUM5_FW_PURE_AES=1 to skip the optional C backend.
_PURE_ENV = "MOMENTUM5_FW_PURE_AES"

try:  # optional fast backend (C-backed); falls back to pure stdlib below
    from cryptography.hazmat.primitives.ciphers import (  # type: ignore
        Cipher,
        algorithms,
        modes,
    )

    _HAVE_CRYPTOGRAPHY = True
except ImportError:  # pragma: no cover - exercised only without the package
    _HAVE_CRYPTOGRAPHY = False


class ToolError(Exception):
    """Operational error that maps to exit code 2."""


# --- pure-stdlib AES-CTR (fallback backend) ----------------------------------
#
# Textbook FIPS-197 AES encryption (validated during development against
# FIPS-197 appendix C.3 and PyCryptodome 3.23 AES-ECB). Only the encryption
# direction is needed: CTR mode is a stream mode built from ECB encryption
# of counter blocks.


def _make_sbox() -> bytearray:
    """Build the AES S-box from first principles (avoids transcription bugs)."""
    # GF(2^8) log/exp tables over generator 3, poly x^8+x^4+x^3+x+1 (0x11B).
    exp = bytearray(256)
    log = bytearray(256)
    v = 1
    for i in range(255):
        exp[i] = v
        log[v] = i
        v2 = ((v << 1) & 0xFF) ^ (0x1B if v & 0x80 else 0x00)  # v * 2
        v = v2 ^ v  # v * 3
    sbox = bytearray(256)
    sbox[0] = 0x63  # 0 has no inverse; affine of 0 gives 0x63
    for a in range(1, 256):
        b = exp[(255 - log[a]) % 255]  # multiplicative inverse of a
        sbox[a] = (
            b
            ^ ((b << 1) & 0xFF | b >> 7)
            ^ ((b << 2) & 0xFF | b >> 6)
            ^ ((b << 3) & 0xFF | b >> 5)
            ^ ((b << 4) & 0xFF | b >> 4)
            ^ 0x63
        ) & 0xFF
    return sbox


_SBOX = _make_sbox()
_MUL2 = bytearray(((x << 1) & 0xFF) ^ (0x1B if x & 0x80 else 0x00) for x in range(256))
_MUL3 = bytearray(_MUL2[x] ^ x for x in range(256))
_RCON = (0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36, 0x6C, 0xD8, 0xAB, 0x4D)


def _expand_key(key: bytes) -> Tuple[List[List[int]], int]:
    """AES key expansion (FIPS-197 §5.2). Returns (round-key words, rounds)."""
    nk = len(key) // 4
    if len(key) not in (16, 24, 32):
        raise ToolError(f"unsupported AES key length: {len(key)} bytes")
    nr = nk + 6
    words: List[List[int]] = [list(key[4 * i : 4 * i + 4]) for i in range(nk)]
    rcon_i = 0
    i = nk
    while len(words) < 4 * (nr + 1):
        temp = list(words[i - 1])
        if i % nk == 0:
            temp = temp[1:] + temp[:1]  # RotWord
            temp = [_SBOX[b] for b in temp]  # SubWord
            temp[0] ^= _RCON[rcon_i]
            rcon_i += 1
        elif nk > 6 and i % nk == 4:
            temp = [_SBOX[b] for b in temp]  # SubWord (AES-256 extra step)
        prev = words[i - nk]
        words.append([prev[j] ^ temp[j] for j in range(4)])
        i += 1
    return words, nr


def _shift_rows(s: bytearray) -> bytearray:
    """ShiftRows on a flat column-major state (s[r + 4c])."""
    out = bytearray(16)
    for r in range(4):
        for c in range(4):
            out[4 * c + r] = s[4 * ((c + r) % 4) + r]
    return out


def _mix_columns(s: bytearray) -> None:
    """MixColumns in place on a flat column-major state."""
    for c in range(0, 16, 4):
        a0, a1, a2, a3 = s[c], s[c + 1], s[c + 2], s[c + 3]
        s[c] = _MUL2[a0] ^ _MUL3[a1] ^ a2 ^ a3
        s[c + 1] = a0 ^ _MUL2[a1] ^ _MUL3[a2] ^ a3
        s[c + 2] = a0 ^ a1 ^ _MUL2[a2] ^ _MUL3[a3]
        s[c + 3] = _MUL3[a0] ^ a1 ^ a2 ^ _MUL2[a3]


def _aes_encrypt_block(block: bytes, rk: List[List[int]], nr: int) -> bytes:
    """Encrypt one 16-byte block with the expanded key (round keys pre-baked)."""
    s = bytearray(block)
    for c in range(4):  # AddRoundKey(0)
        w = rk[c]
        s[4 * c] ^= w[0]
        s[4 * c + 1] ^= w[1]
        s[4 * c + 2] ^= w[2]
        s[4 * c + 3] ^= w[3]
    for rnd in range(1, nr):
        for i in range(16):
            s[i] = _SBOX[s[i]]
        s = _shift_rows(s)
        _mix_columns(s)
        base = 4 * rnd
        for c in range(4):
            w = rk[base + c]
            s[4 * c] ^= w[0]
            s[4 * c + 1] ^= w[1]
            s[4 * c + 2] ^= w[2]
            s[4 * c + 3] ^= w[3]
    for i in range(16):  # final round (no MixColumns)
        s[i] = _SBOX[s[i]]
    s = _shift_rows(s)
    base = 4 * nr
    for c in range(4):
        w = rk[base + c]
        s[4 * c] ^= w[0]
        s[4 * c + 1] ^= w[1]
        s[4 * c + 2] ^= w[2]
        s[4 * c + 3] ^= w[3]
    return bytes(s)


class _PureAesCtr:
    """Pure-stdlib AES-CTR with a full-width big-endian integer counter."""

    def __init__(self, key: bytes, initial_value: int) -> None:
        self._rk, self._nr = _expand_key(key)
        self._start = initial_value

    def crypt(self, data: bytes) -> bytes:
        """XOR ``data`` with the AES-CTR keystream (encrypt == decrypt)."""
        n = len(data)
        if n == 0:
            return b""
        out = bytearray(n)
        rk, nr, start = self._rk, self._nr, self._start
        n_full = n // AES_BLOCK
        for i in range(n_full):
            ks = _aes_encrypt_block((start + i).to_bytes(16, "big"), rk, nr)
            off = AES_BLOCK * i
            for j in range(AES_BLOCK):
                out[off + j] = data[off + j] ^ ks[j]
        rem = n - AES_BLOCK * n_full
        if rem:
            ks = _aes_encrypt_block((start + n_full).to_bytes(16, "big"), rk, nr)
            off = AES_BLOCK * n_full
            for j in range(rem):
                out[off + j] = data[off + j] ^ ks[j]
        return bytes(out)


# --- core decrypt -------------------------------------------------------------


def ctr_decrypt(data: bytes) -> bytes:
    """Decrypt an ``.enc`` blob with the fixed Sennheiser key/counter.

    Mirrors ``AES.new(FW_KEY, AES.MODE_CTR, counter=Counter.new(128,
    initial_value=5)).decrypt(data)`` from the reference ``dec.py``.
    """
    if not isinstance(data, (bytes, bytearray)):
        raise ToolError("ctr_decrypt expects bytes")
    if _HAVE_CRYPTOGRAPHY and not os.environ.get(_PURE_ENV):
        # modes.CTR(<16 bytes>) is the full initial counter block; the
        # library increments it as one 128-bit big-endian integer.
        dec = Cipher(
            algorithms.AES(FW_KEY), modes.CTR(CTR_START.to_bytes(16, "big"))
        ).decryptor()
        return dec.update(bytes(data)) + dec.finalize()
    return _PureAesCtr(FW_KEY, CTR_START).crypt(bytes(data))


def md5_hex(data: bytes) -> str:
    """MD5 hex digest (the packages' integrity hash — not a security hash)."""
    return hashlib.md5(data).hexdigest()


# --- container / name analysis ------------------------------------------------

_NAME_RE = re.compile(
    r"^(?P<codename>[a-z0-9]+(?:-[a-z0-9]+)*?)-"
    r"(?P<maj>\d{1,4})-(?P<min>\d{1,4})-(?P<pat>\d{1,4})"
    r"-dfu-encrypted-(?P<kind>[A-Za-z0-9._-]+?)\.bin(?:\.zip)?(?:\.enc)?$",
    re.IGNORECASE,
)

_MD5_TOKEN_RE = re.compile(r"\b([0-9a-fA-F]{32})\b")


@dataclass(frozen=True)
class NameInfo:
    """Facts parsed from a package filename (URL pattern per ERRATA R-03)."""

    codename: Optional[str]
    version: Optional[str]
    kind: Optional[str]
    kind_note: str


def analyze_name(filename: str) -> NameInfo:
    """Parse ``herakles-saturn-<MMM>-<mmm>-<ppp>-dfu-encrypted-<kind>.bin...``."""
    m = _NAME_RE.match(os.path.basename(filename))
    if not m:
        return NameInfo(None, None, None, "name does not match the known pattern")
    version = f"{int(m['maj'])}.{int(m['min'])}.{int(m['pat'])}"
    kind = m["kind"]
    k = kind.lower()
    if k.startswith("vp-"):
        note = f"voice-prompt pack, locale '{k[3:]}'"
    elif k.startswith("full"):
        locale = k[len("full") :].lstrip("-_") or "?"
        note = f"full application image (embedded voice-prompt locale '{locale}')"
    else:
        note = "unknown package kind"
    return NameInfo(m["codename"], version, kind, note)


def strip_container_suffixes(name: str) -> str:
    """Strip trailing ``.enc`` then ``.zip`` (dec.py's derived inner name)."""
    if name.lower().endswith(".enc"):
        name = name[: -len(".enc")]
    if name.lower().endswith(".zip"):
        name = name[: -len(".zip")]
    return name


def expected_inner_name(input_path: str) -> str:
    """Inner image name the reference derives from the ``.enc`` filename."""
    return strip_container_suffixes(os.path.basename(input_path))


def default_output_path(input_path: str) -> str:
    """Reference-style output: ``<input dir>/<inner name>_decrypted.bin``."""
    directory = os.path.dirname(os.path.abspath(input_path))
    return os.path.join(directory, strip_container_suffixes(os.path.basename(input_path)) + "_decrypted.bin")


@dataclass
class Md5Ref:
    """A reference (expected) MD5 digest and where it came from."""

    digest: Optional[str]
    source: Optional[str]


def parse_md5_text(text: str, source: str) -> str:
    """Extract the 32-hex digest from a manifest/sidecar.

    The reference compares the whole (stripped) file content, i.e. the
    canonical format is a bare lowercase digest; ``md5sum``-style
    ``<digest>  <filename>`` lines are accepted as a robustness extension.
    """
    stripped = text.strip()
    m = _MD5_TOKEN_RE.search(stripped)
    if not m:
        raise ToolError(f"cannot parse MD5 digest from {source}: {stripped[:60]!r}")
    return m.group(1).lower()


def md5_sidecar_candidates(input_path: str) -> List[str]:
    """Disk sibling candidates for the app-cache ``.md5`` sidecar layout."""
    base = os.path.abspath(input_path)
    stripped = strip_container_suffixes(base)
    cands = [base + ".md5", stripped + ".md5"]  # <file>.enc.md5 / <image>.bin.md5
    if base.lower().endswith(".enc"):
        cands.append(base[: -len(".enc")] + ".md5")  # <image>.bin.zip.md5
    return list(dict.fromkeys(cands))


def locate_reference_md5(
    zip_file: Optional[zipfile.ZipFile],
    input_path: str,
    explicit: Optional[str],
    inner_name: Optional[str],
) -> Md5Ref:
    """Find the expected MD5: --md5 flag, in-zip manifest, then disk sidecar."""
    if explicit is not None:
        with open(explicit, "r", encoding="utf-8", errors="replace") as fh:
            return Md5Ref(parse_md5_text(fh.read(), explicit), f"file {explicit}")
    if zip_file is not None:
        names = zip_file.namelist()
        wanted = (inner_name + ".md5") if inner_name else None
        entry = None
        if wanted and wanted in names:
            entry = wanted
        else:
            md5_entries = [n for n in names if n.lower().endswith(".md5")]
            if len(md5_entries) == 1:
                entry = md5_entries[0]
        if entry is not None:
            raw = zip_file.read(entry).decode("utf-8", errors="replace")
            return Md5Ref(parse_md5_text(raw, f"zip entry {entry}"), f"zip entry {entry}")
    for cand in md5_sidecar_candidates(input_path):
        if cand and os.path.isfile(cand):
            with open(cand, "r", encoding="utf-8", errors="replace") as fh:
                return Md5Ref(parse_md5_text(fh.read(), cand), f"file {cand}")
    return Md5Ref(None, None)


def pick_inner_entry(zip_file: zipfile.ZipFile, inner_name: Optional[str]) -> str:
    """Choose the firmware image entry (reference: exact inner name match)."""
    names = [n for n in zip_file.namelist() if not n.endswith("/")]
    if inner_name and inner_name in names:
        return inner_name
    bins = [n for n in names if not n.lower().endswith(".md5")]
    if not bins:
        raise ToolError("zip container has no non-manifest entry")
    if len(bins) == 1:
        return bins[0]
    return max(bins, key=lambda n: zip_file.getinfo(n).file_size)


def hex_head(data: bytes, n: int = 32) -> str:
    """First ``n`` bytes of ``data`` as a spaced hex string."""
    return " ".join(f"{b:02x}" for b in data[:n])


# --- reporting ----------------------------------------------------------------


def print_info(input_path: str, data: bytes) -> int:
    """``--info``: analyze the container; read-only, writes nothing."""
    name_info = analyze_name(input_path)
    plain = ctr_decrypt(data)
    print("== Sennheiser firmware package analysis (R-09) ==")
    print(f"file              : {input_path}")
    print(f"size              : {len(data):,} bytes")
    print(f"md5 (as stored)   : {md5_hex(data)}")
    print(f"first 32 bytes    : {hex_head(data)}  (ciphertext)")
    if name_info.codename:
        print(f"name analysis     : codename={name_info.codename} version={name_info.version} kind={name_info.kind}")
        print(f"                    -> {name_info.kind_note}")
    else:
        print(f"name analysis     : {name_info.kind_note}")
    print("algorithm         : AES-256-CTR, whole file, no header/skip")
    print(f"key               : {FW_KEY_HEX}")
    print(f"counter           : full-width 128-bit big-endian, start={CTR_START}")
    print(f"                    block[0]={CTR_START.to_bytes(16, 'big').hex()}")
    print(f"                    block[1]={(CTR_START + 1).to_bytes(16, 'big').hex()} ...")
    print(f"backend           : {'cryptography' if _HAVE_CRYPTOGRAPHY and not os.environ.get(_PURE_ENV) else 'pure-stdlib'}")
    print(f"decrypt result    : {len(plain):,} bytes plaintext")
    print(f"plaintext magic   : {hex_head(plain, 4)}  ({'ZIP container' if plain.startswith(ZIP_LOCAL_MAGIC) else 'NOT a ZIP'})")

    if plain.startswith(ZIP_LOCAL_MAGIC):
        try:
            with zipfile.ZipFile(io.BytesIO(plain)) as zf:
                print("zip entries       :")
                for info in zf.infolist():
                    if info.is_dir():
                        continue
                    print(f"  {info.filename}  {info.file_size:,} bytes  crc32={info.CRC:08x}")
                inner = pick_inner_entry(zf, expected_inner_name(input_path))
                print(f"inner image       : {inner}")
                ref = locate_reference_md5(zf, input_path, None, inner)
                _print_md5_status(ref, md5_hex(zf.read(inner)))
                print("layout            : encrypted(zip(image.bin + image.bin.md5)) - 3-layer container")
        except (zipfile.BadZipFile, RuntimeError, NotImplementedError, ToolError) as exc:
            print(f"zip analysis      : FAILED - {exc}")
            return EXIT_ERROR
    else:
        print("layout            : encrypted(image.bin) - 2-layer (no zip wrapper)")
        ref = locate_reference_md5(None, input_path, None, expected_inner_name(input_path))
        _print_md5_status(ref, md5_hex(plain))
    return EXIT_OK


def _print_md5_status(ref: Md5Ref, actual: str) -> None:
    if ref.digest:
        status = "MATCH" if ref.digest == actual else "MISMATCH"
        print(f"md5 manifest      : {ref.digest} ({ref.source})")
        print(f"md5 computed      : {actual}")
        print(f"md5 status        : {status}")
    else:
        print(f"md5 computed      : {actual}")
        print("md5 status        : no reference digest found (NOT VERIFIED)")


# --- main decrypt flow --------------------------------------------------------


def run_decrypt(args: argparse.Namespace) -> int:
    """Decrypt, verify, and write the plaintext image. Returns exit code."""
    input_path: str = args.package
    try:
        with open(input_path, "rb") as fh:
            data = fh.read()
    except OSError as exc:
        raise ToolError(f"cannot read {input_path}: {exc}") from exc
    if not data:
        raise ToolError(f"{input_path} is empty")

    print(f"DECRYPT: {input_path} ({len(data):,} bytes)")
    plain = ctr_decrypt(data)
    print(f"DECRYPT: AES-256-CTR done, {len(plain):,} bytes plaintext")

    out_path: str = args.output or default_output_path(input_path)

    if plain.startswith(ZIP_LOCAL_MAGIC):
        # Reference flow: plaintext is a zip holding image + md5 manifest.
        try:
            with zipfile.ZipFile(io.BytesIO(plain)) as zf:
                inner_name = pick_inner_entry(zf, expected_inner_name(input_path))
                print(f"UNZIP:   zip container, inner image = {inner_name}")
                try:
                    image = zf.read(inner_name)  # verifies CRC on read
                except (zipfile.BadZipFile, RuntimeError, NotImplementedError) as exc:
                    raise ToolError(f"zip entry {inner_name} corrupted after decryption: {exc}") from exc
                ref = locate_reference_md5(zf, input_path, args.md5, inner_name)
        except zipfile.BadZipFile as exc:
            raise ToolError(f"invalid zip file after decryption: {exc}") from exc
        print(f"UNZIP:   extracted {len(image):,} bytes")
    else:
        # Extension beyond the reference: a raw (non-zipped) encrypted image,
        # e.g. the app-cache `*.bin.enc` layout noted in ERRATA R-03.
        image = plain
        inner_name = expected_inner_name(input_path)
        print("WARN:    decrypted plaintext is not a ZIP; treating the whole blob")
        print("WARN:    as the raw image (layout: encrypted(image.bin))")
        ref = locate_reference_md5(None, input_path, args.md5, inner_name)

    actual = md5_hex(image)
    print(f"MD5:     computed  {actual}")

    with open(out_path, "wb") as fh:
        fh.write(image)
    print(f"WRITE:   {out_path} ({len(image):,} bytes)")

    if ref.digest is None:
        print("MD5:     no reference digest available - NOT VERIFIED (exit 0)")
        return EXIT_OK
    print(f"MD5:     expected  {ref.digest} ({ref.source})")
    if ref.digest == actual:
        print("UNZIPPED: successfully decrypted, extracted, and verified")
        return EXIT_OK
    print("ERROR:   file is corrupted - MD5 does not match")
    print(f"ERROR:   expected {ref.digest}, got {actual} (plaintext written; inspect {out_path})")
    return EXIT_MISMATCH


def build_parser() -> argparse.ArgumentParser:
    """CLI parser (exit code 2 on usage errors, per argparse)."""
    parser = argparse.ArgumentParser(
        prog="decrypt.py",
        description=(
            "Decrypt and verify a Sennheiser MOMENTUM firmware package "
            "(AES-256-CTR, counter start 5, MD5 manifest; mirrors "
            "kero3009destiny/sennheiser-fw-RE dec.py). No network access."
        ),
        epilog=(
            "exit codes: 0 ok, 1 MD5 mismatch, 2 operational/usage error. "
            "See docs/fw-format.md for the byte-level container spec."
        ),
    )
    parser.add_argument("package", help="encrypted package (.bin.zip.enc or .bin.enc)")
    parser.add_argument("--md5", help="explicit MD5 sidecar/manifest file (bare 32-hex digest or md5sum format)")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("-o", "--output", help="output plaintext path (default: <inner name>_decrypted.bin)")
    group.add_argument("--info", action="store_true", help="analyze container structure only; no files written")
    return parser


def main(argv: Optional[List[str]] = None) -> int:
    """Entry point. Returns the process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.info:
        try:
            with open(args.package, "rb") as fh:
                data = fh.read()
        except OSError as exc:
            print(f"ERROR: cannot read {args.package}: {exc}", file=sys.stderr)
            return EXIT_ERROR
        if not data:
            print(f"ERROR: {args.package} is empty", file=sys.stderr)
            return EXIT_ERROR
        return print_info(args.package, data)
    try:
        return run_decrypt(args)
    except ToolError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_ERROR


if __name__ == "__main__":
    sys.exit(main())
