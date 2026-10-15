# Safety

Condensed from the plan's safety doctrine
[docs/plan/ref/04-safety.md](plan/ref/04-safety.md) and matched to the
implemented code (`SafetyGate`, `ConsentService`, `M5Profile`, the Advanced
pages). Companion docs: [README](../README.md) · [ARCHITECTURE](ARCHITECTURE.md)
· [CONTRIBUTING](CONTRIBUTING.md).

Doctrine: the project implements **every** feature, including dangerous ones.
Safety = typed, logged, gated — not hidden. A destructive command is a
deliberate, confirmed, audited action; it is never one accidental click away.

## Safety classes

Every catalog entry carries a `SafetyClass` (Core):

| Class | Rule |
|---|---|
| Read | Always allowed (any trust tier). GET and push notifications are always Read. |
| Write | Allowed when firmware trust ≥ Compatible; read-after-write (verify) is mandatory. |
| Gated | Like Write, plus an explicit user action in the UI and a per-command capability flag; on the M5 currently also requires the Verified tier (or DevMode). |
| Dangerous | Track X only: DevMode on + a single-use consent token + pre/post frame logging. |
| Blocked | Refused outright. Reachable only from the DevTools console with a per-command, per-session override. |
| Unknown | GET-style probing from DevTools only — never a write. |

Enforcement (implemented in `Client.SafetyGate`, called from the pipeline):

1. Every outgoing frame passes `ISafetyGate` in the pipeline — there is no
   bypass API.
2. A sendable command must have command-type bits (`response`, `error`, and
   `notification` words are never sent — e.g. `0x0685`).
3. Never send an upgrade/VM command that was not observed in a captured
   official session.
4. Every Dangerous execution logs intent before the write and the outcome
   after it; destructive actions show a recovery info bar.
5. UI pages other than Advanced/DevTools cannot construct Dangerous/Blocked
   requests — an architecture test scans the App project for catalog
   Dangerous/Blocked usage outside `Pages/Advanced/`.

## Device and firmware gates

**Model gate.** After handshake command `S:1206`, the model string must start
with `M5AEBT`, otherwise the app disconnects with "unsupported device". No
generic Sennheiser mode exists.

**Firmware trust tiers** (`M5Profile`):

| Tier | Condition | Effect |
|---|---|---|
| Verified | Exactly a version in `M5Profile.Verified` — currently **6.21.5**, the hardware-captured firmware | All Write and Gated features allowed. |
| Compatible | Same major.minor, greater patch | Write allowed; Gated needs DevMode; banner "firmware newer than verified". |
| Unknown | Anything else | Read-only plus a banner; DevMode can override per session. |

After the verification matrix passes on a new firmware, one line is added to
`M5Profile.Verified` — that is the whole upgrade procedure.

## Consent tokens (Dangerous commands)

The consent flow (`ConsentDialogService`, X-02) before anything Dangerous is
sent:

1. A full-window dialog shows the **consequence in plain language** and the
   exact command it binds to (e.g. "Sennheiser:0040").
2. The user must type a **confirmation phrase** — the target device name or
   command name — exactly (case-sensitive match).
3. The confirm button stays locked for a **5-second cooldown** so the
   consequence text is actually readable.
4. Confirming issues a `ConsentToken` from `ConsentService`: **single-use**,
   bound to one `(vendor, command)`, with a **5-minute default expiry**,
   consumed by the safety gate at send time. Reuse, expiry, or a command
   mismatch are all denied.
5. The consequence text is hashed (SHA-256, first 16 hex chars) into the
   consent audit trail, so what the user confirmed and what the gate later
   allow cannot drift apart. All consent decisions of a session are recorded.

**DevTools overrides:** `OverrideToken` is a per-command, per-session,
no-lifetime token used only for Blocked commands from the DevTools console —
never through normal pages.

**DevMode** is unlocked in the Settings page and wired to the supervisor. It
is required for the Advanced hub, all Dangerous commands, Blocked overrides,
Unknown probing, and write overrides on Unknown-trust firmware. Leave it off
unless you know why you need it.

