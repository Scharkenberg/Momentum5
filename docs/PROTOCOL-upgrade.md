# GAIA VM-upgrade / DFU session protocol (X-11 reconstruction)

Reconstruction of the firmware-upgrade session the Sennheiser Smart Control Plus app
runs against the MOMENTUM 5 over GAIA, and the reference for **X-11
(`Momentum5.Client/Firmware/UpgradeSession.cs`)**.

> **No M5 OTA capture exists yet (R-06 BLOCKED-HW).** Every command below is from the
> documented GAIA VMU family; nothing here is verified against an M5 session; X-12
> replay will pin the exact sequence. The qualification experiment (fw-format.md §5)
> is Sennheiser's GAIA feature-version DB listing the Qualcomm `dfu2` feature on
> M5 fw 6.21.5 — i.e. the standard Qualcomm GAIA VM-upgrade/DFU tunnel, vendor
> `0x001D`, is the transport **[INFER + R2]**. Until R-06 lands, this document is a
> strict-fact reconstruction: `UpgradeSession` refuses to send any word its profile
> does not carry (ref/04 rule 3: *never send an upgrade/VM command that was not
> observed in a captured official session*).

Fact tags: **[REF]** = the documented GAIA VM-upgrade family — the Qualcomm
GAIAControl sample app (© Qualcomm Technologies International 2017; `GAIA.java` v2
command list + the `vmupgradelibrary` VM-upgrade manager, cloned to
`/tmp/gaia-repos/GAIAControl` during R2) and the research dossier
(`momentum5-windows-research.md` §5.5, §5.6, §7.6; the M4 APK device model `m4.json`).
**[INFER]** = inference from the above, verify on the first capture. **[CAP-R06]** =
must be pinned by the user's R-06 OTA capture (HCI snoop of Smart Control Plus
running an update; procedure in `docs/capture-procedure.md` / bucket R-06).

## 1. Command table

Transport: GAIA v3 frames over the RFCOMM control link (`FF | 03 | len u16BE |
vendor u16BE | cmd u16BE | payload`), exactly like every other Momentum5 command.

| Word | Vendor | Meaning | Payload | Fact |
|---|---|---|---|---|
| `0x0C00` | Q `0x001D` | VM-upgrade tunnel **connect** — opens the tunnel; after the ACK, VMU packets may flow | empty | word + meaning [REF]; on-M5 use [CAP-R06] |
| `0x0C01` | Q | tunnel **disconnect** — ends the session (after `UPGRADE_COMPLETE_IND`, abort, or error cleanup) | empty | word + meaning [REF]; on-M5 use [CAP-R06] |
| `0x0C02` | Q | tunnel **control** — tunnels one VMU packet (§3 framing) | VMU packet | word + meaning [REF]; on-M5 use [CAP-R06] |
| `0x0C03` | Q | tunnel **data** — alternative VMU-packet carrier for `UPGRADE_DATA` ("Introduces VM Upgrade Protocol data") | VMU packet | word [REF]; whether the M5 app uses it for data packets is [CAP-R06] — the Qualcomm sample sends *all* VMU packets, data included, on the control word [REF] |
| `0x0C80` | Q | tunnel **notification** — device-to-host VMU packets arrive as this push | VMU packet (a leading event/feature byte may precede it, see §7) | word [REF]; payload layout + whether a registration is needed [CAP-R06] |
| `0x0007` | Q | RegisterNotification for the tunnel push (feature id, §7) | `[featureId]` | command [REF]; the M5's accepted feature ids [CAP-R06] |
| `0x1201` | S `0x0495` | firmware version re-read after the upgrade (`Service_SystemReleaseVersion`) | response `3 × u16BE` = major/minor/patch | [REF m4.json] — already bound as `Keys.Firmware` in this repo |
| `0x0302` | S | `UpgradeProgress` GET (app-level progress query) | response semantics unknown | word + notif `0x0282` + error `0x0382` [REF m4.json "upgrade tunnel"]; usage [CAP-R06] — X-11 does **not** send it (progress is computed locally, like the Qualcomm sample [REF]; the word is not in the catalog yet) |
| `0x0630–0x0634` | S | DFU family: `DFU_REQUEST / DFU_BEGIN / DFU_WRITE / DFU_COMMIT / DFU_GET_RESULT` (GAIA v2 DFU command numbers reused under the Sennheiser vendor; "VM-upgrade era commands", community-verified on M4) | `DFU_BEGIN`: "8 or 136 octets depending on the message digest type"; others unknown | words + v2 semantics [REF]; on-M5 payloads [CAP-R06] — **not** modelled by X-11; if R-06 shows the M5 uses this family instead of the tunnel, the profile/doc grow a second command table (X-12 scope) |

