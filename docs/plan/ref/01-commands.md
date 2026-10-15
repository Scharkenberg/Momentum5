# ref/01 — Command catalog source table

Single source for P-05…P-11. `S`=vendor 0x0495, `Q`=0x001D. Safety: R=SafeRead, W=SafeWrite, G=Gated write, D=Dangerous (Track X), B=Blocked, ?=Unknown (DevTools probe only). Status: M5/M4/APK/U per ref/00. "sel" = GET request carries selector payload.
PUSH = notification word (cmd|0x0080).

## Core
| Key | V | GET | SET | PUSH | Payload | Safety | Status |
|---|---|---|---|---|---|---|---|
| ApiVersion | Q | 0000 | | | | R | M4 |
| Features | Q/S | 0001 (+0002 next) | | | `[cont][featId,ver]…` | R | M4 |
| Serial | Q | 0003 | | | cstring | R | M4 |
| RegisterNotification | S | | 0007 | | `[featId]` ACK 0107 | W | M5 [CAP] (S:0007 and Q:0007 both ACKed 0107) |
| PrimarySide | S | 0041 | | | u8 | R | U |
| Upgrade tunnel | Q | | 0C00,0C01,0C02,0C03 | 0C80 | | D | U |
| Data transfer/transport | Q | | 0009,000A,000D | | | B | U |
| Panic log | Q | | 0803,0804 | | | B | U |
| VoiceUI | Q | 0600,0602 | 0601 | | | R / B(0601) | U |

## Identity / power
| Key | V | GET | SET | PUSH | Payload | Safety | Status |
|---|---|---|---|---|---|---|---|
| Model | S | 1206 | | | cstring `M5AEBT Black\0` | R | M5 |
| Firmware | S | 1201 | | | 3×u16BE → 6.21.5 | R | M5 |
| FirmwareVersions | S | 1202 | | | ? | R | U |
| HwRevision | S | 1200 | | | ? | R | U |
| LocalName | S | 2802 | 2801 | | UTF-8 (+NUL), ≤16 B on M5 | R / G | M4 |
| BatteryPercent | S | 0603 | | 0683 | u8 (0x1E=30 %) | R | M5 |
| ChargingState | S | 0602 | | 0682 | 0 none,1 charging,2 full | R | M4 |
| AutoOff | S | 0601 (sel `00`) | 0600 `00 <sec u16BE>` | | 0/900/1800/3600 | W | M4 |
| BatteryProtection | S | 060B | 060A | | bool | W | M5 |
| EcoMode | S | 0609 | | | ? | R | U |

## ANC / Transparency (feature 13 / 12)
| Key | GET | SET | PUSH | Payload | Safety | Status |
|---|---|---|---|---|---|---|
| AncEnabled | 1A05 | 1A04 | 1A85 | bool | W | M5 |
| AncModes (AntiWind=1, Comfort=2, Adaptive=3) | 1A01 → `(1,v)(3,v)(2,v)` | 1A00 `[mode,v]` single pair | 1A81 | | W | M5 |
| AncLevel | 1A03 | 1A02 | 1A83 | u8 0–100 | W | M5 |
| TransparencyStatus | 1805 | 1804 | 1885 | bool | W | M5 |
| TransparencyLevel | 1803 | 1802 | 1883 | u8 (0x64 seen) | W | M5 |
| TransparencyAutoPause | 1801 | 1800 | | bool | W | M5 |

