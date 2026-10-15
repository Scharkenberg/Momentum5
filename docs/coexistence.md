# Coexistence — GAIA control channel vs audio and other control clients (V-02)

Status: this is the V-02 **expected-behavior + capture-plan** deliverable. Every "Observed" entry is
**PENDING USER SESSION** — no Windows-side hardware capture of these scenarios exists yet (the only
Windows hardware datapoint to date is the G1/T-03 probe session; R-05 outputs pending). The bucket's
done-when ("each scenario has frames + observed behavior") is hardware-gated and tracked by the
summary table at the end.

Tags: [REF] = fact sourced from a file read for this task (research dossier
`/home/z/my-project/public/momentum5-windows-research.md`, cited by section; code cited by
type/member; `docs/plan/ERRATA.md`; `docs/plan/ref/06-windows-bt.md`; `docs/plan/ref/01-commands.md`;
worklog entries). [INFER] = reasoned from those facts, not directly observed. [PENDING] = unverified,
needs the user's hardware session.

## 1. What coexistence means here

The Windows client owns exactly one thing on the Bluetooth link: the **GAIA control channel** — an
RFCOMM data link connection (DLC) to the M5's GAIA service, UUID `A2129FF3-081B-4C45-8AFE-469D9C4842EC`
[REF: `GaiaUuids.RfcommService`; research dossier §5.2]. Everything else that can be active at the
same time is owned by someone else:

- **Audio streams from this PC** — the Windows audio stack opens and owns the A2DP (media) and
  HFP/HSP (call) connections. The app never opens an audio socket; its only transport is
  `RfcommGaiaTransport` (id `rfcomm-gaia`) [REF: `AppComposition.cs`, `RfcommGaiaTransport.Id`].
- **USB-C audio** — a wired audio path that consumes no Bluetooth ACL link at all
  [REF: research dossier §6.5, "the 2-ACL-link limit doesn't apply to USB"].
- **Another GAIA control client** — the Sennheiser Smart Control Plus phone app, which speaks the
  same GAIA protocol over its own RFCOMM connection to the M5 [REF: research dossier §5.7 rule 4].
- **Another host entirely** — the M5 is a multipoint headset: 2 active connections, 6 stored profiles
  (7th overwrites least-used), profiles A2DP/AVRCP/HFP/HSP/GATT, BLE advertises as `LE-MOMENTUM 5`
  [REF: research dossier §2 spec table]. No LE Audio on the M5 today, so Classic A2DP + RFCOMM GAIA
  remains the model [REF: research dossier §6.10].

"Coexistence" answers one question per scenario: does the RFCOMM GAIA session (and the supervisor's
handling of it) survive, degrade, or compete while each of those is active? Framing facts:

- RFCOMM GAIA and A2DP run on the **same controller and ACL link as separate DLCs**; community
  clients treat A2DP-streaming + GAIA-control as the normal operating mode [REF: research dossier
  §6.5].
- The limits are **headset-side**: max 2 ACL links (multipoint), and a **single GAIA client at a
  time** — the phone's Smart Control app competes for the same channel [REF: research dossier §6.5,
  §5.7 rule 4].
- The M4/M5 family exposes **two RFCOMM services under the GAIA UUID** (M4: channels 1 and 2);
  the Windows client tries up to two service indexes per connect attempt [REF: research dossier §5.2;
  `RfcommGaiaTransport.MaxServicesTried = 2`; ERRATA T-03: "SDP Cached returned 2 services; service
  #0 connected first try"].

## 2. The supervisor model (shared by every scenario)

All behavior below is from `ConnectionSupervisor`
(`Momentum5.Client/Supervisor/ConnectionSupervisor.cs`) unless noted.

### 2.1 Session lifecycle

One supervision loop (`RunAsync`) per target: publish `SessionPhase.Connecting` → `RunSessionAsync`
→ `ConnectAnyAsync` (each `ILinkTransport` in order; the app registers exactly one,
`RfcommGaiaTransport`) → `GaiaLink` (one read loop, single-writer `SendAsync`) → `CommandPipeline`
→ `NotificationHub` → `Handshake.RunAsync` (identity + trust + 0007 registrations) → `ActiveSession`
published → phase `Ready`, or `ReadOnly` with Detail "Firmware not verified: read-only." when trust is
`TrustTier.Unknown`. The loop then blocks on `await link.Closed.WaitAsync(ct)`: the session lives
exactly as long as the link. `RadioOff` and `Unsupported` are terminal-with-wait states at the loop
top.

### 2.2 Link loss, backoff, re-registration

When the link ends (transport error, EOF in `GaiaLink.ReadLoopAsync`, peer close, or dispose),
`GaiaLink.Closed` completes and the `finally` block in `RunSessionAsync` tears the session down:
`_current`/`_currentLink` cleared, `NotificationHub.Detach()`, `HeadsetStateStore.Reset()`, pipeline
and link disposed. `Detach()` "stops decoding and forgets registrations (the headset forgets them
when the link closes)" [REF: `NotificationHub.cs` doc comment] — so **every reconnect starts from
zero registrations**; `Handshake.RunAsync` re-registers the `0007` set
(`hub.RegisterAsync(options.Notifications, …)`) each session [REF: `Handshake.cs`].