v2 lineage: the Qualcomm v2 command list carries the same quad as
`VM_UPGRADE_CONNECT/DISCONNECT/CONTROL/DATA = 0x0640–0x0643` [REF] — the v3 tunnel
words `0x0C00–0x0C03` are the same four-pronged tunnel renumbered into feature 6
**[INFER]**. The v2 DFU command numbers `0x0630–0x0634` survive unchanged under the
Sennheiser vendor **[INFER]**.

Safety classes in this repo's catalog (`RawCommands`): the tunnel words
`0x0C00–0x0C03` are **Dangerous** (DevMode + single-use `ConsentToken` per frame);
`0x0630–0x0634` are Dangerous; `0x0200/0x0201` are Blocked (X-12 only); `0x0302` is
not in the catalog (gate refuses it). `0x1201`/`0x0007` are Read.

## 2. VMU packet framing [REF]

Every control/data payload is a *VMU packet* (Qualcomm `VMUPacket`):

```
 0        1        2         3        length+3
 +--------+--------+--------+ +--------
 | OPCODE |      LENGTH      |  DATA...
 +--------+--------+--------+ +--------
 OPCODE : u8      VM upgrade operation code (§4)
 LENGTH : u16BE   byte length of DATA
 DATA   : LENGTH bytes
```

The GAIA frame payload is exactly these `3 + LENGTH` bytes; the GAIA ACK response
for the carrier word confirms transport only. The v2 sample computed its data-packet
cap as `transport max payload − 3`; BR/EDR GAIA v2 max payload was 254 → ≤250 file
bytes per `UPGRADE_DATA` packet (plus the 1-byte last-packet flag, §4) [REF]. On
GAIA v3 over RFCOMM the frame allows 64 KiB payloads, but the M5 app's actual chunk
size is **[CAP-R06]** (profile `MaxChunkFileBytes`, default 250).

## 3. VMU message set (opcodes + payloads) [REF]

Host→device (sent via the tunnel carrier word), device→host (via the tunnel
notification push and/or the ACK payload — channel pinning **[CAP-R06]**):

| Opcode | Name | Direction | Payload |
|---|---|---|---|
| `0x13` | `UPGRADE_SYNC_REQ` | H→D | `IN_PROGRESS_IDENTIFIER`: 4 bytes = **the last 4 bytes of MD5(image file)** [REF]; length is a profile knob (`SyncIdByteCount`) |
| `0x14` | `UPGRADE_SYNC_CFM` | D→H | `[RESUME_POINT u8][IDENTIFIER 4B][PROTOCOL_VERSION u8]` |
| `0x01` | `UPGRADE_START_REQ` | H→D | none |
| `0x02` | `UPGRADE_START_CFM` | D→H | `[STATUS u8][BATTERY u16BE]`; STATUS `0x00` = ready, `0x09` = app not ready (host retries, see §5) |
| `0x15` | `UPGRADE_START_DATA_REQ` | H→D | none |
| `0x03` | `UPGRADE_DATA_BYTES_REQ` | D→H | `[NUMBER_OF_BYTES u32BE][FILE_START_OFFSET u32BE]` — device **pulls** the next window |
| `0x04` | `UPGRADE_DATA` | H→D | `[LAST_PACKET u8 (01 = last)][file bytes…]` |
| `0x16` | `UPGRADE_IS_VALIDATION_DONE_REQ` | H→D | none — sent when a requested window is exhausted |
| `0x17` | `UPGRADE_IS_VALIDATION_DONE_CFM` | D→H | `[WAITING_TIME u16BE]` — host waits, then re-asks |
| `0x0B` | `UPGRADE_TRANSFER_COMPLETE_IND` | D→H | none — image received and validated |
| `0x0C` | `UPGRADE_TRANSFER_COMPLETE_RES` | H→D | `[ACTION u8]` `0x00` continue / `0x01` abort |
| `0x0E` | `UPGRADE_IN_PROGRESS_RES` | H→D | `[ACTION u8]` — sent after the validation reboot, resume point 3 |
| `0x0F` | `UPGRADE_COMMIT_REQ` | D→H | none — device asks permission to commit |
| `0x10` | `UPGRADE_COMMIT_CFM` | H→D | `[ACTION u8]` `0x00` continue (commit) / `0x01` abort |
| `0x12` | `UPGRADE_COMPLETE_IND` | D→H | none — the last message of a successful upgrade |
| `0x07` | `UPGRADE_ABORT_REQ` | H→D | none |
| `0x08` | `UPGRADE_ABORT_CFM` | D→H | none |
| `0x11` | `UPGRADE_ERROR_WARN_IND` | D→H | `[RETURN_CODE u16BE]` (§6) — errors fatal, warnings informational |
| `0x1F` | `UPGRADE_ERROR_WARN_RES` | H→D | the received RETURN_CODE echoed back |
| `0x18–0x1E` | sync-after-reboot / version / variant / erase-SQIF | — | deprecated or undocumented in the reference; ignored by X-11 |

