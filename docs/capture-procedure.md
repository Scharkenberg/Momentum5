# HCI capture procedure (Android 16, Smart Control Plus ↔ M5)

Prereqs: phone with USB/wireless adb, M5 paired to the phone, Smart Control Plus installed and connected.

1. Developer options → **Enable Bluetooth HCI snoop log** = Enabled (Filtered off) → toggle Bluetooth off/on. Reconnect the M5, open the app (so the SDP/RFCOMM connect is in the log).
2. PowerShell A: `.\tools\capture-log.ps1` — type a label, press Enter, then perform that one action on the phone. Repeat for the checklist below.
3. Quit the marker (empty line). Then: `.\tools\capture-pull.ps1` (rooted: copies the newest `btsnoop_hci_*.log` from `/data/misc/bluetooth/logs`; one-time `.\tools\root-setup.ps1` enables snoop). Non-rooted: bugreport route.
4. Export: `python tools/btsnoop_export.py research/cap/btsnoop_hci.log --actions research/cap/actions.csv --ts-offset-ms -7200000 --out Momentum5.Tests/Fixtures/captures/<yyyy-mm-dd>-baseline --fw <fw>`
5. Fixtures are append-only ground truth. Never edit `frames.json`.

Wireshark check (optional): filter `btrfcomm`; `btsdp` shows UUID/channel.

## Checklist (one UI action each; mark BEFORE tapping; wait ~3 s between)
battery refresh · ANC off · ANC on · ANC adaptive · ANC comfort · ANC anti-wind · ANC level low/high · transparency off/on · transparency level low/high · transparency auto-pause on/off · EQ band 1..5 each (one change) · EQ preset switch · EQ reset · bass boost · sound mode (each option) · voice prompts on/off · sidetone on/off · sidetone level min · sidetone level max · auto-off (each) · wear detection on/off · smart pause on/off · auto-answer on/off · comfort calls on/off · touch lock on/off · rename device · immersive audio on/off · head tracking on/off · battery protection on/off · connections list · connect/disconnect a second device · EQ preset share (QR) · open app cold (handshake) · open Firmware screen (do NOT start an update).

Record separately (note in ERRATA): is Adaptive ANC exclusive with Comfort? touch-lock polarity (UI "locked" → value); sidetone min/max payload values; which `0007` registrations (vendor + feature ids) the app sends at connect.

Note: btsnoop timestamps are phone LOCAL time; offset = -(UTC offset) in ms.
