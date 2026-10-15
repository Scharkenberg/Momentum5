# Momentum5

Momentum5 is a community Windows client for the Sennheiser MOMENTUM 5 Wireless
headset (model string `M5AEBT`). It provides a WinUI 3 desktop app and the
`m5ctl` command-line tool, talking to the headset over the Bluetooth Classic
RFCOMM GAIA v3 control channel that the official Sennheiser Smart Control Plus
mobile app uses.

The goal is feature parity with Smart Control Plus — ANC, transparency, the
8-band equalizer, presets, voice prompts, sidetone/comfort calls, multipoint
connection management, battery and device info — including the advanced
features the official app exposes (rename, forget pairing, factory reset,
firmware update). Advanced and destructive features are not hidden: they are
behind explicit typed-consent gates (see [docs/SAFETY.md](docs/SAFETY.md)).

This project is **unofficial**. It is not affiliated with, endorsed by, or
supported by Sonova/Sennheiser. Publicly, Sennheiser documents only USB-C audio
as the PC path for this headset and has made no statement about third-party
control clients; independent GAIA-based clients for Sennheiser headsets have
existed for years and work on the M5. The protocol facts in this repo were
derived from published community research and from HCI captures of the
sponsor's own device; interoperability research on your own devices is the
sanctioned use (see [docs/fw-format.md](docs/fw-format.md) §4 for the caveats).

Scope is locked to the MOMENTUM 5 (`M5AEBT*`); a model gate disconnects any
other device with "unsupported device" — there is no generic Sennheiser mode.

## Status

Snapshot per plan track; the live, bucket-by-bucket status is
[docs/plan/PROGRESS.md](docs/plan/PROGRESS.md).

| Track | Scope | Status |
|---|---|---|
| A foundation | build props, central packages, contracts, tests project | DONE (agent shells had no .NET; user builds) |
| P protocol | GAIA v3 frames, catalog, bindings, M5 profile | DONE — unit tests pass |
| S simulation | loopback transport + FakeM5 fake headset | DONE |
| C client | link, pipeline, handshake, supervisor, settings, consent | DONE (build unverified in agent shell) |
| T transports | RFCOMM transport, presence, radio monitor, HFP battery; BLE adverts/GATT explorer | DONE; **G1 hardware spike PASSED on Windows 11**; GAIA-over-GATT (T-08) stays TODO pending G3 |
| K CLI | `m5ctl` | DONE; the G2 selftest run on hardware is the user's |
| U WinUI app | shell, pages, tray, toasts, hotkeys | U-01..U-19 DONE; theming/accessibility (U-20) and location zones (U-21) in the final session |
| X advanced | consent, DevMode, DevTools console, forget pairings, factory reset, firmware stages 0–1 | DONE except X-07 (SKIPPED — semantics unresolved); X-11/X-13 in final session; X-12 BLOCKED-HW (needs an OTA capture); hardware rounds of several pages pending |
| R research | APK study, Android HCI captures, firmware format | R-01..R-04 and R-09 DONE; R-05..R-08 BLOCKED-HW (user hardware) |
| I infra/docs | CI, packaging, documentation, release checklist | CI DONE (first run pending); this documentation set, packaging and release checklist are the final session |
| V verification | verification matrix, coexistence notes | matrix generator DONE (fill on hardware); coexistence doc in final session |

Most rows marked "unverified" mean exactly that: the implementation agents
worked in a Linux container without the .NET/Windows SDKs, so compilation and
hardware checks are performed by the user on Windows.

## Safety

Destructive commands (factory reset, forget pairing, firmware update) are
**consent-gated**: they require DevMode, a dialog that spells out the
consequence in plain language, a typed confirmation phrase, a 5-second
cooldown, and a single-use, time-limited consent token; every Dangerous
execution is logged before and after. The firmware update flow is staged
(battery > 30 % precheck, package MD5 gate, staged consent, recovery guide)
and never sends an upgrade command that was not observed in a captured
official session. Read [docs/SAFETY.md](docs/SAFETY.md) before using the
Advanced pages or the DevTools console.

## Install

There is no packaged release yet. Packaging (MSIX, signing, ADR) is bucket I-02
and the release checklist will live in [docs/release.md](docs/release.md);
until then, build from source:

1. Install the .NET SDK pinned by [global.json](global.json)
   (`11.0.100-rc.2.26504.105`, `rollForward: latestMajor`,
   `allowPrerelease: true` — any later .NET 11 SDK works).
2. `dotnet build Momentum5.slnx` (the App builds for x64 and ARM64).
3. Run the unpackaged WinUI app (Momentum5.App output) — no MSIX install is
   required; the RFCOMM transport is WinRT-based and needs a Windows machine
   with Bluetooth (verified on Windows 11; the G1 spike passed there).