Hash reporting: the only hash in the reference protocol is the MD5-derived sync
identifier (`UPGRADE_SYNC_REQ`) plus the device-side validation between transfer end
and commit — there is **no per-chunk CRC** in the v2 family [REF]. If R-06 shows a
per-chunk or end-of-transfer checksum word, pin it in the profile (**[CAP-R06]**).

## 4. Session state machine (pseudo-code)

States = `UpgradeSessionState` in X-11; the transition log records every step.

```
Idle ─ RunAsync/ResumeAsync ─▶ Precheck
Precheck: battery > 30 % (injected provider; null/unknown ⇒ refuse);
          image = X-10 PreparedPackage (MD5 already verified); profile sanity.
Precheck ─▶ Connect
Connect:  acquire pipeline; (optionally) register the tunnel notification;
          send 0x0C00 (tunnel connect) — Dangerous, consent token required
          BEFORE the first Dangerous frame leaves the app.
Connect ─▶ AwaitReady
AwaitReady: send SYNC_REQ [MD5-last-4B] ─▶ SYNC_CFM [resume point];
          send START_REQ ─▶ START_CFM: status 0x00 = ready (log battery);
          status 0x09 = not ready ⇒ retry ≤ 5× every 2 s [REF], then fail;
          dispatch on the resume point (0 fresh, 1–4 resumed).
AwaitReady ─ resume 0 ─▶ Start: send START_DATA_REQ ─▶ Transfer
AwaitReady ─ resume 1..4 ─▶ Commit (post-transfer paths below)
Transfer: loop on device pulls:
          DATA_BYTES_REQ [nb u32BE, off u32BE]
            ⇒ send UPGRADE_DATA [last?][≤MaxChunkFileBytes] until the window
              is exhausted (each frame Dangerous + own consent token; GAIA
              ACK awaited; progress reported; optional inter-chunk pacing);
            ⇒ window exhausted: send IS_VALIDATION_DONE_REQ;
          IS_VALIDATION_DONE_CFM [wait u16BE] ⇒ wait, re-ask;
          TRANSFER_COMPLETE_IND ⇒ Commit.
Commit:   send TRANSFER_COMPLETE_RES [continue] — the device reboots to
          validate/apply; the link DROPS (expected).
Commit ─▶ Reconnect: poll the (re-acquired) pipeline; re-open the tunnel;
          reconnect-query = SYNC_REQ → START_CFM → dispatch on the new
          resume point:
            3 IN_PROGRESS ⇒ IN_PROGRESS_RES [continue] ─▶ COMMIT_REQ ─▶
              COMMIT_CFM [continue];
            4 COMMIT      ⇒ COMMIT_CFM [continue];
            1/2/0         ⇒ follow them (validation / re-confirm / re-transfer).
          ─▶ COMPLETE_IND ⇒ End.
End:     send 0x0C01 (tunnel disconnect).
End ─▶ Verify: re-read firmware via 0x1201; compare against the expected
          version (parsed from the image name, `MMM-mmm-ppp` [INFER],
          or supplied by the caller). Mismatch ⇒ fail.
Verify ─▶ Done.
Any ─▶ Failed (typed UpgradeSessionException, §6);
Any ─▶ Aborted (caller cancellation: best-effort ABORT_REQ → ABORT_CFM →
          tunnel disconnect, then rethrow).
Mid-transfer link loss ⇒ Reconnect + UpgradeSessionException(LinkLost):
          the caller persists UpgradeProgress (offset, chunks, state) and
          later calls ResumeAsync — see §5 resume rules.
```