## Sound (features 8, 4, 16)
| Key | GET | SET | PUSH | Payload | Safety | Status |
|---|---|---|---|---|---|---|
| EqConfig | 1000 | | | `[n][min i8][max i8][00 00]` | R | M5 |
| EqBand(i) | 1002 sel `[i]` → `[i8]` | 1001 `[i][i8]` | 1082? | dB×10 | W | M5 |
| EqAllGains | 1003 sel `[00]` | | | n×i8 | R | M5 |
| EqBandFreq(i) | 100B sel `[i]` → `[i][u16 Hz]` | | | | R | M5 |
| EqBandQ / FilterType | 100D / 100F sel `[i]` | (X-07 only) | | | R (D to write) | U |
| BassBoost | 1009 | 1008 | | bool | W | M5 |
| SoundMode | 0804 → `[00][m]` | 0803 `[00][m]` | | 0,1,2 (3 read-only) | W | M5 |
| PersonalizationState | 2001 | | | u8 | R | M5 |
| PromptMode | 0802 | 0801 | | 0 off,1 tones,2 tones+voice | W | M5 |
| Sidetone | 0806 | 0805 | | u8 0–5 | W | M4 |
| PromptLanguage / Available / Versions | 0807 / 0808 / 0809 sel | | | | R | M4 |
| Codec | 0800 | | 0880 | enum | R | M4 |
| SampleRate | | | 089A | u32BE (0xAC44=44.1k,0xBB80=48k,0x3E80=16k,0=stopped) | R | community |
| LowLatency | 0818 | 0817 | 0898 | bool | G | U |

## Spatial / toggles / device
| Key | GET | SET | PUSH | Safety | Status |
|---|---|---|---|---|---|
| ImmersiveAudio | 3605 | 3604 | | W | M5 (excl. personalization) |
| HeadTracking | 3603 | 3602 | | W | M5 (needs immersive on) |
| ImmersiveQuery | 3601 → `01 0F` | | | R | M5, meaning U |
| WearDetection | 0401 | 0400 | | W | M5 |
| PhysicalState | 0402 | | 0482 (events 02/03) | R | M4 |
| AutoAnswer | 080B | 080A | | W | M5 |
| SmartPause | 080D | 080C | | W | M5 |
| ComfortCall | 0815 | 0814 | | W | M5 |
| TouchLock | 1607 | 1606 | 1687 | W | M5 (polarity VERIFY) |
| BtCompatibility | 0406 | 0405 | | W | M4 (0 better audio,1 compat) |

## Multipoint / MMI
| Key | GET | SET | PUSH | Payload | Safety | Status |
|---|---|---|---|---|---|---|
| PairedCount | 1400 | | | u8 | R | M4 |
| PairedInfo | 1401 sel `[i]` | | | `[i][prio][conn][name\0]` | R | M4 |
| PairedStatus | 1404 sel `[i]` | | 1484 | | R | M4 |
| OwnIndex / MaxConnections | 1407 / 1409 | | | u8 | R | M4 |
| ConnectPaired | | 1402 `[i]` | | | G | M4 |
| Forget family | | 1403, 1405, 1406 | | semantics U (Aria labels 1403 "Disconnect") | D (until R-02) | U |
| MmiConfig | 1601 sel 2 B, 1605 | 1600 (3 B) | | | R / G | U |
| MmiReset | | 1604 | | | B | U |

## Blocked / Dangerous / Unknown
- **B:** S:`0040` factory reset (→D in X-06), S:`0607`, `0613`, `0685` (hazards, semantics U; → D in X-05 after R-02), S:`0200`,`0201` upgrade (→D in X-12 only).
- **D:** Q:`0C00–0C03`, S:`0630–0634` (community "DFU family", U).
- **?:** S:`0813`,`0819`,`1011`,`1013`,`140B`,`2003`,`2005`,`2007`.