CLI usage (`m5ctl list|status|get|set|…`, `--simulate` for the built-in fake
headset, `--json` for machine-readable output) is documented in
[docs/cli.md](docs/cli.md).

## Repository layout

| Path | Contents |
|---|---|
| `Momentum5.Core/` | `net11.0` — transport + settings contracts, domain types, safety types. References nothing. |
| `Momentum5.Protocol/` | `net11.0` — GAIA v3 frame/deframer, command words, catalog, typed bindings, M5 profile, formatter. |
| `Momentum5.Client/` | `net11.0` — GaiaLink, command pipeline, safety gate, consent service, handshake, supervisor, state store, firmware update check/prepare. No WinRT. |
| `Momentum5.Simulation/` | `net11.0` — LoopbackTransport + FakeM5 (agent- and test-facing fake headset). |
| `Momentum5.Transport.Rfcomm/` | `net11.0-windows10.0.26100.0` — WinRT RFCOMM GAIA transport, Classic presence, radio monitor, HFP battery. |
| `Momentum5.Transport.Ble/` | `net11.0-windows10.0.26100.0` — GATT explorer, BLE advertisement presence. |
| `Momentum5.App/` | WinUI 3 app (x64/ARM64) — composition root, pages, tray, hotkeys. |
| `Momentum5.Cli/` | `m5ctl` (x64/ARM64) — composition root. |
| `Momentum5.Tests/` | `net11.0` — unit/integration tests + golden capture fixtures. |
| `docs/` | user and engineering docs (this set). |
| `docs/plan/` | the implementation plan: [README](docs/plan/README.md) (workflow), [INDEX](docs/plan/INDEX.md) (buckets), [PROGRESS](docs/plan/PROGRESS.md), [ERRATA](docs/plan/ERRATA.md), `ref/` reference chunks, `buckets/` specs. |
| `tools/` | research tools: HCI capture scripts, btsnoop export, firmware decrypt (`tools/fw/decrypt.py`). |
| `.github/workflows/ci.yml` | CI (first run pending). |

Architecture overview: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — projects, dependency rules, contracts, transports.
- [docs/SAFETY.md](docs/SAFETY.md) — safety classes, trust tiers, consent, known-dangerous commands.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) — bucket workflow, output contract, house rules.
- [docs/cli.md](docs/cli.md) — `m5ctl` reference.
- [docs/fw-format.md](docs/fw-format.md) — encrypted firmware package format and decrypt recipe.
- [docs/capture-procedure.md](docs/capture-procedure.md) — how the HCI captures were taken.
- [docs/verification-matrix.md](docs/verification-matrix.md) — per-setting hardware verification matrix.
- [docs/plan/ref/00-protocol.md](docs/plan/ref/00-protocol.md) — **the** protocol document: GAIA v3 frame format, command algebra, session rules, M5 semantics, with evidence tags.
- [docs/release.md](docs/release.md) — release checklist (upcoming; final session).

## License and credits

Code: MIT ([LICENSE.txt](LICENSE.txt)). Third-party material:
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) (Ariazonaa/Momentum4Control
MIT — the M5 hardware-capture fixtures).

The protocol knowledge this project builds on comes from public community
research, principally:

- [zaval/sennheiser-desktop-client](https://github.com/zaval/sennheiser-desktop-client) — the official APK's GAIA v3 command map (`gaiaV3/m4.json`).
- [jarek102/ohr](https://github.com/jarek102/ohr) — GAIA v3 formal spec and test vectors.
- [Ariazonaa/momentum4-control](https://github.com/Ariazonaa/momentum4-control) — Windows GAIA reference implementation and M5 captured frames.
- [DanSmith888/omarchy-momentum4](https://github.com/DanSmith888/omarchy-momentum4) — verified protocol notes and probe tools.
- [qiu-yongheng/GAIAControl](https://github.com/qiu-yongheng/GAIAControl) — Qualcomm GAIA v2 sample code.
- [FxRayHughes/vivopods-mac](https://github.com/FxRayHughes/vivopods-mac) and [Zhaoyi-ya/TWS-Pods-PC](https://github.com/Zhaoyi-ya/TWS-Pods-PC) — GAIA-over-GATT framing references.
- [kero3009destiny/sennheiser-fw-RE](https://github.com/kero3009destiny/sennheiser-fw-RE) — firmware package decrypt algorithm and key. That repository carries **no license file**; provenance, the sponsor's authorization, and the use caveats are recorded in [docs/fw-format.md](docs/fw-format.md) §4. Do not redistribute decrypted firmware images.