The loop then backs off: `NextDelay(attempt)` = `SupervisorOptions.DefaultLadder`
**1 / 2 / 5 / 10 / 30 / 60 s** (capped at the last rung), scaled by ±20% jitter (`JitterFraction`
0.2), published as `SessionPhase.Backoff` with `SessionState{Attempt, RetryDue, Detail ("Link lost."
or the failure message), LastTransportError}`. The attempt counter resets to 0 only when the previous
session reached Ready (`_wasReady`) or after a pause ends; **wake events do not reset it** — a wake
shortens the current wait but the ladder still climbs to the 60 s cap. There is no attempt maximum:
the loop retries until the target changes, the radio is off, the device is unsupported, or the
supervisor is disposed.

Wake sources (each calls `Wake()`, which no-ops while a session is up): radio turned on
(`OnRadioChanged`), target presence (`OnPresenceChanged`, address-scoped), a BLE advertisement
sighting of the target (`OnSighted`, address-scoped), and manual `Connect()`.

`PauseUntilWake()` disposes the current link immediately, parks the loop in `Idle` ("Paused until
the headset wakes.") with **no backoff retries** until a wake source or `Connect()` fires (attempt
resets after the pause). Used by the UI's Disconnect (`StartupController.Disconnect()`) and "forget
app association", and reserved for X-07-style link-dropping commands (X-07 itself was SKIPPED —
0607/0613/0685 semantics unresolved) [REF: `StartupController.cs`; worklog X-07/U-04 entries].

### 2.3 Selection and presence caveats (G1 findings)

- Select the target by **address** (or the model string `M5AEBT*` read over GAIA) — never by name;
  names are user-editable and two M5s can share a display name [REF: ERRATA T-03].
- `BluetoothDevice.ConnectionStatus` and the paired-device list can report connected = False while
  the headset is connected and RFCOMM works — it must not gate connects or be the sole presence
  signal; the connect attempt is the truth (plus `System.Devices.Aep.IsConnected`) [REF: ERRATA
  T-03/T-05].
- Windows service names are `Bluetooth#Bluetooth<localAdapter>-<headsetAddr>#RFCOMM:00000000:{uuid}`
  — passed through unchanged [REF: ERRATA T-03].

## 3. Capture tooling (shared)

Two independent evidence channels exist.

**PC side (always on).** Every GAIA frame is tapped (`SupervisorOptions.Tap` =
`CompositeFrameTap(JsonlFrameTap, InMemoryTap)` in `AppComposition.cs`):

- `JsonlFrameTap` writes JSON lines to `%LOCALAPPDATA%\Momentum5\logs\frames-yyyyMMdd[-NN].jsonl`
  (10 MB rotation, 5 files kept, flush after each batch; record fields: ISO-8601 UTC `ts`, `tx`/`rx`,
  transport id, `hex`, decoded `text`, `length`). The Diagnostics section's Trace page mirrors the
  live in-memory ring; Settings has "Open logs folder" [REF: `JsonlFrameTap.cs`,
  `TraceViewModel.cs`, `SettingsPage.xaml`].