## [CAP 2026-10-15] Observed on M5 fw 6.21.5 (Android 16 HCI snoop, Smart Control Plus 1.6.3)
- Features reply `0495 0101` (`[cont=00][featId,ver]*`): 09/1 device · 08/6 userEq · 1B/1 spatialAudio · 03/2 power · 10/2 personalizedSound · 04/4 genericAudio · 02/1 versions · 0A/4 deviceManagement · 14/1 localName · 0B/2 mmiConfig · 0C/2 transparentHearing · 0D/2 anc · 00/5 core. featId = cmd>>9 (e.g. 0x1A02>>9 = 0D = anc). Q `001D 0101`: 0D/1 0C/1 06/2 04/2 07/1 00/5.
- Connect order (app): S:1201, S:1206, S:0001, Q:0001, Q:000C sel 01..08 (x2, with Q:000D `07 00 00 00 04` between), then registrations `0007`: **S:04, Q:07, S:0A, S:03, S:02, Q:06, S:0D** (each ACK `0107`; Q:0007 accepted). Then initial reads for all features (0401…3606).
- ANC: ACK-only write `1A00 [mode,value]` → push `1A81` = full pair table `01 vv 03 vv 02 vv`. Anti-wind (mode 1): app writes 0 (off) or 2 (auto) → table reads 0 / 1. Adaptive (mode 3): write 0/1 → table mirrors. `1A04 [01]` set + `1B04` ACK, `1A05` read (ANC enabled). `1A02 [lvl]` set, `1A03` read, push `1A83 [lvl]`: single-byte level (seen 00, 15, 22, 32; semantics = ANC/transparency strength slider, U).
- Connections (vendor S, privacy-sensitive, redacted in fixtures): `140E [mac]` (tx), `150D` list, `150E` per-device record `[mac][flags 3B][name…]`, `1491`, `1513/1514`, `2902` local name push/read (cstring).
- Seen read-only, payload samples: `1100` EQ cfg `08 C4 3C 00 00`; `1102/1002` EQ bands; `108B/108D/108F/1091/1093` EQ metadata (band centers, ranges); `2101/2103/2105/2107` personalization coefficients (floats); `3601/3701` `01 0F`; `3606` write-like tx with `01 00 00 00 00 00 00` (U); `0603→0683` battery `1E`.
- Not exercised yet (deferred to G2 readbacks or later capture): Adaptive/Comfort exclusivity, touch-lock polarity (`1606/1607`), sidetone range, `1403/1405/1406`, `0607/0613/0685`, `0040`, `0609`.

## [CAP2 2026-10-15] Guided capture (tier 1), semantics settled on M5 fw 6.21.5 (vendor S unless noted)
Cold-start `0007` order: S:03 S:04 Q:07 S:0A S:02 Q:06 S:0D S:0C S:1B S:0B S:08 S:10 (supersedes the shorter list above).
| Setting | SET → ACK / push | Values |
|---|---|---|
| ANC mode | `1A00 [mode,v]` → `1B00` / table push `1A81`; enabled: SET `1A04 [v]` → `1B04`, GET `1A05`, push `1A85` | Adaptive = `[03,01]`+`1A04 01`; Custom = `[03,00]`+`1A04 01`; Off = `1A04 00` (app also sends `[03,00]`) |
| ANC↔Transparency slider | `1A02 [0..100]` → `1B02`, GET `1A03`, push `1A83` | 0 = full ANC, 50 = neutral, 100 = full transparency (drag sends many intermediate values) |
| Anti-wind | `1A00 [01,v]` | 0 Off, 1 Max, 2 Auto. Table push reads 0/1/1 → **Auto vs Max not readable back; keep app-side** (app DB `anti_wind`='auto') |
| Phone-call transparency | `0805 [v]` → `0905`, push `0886` | 0 Off, 1..4 = Low…High; toggle-on default 2 |
| Transparency auto-pause | `1800 [0/1]` → `1900` | 1 = on |
| Immersive audio | `3604 [0/1]` → `3704`; pushes `3685`,`3683`,`3681 01 1C` | 1 = on |
| Head tracking | `3602 [0/1]` → `3702` (+`3683` push); `3608` start / `3609` stop orientation stream (`368D` pushes, 6 B) | 1 = on |
| Touch lock | `1606 [0/1]` → `1706`, push `1687` | **1 = locked (Controls OFF), 0 = unlocked (Controls ON)** |
| Comfort Calls | `0814 [0/1]` → `0914` | 1 = on (boolean; the only sidetone-like toggle on M5, no range seen) |
| Battery protection | `060A [0/1]` → `070A` | 1 = on |
| Voice prompts | `0801 [v]` → `0901`, push `0882` | 0 Off, 1 Tone only, 2 Voice (golden) |
| Prompt volume | `081C [int8]` → `091C`, push `089D` | seen −20 (0xEC) … +6 (0x06); default ≈ −7 |
M5 has **no ANC "Comfort" mode** (mode 2 always 0, no UI): never send mode 2.