## 5. Reconnect & resume rules

- **Expected reboots**: after `TRANSFER_COMPLETE_RES` and after `COMMIT_CFM` the
  device reboots; the RFCOMM link drops; the app waits for the supervisor to
  re-establish and re-enters at Connect/AwaitReady (the reconnect-query above).
  Reference behavior: after the first reboot the resume point is `IN_PROGRESS (3)`,
  after the second the device signals `UPGRADE_COMPLETE_IND` **[REF]**.
- **Unexpected link loss mid-transfer**: X-11 fails with `LinkLost` and keeps the
  last `UpgradeProgress` for the caller. `ResumeAsync` re-runs Precheck → Connect →
  sync/start, then follows the device:
  1. resume point 1–4 ⇒ continue the post-transfer sequence (no re-transfer);
  2. resume point 0 and the device's first window offset > 0 ⇒ continue
     transferring from that offset;
  3. resume point 0 and window offset 0 with previously-sent bytes > 0 ⇒ the
     device lost everything: profile policy `FollowDevice` (default) re-transfers
     the whole image (idempotent, same MD5 id); `FailOnRestart` refuses with a
     typed error.
  The reference resets the host offset on resume and lets the device's
  `DATA_BYTES_REQ` offset drive continuation **[REF]** — mirrored. The offset field
  is treated as an addition when it fits within the file (reference arithmetic
  **[REF]**); if R-06 shows absolute semantics, the fix is one line + an ERRATA
  note **[CAP-R06]**.

## 6. Error / abort semantics

- GAIA error frame (vendor/carrier word rejected) ⇒ typed `DeviceRejected`
  (reason byte from `GaiaErrorReason`).
- `UPGRADE_ERROR_WARN_IND [return code u16BE]`: host must echo
  `UPGRADE_ERROR_WARN_RES` with the same code first **[REF]**, then:
  fatal codes abort the session (`ERROR_BATTERY_LOW 0x21` ⇒ BatteryLow-style
  typed failure; others ⇒ `DeviceRejected` with the code); the warning
  `WARN_SYNC_ID_IS_DIFFERENT 0x81` means the device is mid-upgrade of a different
  image ⇒ refuse (typed) — full reference code table in GAIAControl
  `ReturnCodes.java` **[REF]**.
- `UPGRADE_ABORT_CFM` without a host abort ⇒ `DeviceAborted` typed failure.
- Host abort (caller cancellation): best-effort `ABORT_REQ` → wait `ABORT_CFM`
  → tunnel disconnect; failures of the best-effort sequence are logged, never
  mask the cancellation. Reference: after abort the device expects the tunnel
  to be closed **[REF]**.
- `UPGRADE_START_CFM` status `0x09` (app not ready): retry ≤ 5 × 2 s **[REF]**,
  then `DeviceNotReady`.
- Timeout while awaiting a VMU reply ⇒ `ReplyTimeout` (resumable — the caller may
  `ResumeAsync`); reconnect window exceeded ⇒ `ReconnectFailed` (resumable).
- Battery: host-side precheck (floor 30 %, strictly greater) + the device's own
  gate (`START_CFM` battery field, `ERROR_BATTERY_LOW`) **[REF]**.

## 7. Progress push words

