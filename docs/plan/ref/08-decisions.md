# ref/08 — Decisions, corrections, gates, unknowns

## Corrections vs previous plan (all verified 2026-10-14)
| # | Old plan | Now | Evidence |
|---|---|---|---|
| 1 | ANC mode: 6-byte table read-modify-write | single `[mode,value]` pair write; table rejected on M5 | Aria hardware.json `limitations` + exchanges |
| 2 | Golden `1A01` described as "adaptive on" | all three values are 0 in that sample; order is (1,3,2) | frame bytes |
| 3 | `0x0007` registration under Qualcomm 0x001D by default | Sennheiser 0x0495 (Aria: Verified); 0x001D unverified | Aria CommandCatalog |
| 4 | Default response timeout 600 ms | 5 s default; battery 9 s, fw 10 s, paired count 9 s | Aria CommandCatalog/Queue |
| 5 | `0x0801/0x0805/0x0817` "act on device index" hazard | 0801/0805 verified writes (0801 on M5); 0817 unverified → Gated | Aria catalog + M5 capture |
| 6 | EQ `1002` response `[band][gain]` | response is `[gain]` only | M5 capture |
| 7 | EQ centers hard-coded; `100B` "X-07 only" | `100B` is a verified read → read centers live | M5 capture |
| 8 | Exact-match firmware 6.21.5 gate | trust tiers (Verified/Compatible/Unknown) | ref/04 |
| 9 | Replace BuildTools preview with 28000.x stable | keep 29648 preview: it is the latest (constraint #7) | NuGet |
| 10 | System.CommandLine rc.1, xunit 4.0.1 | rc.2.26504.105, 4.0.2; MTP runner | NuGet |
| 11 | Events-based `ILinkTransport` + TransportHub + `Wait(0)` lifecycle hacks | connection-oriented transport; supervisor picks transports | ref/03 |
| 12 | 9 per-domain facades + per-feature VMs | `SettingKey<T>` + bindings + `SettingViewModel<T>` | ref/03 |
| 13 | Waves with unbounded parallelism | one linear queue; max 2 lanes (L1 code, L2 research) | user constraint #9 |
| 14 | No Tests project bucket; `[Obsolete]` migration dance | A-03 creates Tests; rename done in place (A-05) | repo audit |
| 15 | Hardware contact at ~bucket 25 | RFCOMM spike (T-03, gate G1) at ~bucket 17 | risk ordering |
| 16 | Immersive/MMI/personalization/unknown reads unplanned | MMI config + personalization state + probe reads added (X-08, R-05) | Aria catalog |

## Hardware gates (stop-and-record points; user on Windows 11 26H2 + M5)
- **G1** (after T-03): `m5ctl probe` returns `M5AEBT Black` over RFCOMM. Fail → do T-04 (Winsock).
- **G2** (after K-03): `selftest` full handshake + notification registration; record vendor accepted for `0x0007`, supported features, ANC read/write behavior.
- **G3** (R-05 Q2): M5 GATT table; GAIA-over-GATT present? No → T-08 stays SKIPPED.
- **G4** (R-05 Q1): GAIA alive during USB-C audio? Yes → unlock "control over USB" badge.

## Errata mechanism
Any research finding that contradicts `ref/` → edit the ref table in place, append a line to `docs/plan/ERRATA.md` (`date · bucket · what changed`), then run the owning P-bucket's tests. No separate fix buckets.

## Unknowns register → resolver
APK model exists? R-01 · vendor of Q:0007 R-05 · ANC adaptive/comfort exclusivity R-04 · `3601` meaning R-05 · GAIA-over-GATT R-05 · USB coexistence R-05 · forget/hazard semantics R-02 · sidetone range/touch polarity V-01 · OTA sequence R-06 · share-format R-03 · MMI format R-03/X-08 · Windows `RequestAccessAsync` need in non-UI CLI G1.
