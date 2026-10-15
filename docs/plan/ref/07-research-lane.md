# ref/07 — Research lane (L2) playbook

Decompiling Smart Control Plus is explicitly allowed by the sponsor. Keep APK/decompiled output in `research/` (gitignored). Commit only derived tables/notes (not verbatim assets).

## APK (R-01/R-02/R-03)
1. `adb shell pm path com.sonova.chb.control` → `adb pull <each split apk>` (app may be split APKs; pull all).
2. `apktool d -s <base.apk> -o research/apk` (skip dex); search `assets/` for JSON models: `grep -ril "GAIAV3\|M5AEBT\|gaiaV3" research/apk`. Old app shipped `resources/assets/app/m4.json` (zaval/sennheiser-desktop-client has a copy — parser template).
3. If nothing in assets: `jadx -d research/jadx <apk>`; grep `GAIAV3`, `0x0495`/`1173`, `com.qualcomm.qti.gaiaclient`, `RegisterNotification`, `A2129FF3`. Also check for Flutter/KMP assets (`flutter_assets`, `*.kotlin_module`) and encrypted blobs.
4. Targets: per-device command model; registered feature list for M5; transport/channel selection; firmware-update metadata URL + package host; preset-share (QR/URL) format; personalization/MMI formats; semantics of `1403/1405/1406`, `0607/0613/0685`, ANC mode pair meaning.

## Android 16 capture (R-04)
- Developer options → "Enable Bluetooth HCI snoop log" → toggle Bluetooth off/on.
- Live btsnoop socket is broken on Android 16 (restored on 17) → use `adb bugreport out.zip` → `FS/data/misc/bluetooth/logs/btsnoop_hci.log`. Samsung: `adb pull /data/log/bt/btsnoop_hci.log`.
- Wireshark filter `btrfcomm` (GAIA payload plaintext: HCI is above encryption); `btsdp` shows UUID/channel.
- One UI action at a time; log action→timestamp in a checklist; export frames to JSON fixtures `{ts,dir,vendor,cmd,payload,action}` under `Momentum5.Tests/Fixtures/captures/<date>-<topic>/` with `meta.json {device,fw,actions}`. Fixtures are append-only ground truth.

## Frida (R-07, optional)
Hook `BluetoothSocket.connect`, `OutputStream.write`, `InputStream.read` (script base: pubglite55/SpaceTravel-Protocol). Needs root or a repackaged APK with frida-gadget.

## Firmware (R-09)
kero3009destiny/sennheiser-fw-RE `dec.py`: AES-CTR, counter start 5, MD5 verify (key in that public repo; do not paste it into this plan's code until R-09 validates the exact counter layout on a real package).