- `m5ctl` (`Momentum5.Cli`): `list` (all paired devices), `probe [--address <hex>]` (connect + read
  model/firmware/battery, prints uppercase-hex TX/RX), `status` (connect + full snapshot), `watch`
  (live pushes until Ctrl+C), `get`/`set` with friendly aliases (`battery`, `anc`, …) [REF:
  `Program.cs`, `StatusCommands.cs`, `GetSet.cs`].
- Note: pure **connect failures produce no frames** — the JSONL only records frames that cross a
  live link. Failure evidence is the supervisor/app log (`SupervisorOptions.Log` → AppLog
  "Session failed: …"; `ConnectAnyAsync` logs "rfcomm-gaia: …"; `RfcommGaiaTransport.Diagnostics`
  logs "SDP cached/final: error=… services=…", "service #i: channel=…") plus the CLI error output
  and exit code [REF: `ConnectionSupervisor.RunAsync`, `RfcommGaiaTransport`].

**Phone side (HCI snoop).** Per `docs/capture-procedure.md`: Developer options → HCI snoop log
Enabled (Filtered off), Bluetooth off/on, reconnect the M5, open the app; label actions with
`.\tools\capture-log.ps1` (marks use the PHONE clock, written to `research/cap/actions-plan.csv`);
pull with `.\tools\capture-pull.ps1` (rooted; newest `btsnoop_hci_*.log` from
`/data/misc/bluetooth/logs` → `research/cap/snoop/`; one-time `.\tools\root-setup.ps1` enables
snoop); export with `python tools/btsnoop_export.py <log> --actions research/cap/actions.csv
--ts-offset-ms <offset> --out Momentum5.Tests/Fixtures/captures/<date>-<name> --fw 6.21.5`.
btsnoop timestamps are LOCAL time; offset = −(UTC offset) in ms (CEST example −7200000). Optional
Wireshark check: filter `btrfcomm` (or `btl2cap.psm == 0x0003`), `btsdp` for UUID/channel. Fixtures
are append-only ground truth; never edit `frames.json` [REF: `docs/capture-procedure.md`,
`tools/capture-log.ps1` / `capture-pull.ps1` / `root-setup.ps1` / `btsnoop_export.py`, ERRATA R-04].

The existing R-04 fixtures (`Momentum5.Tests/Fixtures/captures/2026-10-15-session{1,2,3-tier1}`)
are the comparison baseline: the official app's normal GAIA traffic (handshake, registrations,
commands) without coexistence stress.

## 4. Scenario 1 — GAIA while A2DP streams from this PC

### Expected behavior

- Session-level: unaffected. Phase stays `Ready` (or `ReadOnly`); no `GaiaLink.Closed`, no state
  churn. Windows opening/using the A2DP sink endpoint does not touch the app's RFCOMM `StreamSocket`
  — the audio stack and the GAIA DLC are independent endpoints on the same controller [INFER from
  the DLC separation below].
- Commands keep answering while music plays, within the documented envelope: ~0.4 s typical
  response, firmware busy up to ~2 s after mode switches [REF: research dossier §5.7 rule 4]. The
  pipeline is built for that envelope: single-flight, 5 s default per-command timeout
  (`BindingDefaults.Timeout`), a 2 s busy-grace extension for reads right after a write, one retry
  on read timeouts [REF: `CommandPipeline.cs`, `SettingBinding.cs`].
- If the sample-rate feature is registered, the `089A` push reports 0xAC44 (44.1 k) / 0xBB80 (48 k)
  while streaming and 0 (stopped) on pause/stop — a live in-band signal of the audio state visible
  in the frame tap and `m5ctl watch` [REF: ref/01 `SampleRate` row; whether the default registration
  set includes it: PENDING].
- If the headset reconfigures or drops the ACL (e.g. audio-endpoint arrival/removal), the standard
  loss path runs: "Link lost." → `Backoff` → immediate-retry ladder with attempt reset (the session
  was Ready) → full re-handshake + re-registration on reconnect [REF: §2.2; INFER for the trigger].

### Research basis

- [REF] "RFCOMM GAIA runs fine while A2DP streams" — momentum4-control's normal mode; same
  controller, separate DLC (research dossier §6.5).