- Device→host VMU packets (window pulls, validation, commit requests, completion)
  arrive as the tunnel notification `Q:0x0C80` **[REF word]**. The v2 sample
  received them as `EVENT_NOTIFICATION` frames whose payload was
  `[event id][VMU packet]` (event `VMU_PACKET 0x12`), and it had to *register* for
  that event with `RegisterNotification` (vendor Qualcomm) first **[REF]**. For v3
  the profile carries: the push word (0x0C80), whether the payload carries a
  leading event/feature byte (default: yes, stripped **[INFER]** — every indexed
  Sennheiser push in this repo also starts with a selector byte), and the feature
  id to register (default: none sent; likely feature `0x06` (the 0x0Cx family)
  **[INFER]**). All three are **[CAP-R06]**.
- Replies may alternatively ride the ACK payload of the control word (profile
  `RepliesInControlAck`, default false) **[INFER/CAP-R06]**; X-11 accepts either
  channel, first match wins.
- App-level progress words: `S:0x0302` GET + `0x0282` push + `0x0382` error
  (m4.json "UpgradeProgress", "upgrade tunnel" **[REF]**) and the v2 events
  `DFU_STATE 0x10`/`VMU_PACKET 0x12` **[REF]**. X-11 computes progress locally
  from bytes transferred (like the Qualcomm sample **[REF]**) and sends none of
  these; X-12 pins whether the M5 app uses them and can add them to the profile.

## 8. Profile parameters (every [CAP-R06] pin point)

`UpgradeProtocolProfile` (X-11) holds all of these so a capture changes data, not
code. Defaults are the reference values; every default is provisional until R-06.

| Parameter | Default | Pin |
|---|---|---|
| Tunnel words (connect/disconnect/control/data, notification) | 0x0C00/0x0C01/0x0C02/0x0C03/0x0C80 | [REF] words, [CAP-R06] on-M5 use |
| Data packets on the data word (vs control) | false (sample sends all VMU on control) | [CAP-R06] |
| Notification payload carries leading event byte | true | [CAP-R06] |
| Tunnel notification feature id | null (no registration) | [CAP-R06] |
| VMU replies ride the control ACK payload | false | [CAP-R06] |
| VMU opcodes (all of §3) | v2 values | [CAP-R06] |
| Action bytes (continue/abort) | 0x00 / 0x01 | [REF] |
| Sync-id length / hash | 4 = last 4 bytes of MD5(image) | [REF], [CAP-R06] on M5 |
| Chunk size (file bytes per UPGRADE_DATA) | 250 (v2 BR/EDR cap) | [CAP-R06] |
| Inter-chunk pacing | 0 (device-pulled flow control) | [CAP-R06] |
| Command timeout / VMU reply timeout | 10 s / 10 s | [CAP-R06] |
| Commit-complete wait (COMPLETE_IND) | 60 s (flash write) | [INFER] |
| Max validation wait (IS_VALIDATION_DONE_CFM) | 60 s | [INFER] |
| START not-ready retries / delay | 5 × 2 s | [REF] |
| Reconnect poll / timeout | 1 s / 2 min | [INFER] |
| Battery floor | 30 % | house safety rule |
| Resume policy | FollowDevice | §5 |
| Version read word/vendor | S:0x1201 | [REF m4.json] |

Chunk size, control payload bytes (the opcodes and action bytes above), the hash
algorithm (MD5 sync id), and inter-chunk pacing are exactly the parameters that
ONLY a real capture can pin; the profile exists so R-06 can pin them without code
changes. Until then the session enforces "never any unobserved command": a missing
profile word ⇒ typed `UnobservedCommand` failure, never invented bytes.

## 9. X-11 mapping

- `UpgradeProtocolProfile` — §8 (companion file `UpgradeProtocolProfile.cs`).
- `UpgradeSession` — §4/§5/§6 as a pure state machine: states/transitions per the
  enum, battery precheck delegate, single-flight, all frames through
  `CommandPipeline.ExecuteAsync` with `CommandOptions.Consent` (one fresh
  single-use token per Dangerous frame — the gate consumes tokens per send, so the
  injected token factory is called before every Dangerous frame and must never
  return null), progress via `IProgress<UpgradeProgress>`, typed
  `UpgradeSessionException`, image input = X-10's `PreparedPackage`.
- X-12 replay will diff captured TX frame-for-frame against this machine driven by
  a profile edited from the capture; X-13 wires the UI/consent dialog.