## Firmware update staging

The firmware flow is split into stages, each with its own gate
(X-09..X-13; see [docs/fw-format.md](fw-format.md) for the package format):

| Stage | Bucket | Gate |
|---|---|---|
| 0 · Version check | X-09 | Read-only HTTP check against the update metadata; no transfer code. |
| 1 · Package prepare | X-10 | Download, AES-256-CTR decrypt, ZIP validation, and an **MD5 gate**: the decrypted image's MD5 must match the package digest or the image is rejected — it never reaches the upgrade tunnel. |
| 2 · Upgrade transfer | X-11 (pure state machine) | Battery **> 30 %** precheck; single-flight; every frame sent with Dangerous consent; implements exactly the captured official sequence — resumable, abortable. |
| 2b · Replay verification | X-12 | The state machine must first replay a real captured OTA session (R-06). **BLOCKED-HW** until the user captures one; until then no upgrade command is ever sent to a real headset. |
| 3 · UI + recovery | X-13 | Staged UI with typed consent ("I understand this can brick my headphones"), battery gate, progress, and a recovery page (20-second button reset, re-flash via the official app). |

## Known-dangerous commands

The explicit list (S = Sennheiser vendor `0x0495`, Q = Qualcomm `0x001D`).
Words with unknown semantics stay Blocked until captures resolve them.

| Command | Class | What it does / why it is restricted |
|---|---|---|
| `S:0040` factory reset | Dangerous (X-08) | Full factory reset of the headset. Typed `RESET` + checkbox + consent token; afterwards the app clears device-bound local state (presets are kept) and shows a re-pairing guide. Implemented and simulated; the hardware round (one controlled reset on the dev unit) is still pending the user. |
| `S:1403` forget pairing | Dangerous (X-06) | Deletes one pairing-table entry (payload = the entry index; the table is refreshed by the `1484` push). Consent phrase is the target device's name; forgetting the app's own connection index requires a second, typed-`FORGET` warning. Re-pairing steps shown after. Implemented and simulated; hardware round pending. |
| `S:1405` / `S:1406` | Dangerous (raw) | Companion forget-family commands with unresolved semantics (R-02 found no numeric ids in the APK); kept as Dangerous raw rows, not exposed in the UI. |
| `S:1604` MMI reset | Blocked | Resets the touch/button configuration; semantics unknown. |
| `S:0607` / `S:0613` hazard | Blocked | Link-reset/offline family with unknown semantics (R-02). X-07 (exposing them) was SKIPPED for exactly this reason; they remain Blocked placeholders reachable only via a DevTools override. |
| `S:0685` hazard | Blocked | Notification-typed word — can never be sent under the sendability rule. |
| `Q:0C00..0C03` upgrade tunnels | Dangerous | The GAIA VM-upgrade/DFU tunnel. Only the X-11 state machine may use them, only after X-12 replay passes. |
| `S:0630..0634` DFU family | Dangerous | Community-documented DFU commands; same restriction as the upgrade tunnels. |
| `Q:0009/000A/000D` data transfer, `Q:0803/0804` panic log, `S:0200/0201` firmware upgrade, `Q:0601` voice UI set | Blocked | Transfer/log/debug words with no sanctioned use in this client. |

`m5ctl` mirrors the gate: exit code **3** means "refused by safety policy"
(Gated/Dangerous/Blocked keys, or read-only firmware) — see
[docs/cli.md](cli.md).

## Evidence status

Anything marked "hardware round pending" above has been implemented and
verified against the FakeM5 simulator only; the one-shot hardware executions
(forget, factory reset, full firmware update) are deliberately left as user
actions to be logged in the verification matrix
([docs/verification-matrix.md](verification-matrix.md)). Protocol-level facts
carry evidence tags in [docs/plan/ref/00-protocol.md](plan/ref/00-protocol.md);
contradictions found later are recorded in
[docs/plan/ERRATA.md](plan/ERRATA.md), and per-bucket build status is in
[docs/plan/PROGRESS.md](plan/PROGRESS.md).