- [REF] Headset-side limits are the 2-ACL multipoint cap and GAIA-client competition — not
  audio-vs-control (research dossier §6.5).
- [INFER] The repo's HCI-level tooling targets the Android phone; on the PC the evidence is the
  frame tap + supervisor log, not link-layer frames.

### Observed

PENDING USER SESSION. (The R-04 phone-side fixtures show the official app's GAIA while the phone
interacts, but A2DP-from-this-PC with our client is uncaptured.)

### How to capture

1. Pair the M5 in Windows; start the app (or `m5ctl status`); confirm `Ready`. Note the session
   start from the JSONL's first frame.
2. Set Windows output to the M5; play continuous audio for ~5 minutes. While playing, run a few
   spaced commands (e.g. `m5ctl get battery`, a noise-mode toggle), ~10 s apart.
3. Stop playback; wait ~30 s; note the last push timestamps.
4. Keep: the day's `frames-*.jsonl` (handshake before playback, exchanges during, pushes around
   playback state changes) and a wall-clock note of playback start/stop.
5. Acceptance: no `Backoff` in the phase timeline; commands answered during playback; pushes
   observed at playback transitions.
6. Optional HCI-level rehearsal of the same physics on the phone: stream A2DP from the phone while
   using Smart Control Plus and capture per `docs/capture-procedure.md` — btsnoop shows GAIA RFCOMM
   frames interleaved with A2DP/AVDTP traffic on separate channels.

## 5. Scenario 2 — GAIA while USB-C audio

### Expected behavior

- USB-C audio consumes no Bluetooth ACL link [REF: research dossier §6.5]; the GAIA RFCOMM session
  and USB audio should be fully independent — controlling the M5 while it plays USB-C audio from
  the same PC is a capability no official app offers [REF: research dossier §6.5, §3].
- Open question (tracked as R-05 Q1, gate G4): whether the **M5 keeps its GAIA service reachable
  while USB audio is active** — the firmware could prefer the wired path or suspend the BT stack
  [REF: ref/06 "RFCOMM during USB-C audio = open question (R-05 Q1, gate G4)"; research dossier §3
  caveat "verify M5 keeps RFCOMM alive while USB streaming"; PENDING].
- Supervisor: if the M5 stays reachable, nothing changes — in both orders (connect-then-USB and
  USB-then-connect) the session reaches `Ready`. If the M5 is unreachable in USB mode,
  `ConnectAnyAsync` fails with a mapped `LinkException` (e.g. `BtError.ElementNotFound` /
  `DeviceNotConnected` / `Timeout`: SDP empty or socket connect refused within the 10 s
  `connectTimeout`), the loop enters `Backoff` and climbs the ladder; the M5 restoring its radio
  (or cable unplug) triggers an immediate retry via the wake sources [REF:
  `RfcommGaiaTransport`, §2.2].
- If a live session existed before the M5 entered USB mode and the link drops, the standard loss
  path runs ("Link lost." → backoff → re-handshake with fresh registrations).

### Research basis

- [REF] Control link independent of the audio path; the 2-ACL cap does not apply to USB (research
  dossier §6.5, §3).
- [PENDING] M5 RFCOMM reachability during USB streaming — exactly R-05 Q1 (G4).

### Observed

PENDING USER SESSION (R-05 Q1 is the tracking item).

### How to capture

1. Order A (session first): connect the app, confirm `Ready`, note the time; plug the M5 in via
   USB-C, select its USB endpoint in Windows, play audio 2–3 minutes, run a few commands during
   playback.
