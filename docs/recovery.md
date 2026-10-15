# Firmware recovery guide (MOMENTUM 5 Wireless)

This guide describes what to do when a firmware update started from this app fails, stalls, or leaves the
headphones in a bad state. Read it before you start an update — not afterwards.

Scope and honesty first: **Momentum5 is an unofficial, community-built client.** It is not affiliated with or
supported by Sennheiser. The firmware-update pipeline (check → download → decrypt → verify → flash) is built
from reverse-engineered, publicly documented behavior (see `docs/fw-format.md`, `docs/PROTOCOL-upgrade.md`,
`docs/SAFETY.md`); the exact update metadata format and VMU sequence were not yet confirmed by a hardware
capture at the time of writing (research lanes R-06 / bucket X-12). Treat the flash stage as experimental and
assume you are doing it at your own risk.

## When to use this guide

- The update stopped mid-transfer ("link lost", "reply timeout", "reconnect timeout") and the headphones do not
  come back or reconnect.
- The update reported a device rejection, an abort, or a version mismatch after the commit.
- The headphones misbehave after an update: no audio, no Bluetooth, do not power on, or are stuck in a
  boot/reconnect loop.

The firmware page itself keeps the last state, saved progress and per-failure guidance on screen (X-13); the
recovery bar on that page links here. An interrupted transfer is normally **resumable**: reconnect the
headphones in the app and choose *Resume update* — the session follows the device's resume point instead of
restarting from zero.

## 1. The 20-second button reset [INFER]

The MOMENTUM 5 Wireless has a hardware reset that clears runtime state without deleting pairings or settings:
hold the power/Bluetooth button combination for about 20 seconds until the headphones signal the reset (for
example with the LED changing or a voice prompt), then release and let them restart.

**The exact button combination and the signal differ per Sennheiser generation — see the official MOMENTUM 5
manual for the reset procedure that applies to your hardware revision.** This document deliberately does not
invent specifics: use the procedure printed in the official manual (the "troubleshooting / reset" section), and
only the official one.

When it helps: a headset stuck in a bad runtime state after a failed update, one that powers on but no longer
connects, or one that keeps dropping the link after an interrupted flash.

When it does not help: a genuinely failed flash (corrupt image committed, or the bootloader never came back).
Then continue with section 2.

## 2. Re-flash with the official Sennheiser Smart Control Plus app

The official app performs the same over-the-air update through Sennheiser's supported path and can re-install
firmware over a half-written image in most cases.

1. Install **Sennheiser Smart Control Plus** (Android or iOS) on a phone.
2. Pair or re-pair the MOMENTUM 5 with that phone (after the button reset they pair like a new device; the
   procedure is in the official manual).
3. Open the app and let it connect. If it offers a firmware update, install it and let it run to completion —
   keep the phone in range, the headphones charged, and do not close the app mid-transfer.
4. If the app cannot connect at all, repeat the button reset once, then retry. If the app still does not see
   the headphones, contact Sennheiser support (see section 4) — the device may need service.

## 3. Collect the frame log for a bug report

Every GAIA frame this app sends or receives is recorded. Two artifacts matter:

- **In-app trace tab**: Diagnostics → *Trace* shows the live frame ring (direction, transport, command, hex).
  Keep it open while reproducing a failure if you want to watch the session live.
- **JSONL frame log**: the tap writes every frame to
  `%LOCALAPPDATA%\Momentum5\logs\frames-<date>.jsonl` (one JSON object per line, rotated at 10 MB, newest five
  files kept). The log of the failed update run is already there — you do not need to enable anything.

For a bug report, attach:

1. The `frames-*.jsonl` files covering the update run (check file timestamps).
2. The app log (same folder family — the in-app diagnostics view can export it) or the lines shown in the
   Diagnostics log view.
3. What you saw on the firmware page: the phase, the failure text, the battery percentage, and the
   installed/published versions.
4. The package URL from the check card, if the failure happened before or during the transfer.

If the failure is reproducible and you can capture the *official* app doing the same update, that capture is
the ground truth this project still needs: follow `docs/capture-procedure.md` (HCI snoop on Android, exported
into the capture fixtures). Do not attach decrypted firmware images to anything — redistribute nothing from the
decrypted package.

## 4. Support boundaries

- **Sennheiser support** is the only channel that can service hardware. A device that does not recover after
  the button reset and an official re-flash needs their service. Be honest about what happened: an update with
  an unofficial tool ran and failed — it is the fastest way to get the right help.
- **This project** can debug the protocol side: file the frame log and the page output (section 3). What this
  project cannot do: recover a bricked device remotely, promise that the flash works before the first hardware
  verification round lands (bucket X-12 / the pending capture), or take responsibility for damage. The typed
  consent phrase on the firmware page is the contract: *I understand this can brick my headphones.*
- **Not in scope**: distributing firmware images, bundling the decryption key into third-party builds, or
  guiding anyone around Sennheiser's update path. The recovery route back to a supported state is always the
  official app.
