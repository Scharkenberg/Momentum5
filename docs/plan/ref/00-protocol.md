# ref/00 — GAIA v3 protocol essentials (M5AEBT)

Evidence tags: **[M5]** = seen in hardware capture on M5AEBT fw 6.21.5 (Ariazonaa/momentum4-control `tests/CapturedPackets/M5`, MIT; checked 2026-10-14). **[M4]** = verified by community on M4 only. **[APK]** = from official app model (via dossier). **[U]** = unverified.

## 1. Frame (RFCOMM, ByteStream)
```
FF | 03 | LEN u16BE | VENDOR u16BE | COMMAND u16BE | PAYLOAD[LEN]
```
- `LEN` = payload bytes only. Frame size = 8 + LEN. No checksum.
- Accept VER 03 and 04 structurally. Resync on next `FF` on malformed length (> 1024) or garbage.
- Datagram transports (GATT) carry bare `VENDOR+COMMAND+PAYLOAD`; `GaiaLink` adds/strips the 4-byte header (`FF 03 LEN`) — transports never interpret GAIA.

## 2. Command word
`cmd = (feature<<9) | (type<<7) | number`; type 0=command, 1=notification, 2=response, 3=error.
- response = cmd|0x0100 · error = cmd|0x0180 (payload: 1 reason byte) · notification = cmd|0x0080.
- A sendable catalog entry MUST have type bits == 0 (catalog invariant test). Words like `0x0302`, `0x0685` are response/notification-typed → never sendable.
- Catalog key = `(vendor, cmdBase)`. `0x0600` under 0x0495 = auto-off SET; under 0x001D = VoiceUI GET.

## 3. Vendors
`0x0495` Sennheiser (all settings **and** RegisterNotification `0x0007` [CAP2]), `0x001D` Qualcomm core (also accepts `0007`; api version, features, serial, VM-upgrade).
Both vendors answer `GetSupportedFeatures 0x0001` **[M4]**. Sennheiser features 0–31 only; never probe 32–127.

## 4. Session rules (corrected vs previous plan)
1. SET ACK = response with empty payload, no state → always re-GET.
2. One command in flight. **Default timeout 5 s** (Aria uses 5 s). Per-command overrides: battery `0x0603` 9 s, firmware `0x1201` 10 s, paired-count `0x1400` 9 s. Retry **once for reads only**; never auto-retry writes.
3. After mode-changing writes (ANC/TH/sound mode/EQ) allow +2 s grace before declaring timeout.
4. Register only needed features via `0x0007 [featureId]`. Official app cold start (capture, fw 6.21.5): S:03 S:04 Q:07 S:0A S:02 Q:06 S:0D S:0C S:1B S:0B S:08 S:10 (S = vendor 0x0495, Q = 0x001D; ids in ref/01 [CAP2]). Then rely on pushes; no polling loops.
5. ANC group and Transparency group are **coupled**: after `1804=1` the `1A03` read-back changed `00→64` **[M5]**. After any ANC/TH write re-read both groups.
6. Transparency resets to OFF at headset power-on → read fresh each session.
7. Phone Smart Control Plus competes for the same GAIA channel → expect refusal; supervisor backs off.
8. Two RFCOMM services under UUID `A2129FF3-081B-4C45-8AFE-469D9C4842EC`: try index 0, then 1.
9. Error reasons: `00` feature unsupported, `01` operation unsupported, `05` malformed payload, `83` index out of range; keep raw byte for unknown.

## 5. Handshake order
`Q:0000` api version → `S:0001`(+`0002` if continuation=1) features → `S:1206` model (gate `M5AEBT*`) → `S:1201` firmware (profile/trust) → `Q:0003` serial → `S:1200` hw rev → register notifications → group refresh → SessionReady.

## 6. Semantics learned from captures **[M5]**
- ANC mode write `1A00` takes ONE pair `[mode,value]`; the 6-byte table write is **rejected** on M5. Read `1A01` returns 3 pairs in order `(1,antiWind)(3,adaptive)(2,comfort)`; e.g. `01 00 03 00 02 00`. Anti-wind write values: 0 off, 1 Max, 2 Auto; table reads 0/1/1, so Auto vs Max is app-side state [CAP2].
- Adaptive|Custom mode: `1A00 [03,01]` = Adaptive, `[03,00]` = Custom, each plus `1A04 01`; the M5 has NO Comfort mode [CAP2].
- EQ: `1001 [band][i8 dB×10]`; `1002 [band]`→`[i8]` (gain only); `1003 [00]`→n×i8; `100B [band]`→`[band][u16 Hz]`. Config `1000`→`08 C4 3C 00 00` = 8 bands, −6.0…+6.0 dB. Centers read live: 90/325/1500/2500/4500/8000/13000/16000 Hz.
- Sound mode `0803 [00][m]`, read `0804`→`[00][m]`; m 0 off,1 graphic EQ,2 speech clarity (3 personalization = read-only). `2001`→u8 personalization state.
- Prompt mode `0801 [0|1|2]` verified on M5 (so `0x0801/0x0805` are NOT hazards). `0817` low-latency SET unverified → Gated.
- Touch lock `1606/1607` value polarity: 1 = locked (Controls OFF), 0 = unlocked [CAP2].
- `0x3601` returns `01 0F` (meaning unknown). Immersive `3604/3605`, head tracking `3602/3603`, battery protection `060A/060B` bool, write→read-back verified.