2. Order B (USB first): with USB audio already playing, start the app (auto-connect) or
   `m5ctl probe --address <M5>`; record whether SDP resolves (`Diagnostics`: "SDP cached/final:
   error=… services=…") and which service index wins.
3. Keep: the day's `frames-*.jsonl` for both orders, the app log lines for any failed connect
   (BtError + HRESULT), wall-clock notes of plug/unplug and playback windows.
4. Acceptance: per order, either (a) frames flow during USB playback — Q1 answered "yes" — or
   (b) a typed connect failure with a consistent BtError — record it in ERRATA for R-05.

## 6. Scenario 3 — GAIA while the phone app holds the channel

### Expected behavior

- The GAIA service accepts **one control client at a time**; the phone's Smart Control Plus competes
  for the same channel [REF: research dossier §5.7 rule 4]. When our client connects while the phone
  app holds the channel, the expected encounter order is [INFER]:
  - The connect on the busy service fails — refused or "busy" (the documented community mapping for
    a busy channel is `0x80070490` ElementNotFound, "service/channel unavailable or busy";
    `RfcommGaiaTransport` maps it to `BtError.ElementNotFound`) — and, because two services exist
    under the UUID, the transport then tries service #1 (`MaxServicesTried = 2`) [REF: research
    dossier §6.3 + §5.2; `RfcommGaiaTransport.ConnectToAnyAsync`].
  - What the second service admits is unverified: a concurrent second GAIA client, a fallback that
    also refuses, or a takeover that evicts the phone app [PENDING — R-05 Q3 (channel index 0 vs 1)
    and Q6 (behavior while the phone app holds GAIA)].
- Supervisor steady state while the channel is busy: `Connecting` → failure → `Backoff` climbing
  1→2→5→10→30→60 s (±20% jitter) indefinitely, `LastTransportError` carrying the mapped `BtError`;
  **immediate extra retries** whenever a wake fires (radio on, target presence, advertisement
  sighting, manual `Connect()`) — wakes shorten the wait but do not reset the ladder (only a Ready
  session or a pause end resets the attempt counter). If the M5's BLE advertisements remain visible
  to the PC, sightings produce frequent-but-still-laddered attempts [REF: §2.2; advertisement
  visibility: PENDING].
- If our link is accepted (either service), it is a full normal session: handshake, registrations,
  pushes. If the phone app then (re)connects and the headset evicts us, `GaiaLink.Closed` completes
  and the standard loss path runs; on the next successful reconnect the whole 0007 registration set
  is re-sent (the headset forgot it at link close) [REF: §2.2].
- Reverse order (we hold GAIA, the phone app connects): symmetric open question — the phone app's
  connect may fail, queue, or take over; only the phone-side btsnoop shows this directly [PENDING].

### Research basis

- [REF] Single GAIA client at a time; the phone app competes for the same channel (research dossier
  §5.7 rule 4, §6.5).
- [REF] Two RFCOMM services under the GAIA UUID (M4 channels 1 & 2; index-fallback is standing
  advice); busy-channel handling maps to 0x80070490 (research dossier §5.2, §6.3).
- [REF] G1/T-03: Windows SDP (Cached) returned 2 services; service #0 connected first try — with no
  other client holding the channel.
- [INFER] The concrete busy-channel behavior on M5 fw 6.21.5 (refuse / second service / eviction /
  both live) is the core unknown this scenario's capture resolves.

### Observed

PENDING USER SESSION (R-05 Q6 is the tracking item).

### How to capture

1. Phone: enable HCI snoop, restart Bluetooth, connect the M5, open Smart Control Plus and let it
   hold GAIA (note foreground vs backgrounded).
2. Start `.\tools\capture-log.ps1` and label the steps (e.g. `pc-attempt-1`, `pc-attempt-2`,
   `phone-app-reconnect`) — mark before acting, ~3 s between steps.
3. PC: start the app with auto-connect (or `m5ctl probe --address <M5>`) and let it run at least
   5 minutes so the backoff ladder and wake-driven retries are visible; record what the app
   log / CLI prints (BtError, HRESULT) and whether any link succeeds.
4. If a link succeeds: run commands, watch pushes (`m5ctl watch` or the Trace tab), then open or
   foreground the phone app and see who survives.
5. Reverse order: with the PC session `Ready`, open Smart Control Plus on the phone; watch both the
   PC tap (does `link.Closed` fire?) and the phone snoop (does the phone's connect get refused?).
6. Pull and export: `capture-pull.ps1`, `btsnoop_export.py` with the actions CSV; in Wireshark check
   `btsdp` (which channels exist) and `btrfcomm` (who sends, who is refused/evicted — an RFCOMM/L2CAP
   disconnect is visible even when the GAIA frames are not ours).
7. Acceptance: a typed answer to "what does the M5 do with a second GAIA client" (refuse / second
   service / eviction / both live), evidenced by frames plus the PC phase timeline.

## 7. Scenario 4 — GAIA during an HFP call

### Expected behavior

- HFP call audio is a separate profile (SCO/eSCO) negotiated by the call's host; the GAIA RFCOMM DLC
  is not the audio path, so the session is expected to survive call setup and teardown [INFER from
  DLC/profile separation, research dossier §6.5].
- Sub-case (a) — call from the phone while the PC holds GAIA: the phone is the second multipoint
  host; the M5 switches its audio to HFP for the call. The PC's session should stay `Ready`;
  commands still answer, subject to the ~2 s busy window after profile switches [REF: research
  dossier §5.7 rule 4 for the busy window; INFER for session survival].
- Sub-case (b) — call from this PC (Windows opens the Hands-Free service): Windows switches the M5
  endpoint from A2DP to hands-free; the app's RFCOMM socket is unaffected. The Hands-Free AG
  devnode appears, and its battery property (`{104EA319-6E3C-4A49-AB25-2E9B1A11E929},2`, read by
  `HfpBatteryReader` through `PnpObject`) is the documented fallback whose values match GAIA battery
  when the GAIA link is down [REF: ref/06 coexistence facts; ERRATA T-07; `HfpBatteryReader`].
- In-band signals: if registered, the sample-rate push `089A` reports 0x3E80 (16 k) during a call
  versus 44.1/48 k for media — the tap itself records the HFP↔A2DP transitions [REF: ref/01
  `SampleRate` row; registration mapping: PENDING].
- Supervisor: no phase change expected. If call setup reconfigures or drops the ACL, the standard
  loss path runs and auto-reconnect lands back in `Ready` — the user-visible worst case is a brief
  `Backoff`, not a stall [INFER; REF for the loss path].

### Research basis

- [REF] HFP battery devnode fallback matching GAIA values (ref/06; research dossier §6.4; ERRATA
  T-07).
- [REF] Profile auto-switching on call state is normal headset behavior (research dossier §3,
  "auto-switching profiles on call state is nice UX").
- [INFER] GAIA-over-RFCOMM surviving SCO setup — reasoned from separate-DLC coexistence; no direct
  capture yet.

### Observed

PENDING USER SESSION.

### How to capture

1. Sub-case (a): with the PC session `Ready` and the phone paired to the M5 (second multipoint
   host), place a call from the phone; run a command mid-call (e.g. `m5ctl get battery`); end the
   call. Phone side: snoop + labels (`call-start`, `mid-call-command`, `call-end`) so the SCO setup
   and any GAIA frames interleave visibly; PC side: keep the JSONL running throughout.
2. Sub-case (b): make the M5 the Windows output, start a call from a PC softphone, watch the tap
   through setup/call/teardown. Afterwards exercise the fallback: Disconnect (→ `PauseUntilWake`,
   GAIA down) and compare the Hands-Free devnode battery value (fallback path) with the last GAIA
   reading.
3. Keep: the JSONL spanning pre-call/call/post-call with wall-clock call boundaries; for (a) the
   phone btsnoop + actions CSV.
4. Acceptance: the phase never leaves `Ready` (or the exact loss/reconnect timeline is recorded);
   commands/pushes observed during the call; `089A` transitions if registered.

## 8. Scenario 5 — Two M5s paired to this PC

### Expected behavior

- The supervisor is **single-target by design**: one `Target` (`SetTarget`), one session per connect
  cycle. A second paired M5 is just another device; it stays untouched until the user selects it
  [REF: `ConnectionSupervisor`].
- Both M5s can be connected to the PC simultaneously (one as the audio endpoint and one under GAIA
  control, or two GAIA clients in two processes): each is a separate device with its own ACL link
  and its own GAIA service — the multipoint/2-ACL cap is per headset, so there is no headset-side
  shared resource between two distinct headsets [INFER from research dossier §6.5].
- Selection safety with two units: pick by address. Names are user-editable and both units may
  display as "MOMENTUM 5"; `ClassicPresence` matches the target address OR a name filter, so the
  name filter can fire for the wrong unit — but the supervisor's wake handlers are address-scoped
  (`OnPresenceChanged`/`OnSighted` compare `Target.Address`), so a sighting of the other M5 never
  triggers a connect attempt to it [REF: ERRATA T-03/T-05; `ConnectionSupervisor`].
- Switching units while connected: `SetTarget(other)` stores the new target and calls `Connect()` →
  `Wake()` — which **no-ops while the current session is `Ready`/`ReadOnly`**. The live session to
  the first M5 keeps running; the new target takes effect at the next connect cycle (after the
  current link drops or the user disconnects). Deterministic switch: Disconnect (`PauseUntilWake`,
  drops the link, parks in `Idle`) then Connect the other unit [REF: `ConnectionSupervisor.SetTarget`
  / `Wake` / `PauseUntilWake`; `StartupController.Connect`/`Disconnect`].
- The M5's own multipoint GAIA keys (`1400` count, `1401` info, `1404` status, `1407` own index,
  `1409` max — M4-modeled; the M5 capture family is `140E`/`150D`/`150E`) describe **the headset's
  paired hosts** (e.g. this PC + the phone), not two M5s. Those payloads are UNVERIFIED on the M5 —
  read-only until verified [REF: ref/01 Multipoint table; ERRATA P-08/P-09].

### Research basis

- [REF] Single-target supervisor; address-scoped wakes; wake no-op while Ready
  (`ConnectionSupervisor`).
- [REF] `m5ctl list` shows all paired devices precisely because names are unreliable (ERRATA K-01).
- [INFER] No cross-device interference: each M5 is an independent peer.

### Observed

PENDING USER SESSION.

### How to capture

1. Pair both M5s in Windows. `m5ctl list` — record both addresses (names may be identical).
2. Connect the app to unit A (`Ready`), run commands; in a second console run
   `m5ctl probe --address <B>` (its own process and supervisor) — A's frames land in the app's
   JSONL, B's exchange prints in the CLI.
3. Exercise wake scoping: power-cycle or reconnect unit B and verify the app's session to A shows
   no `Connecting` churn (nothing new is triggered by B).
4. Switch: with A connected, Connect B in the app; observe that A's session persists (no immediate
   drop); then Disconnect, Connect B, and confirm B's handshake in the JSONL.
5. Keep: the `m5ctl list` output, the day's JSONL, wall-clock notes of which unit was live, and
   (optional) both units' `probe` outputs.
6. Acceptance: two concurrent GAIA sessions in two processes without interference; address-scoped
   wake behavior confirmed; the deterministic switch path recorded.

## 9. Summary

| # | Scenario | Expected behavior (summary) | Supervisor behavior | Observed |
|---|----------|------------------------------|---------------------|----------|
| 1 | GAIA while A2DP from this PC | Unaffected: same ACL, separate DLC; commands answer within the busy envelope; `089A` push transitions if registered | Stays `Ready`; standard loss/backoff only if the ACL reconfigures | PENDING USER SESSION |
| 2 | GAIA while USB-C audio | Independent paths (USB uses no ACL); M5 reachability during USB audio unverified (R-05 Q1, gate G4) | `Ready` if reachable; else mapped `LinkException` → backoff ladder, immediate retry on wake when the radio returns | PENDING USER SESSION |
| 3 | Phone app holds GAIA | One GAIA client at a time; second connect refused, second-service behavior unknown (R-05 Q3/Q6) | `Connecting` → failure → backoff to the 60 s cap; immediate retries on wakes without attempt reset; full re-registration after any successful reconnect | PENDING USER SESSION |
| 4 | During an HFP call | GAIA DLC survives SCO/profile switching; Hands-Free devnode battery fallback matches GAIA | Stays `Ready`; worst case a brief `Backoff` if the link reconfigures | PENDING USER SESSION |
| 5 | Two M5s paired | Independent devices, concurrent sessions possible; select by address; wakes are address-scoped | One target per cycle; a target switch takes effect at the next cycle; `PauseUntilWake` + Connect for a deterministic switch | PENDING USER SESSION |

Honest status: this document is the V-02 expected-behavior + per-scenario capture-plan deliverable.
The observed column requires the user's hardware session; results land as exported fixtures
(btsnoop → `Momentum5.Tests/Fixtures/captures/…`), updates to each scenario's Observed entry, and
ERRATA notes answering R-05 Q1/Q3/Q6.
