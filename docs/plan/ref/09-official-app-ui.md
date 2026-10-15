# 09 Official app UI reference (ABSOLUTELY INDISPENSABLE, NOT FOR COMPACTING)

Source: Smart Control Plus (Android) screenshots, headset "Joe's Vibe Cans", FW 6.21.5. Images: `screenshots/01..17-*.jpg`.
Keep this file and the images through every context compaction; the UI is built to mirror them.

## Information architecture
Bottom tabs: **My Device**, **Device Settings**, **News**. Top-left: tile list, top-right: account. Devices list (17) shows the headset card with "..." menu.

## My Device (01), vertical card stack
1. Hero: headset picture, name, "Bluetooth", battery ring + %.
2. Your experience (feedback prompt) - skip.
3. **Connection Management** card: numbered connected devices (1. Joe's Xiaomi 15, 2. iPhone von Joe) -> detail (02).
4. **Noise control** card -> detail (03). Segmented **Adaptive | Custom | Off**; animated waveform; slider labelled "ANC 100%" (left) .. "0% Transparency" (right) with a centre tick; slider is 0..100 where left = full ANC. **Anti-wind** toggle row under it ("Reduce wind noise in ANC").
   - In **Adaptive** (04): slider shows a fixed centre dot (not adjustable) and the Anti-wind toggle is greyed out/disabled.
   - In **Custom** (01): slider adjustable, Anti-wind toggle active. In Off: no ANC, so slider/anti-wind inactive.
5. **My sound** card -> presets as chips (Movie, Jazz, Mids & Voices ...), **Bass boost** toggle.
6. **Immersive audio** card -> detail (05). Segmented On | Off, Dolby Atmos badge.
7. Find headphones (10, 11): phone location of last disconnect; needs phone location. Not applicable on Windows.
8. **Controls**: "Enabled" -> detail (09).
9. **Sound zones** -> intro (06), editor (08), location permission (07).
10. Explore features, Manage tiles - skip.

## Connection Management (02)
- Toggle **Multipoint** "Connect up to two devices at once."
- List of paired devices: "This device" row (greyed, checkmark, not selectable), connected devices show a blue check, not-connected ones no check (BTD 700). Trash icon top-right = forget devices (select, then delete).
- M5 wire format (captured with m5ctl multipoint): 1409 max=2; 1413 = this device MAC; 140D list; 140E[mac] record = mac6 + flags3 + name; flags 02 0000 = connected, 01 0000 = paired only; 1414 = mac6 + status byte.

## Noise control detail (03)
- **Anti-wind settings**: segmented **Auto | Max** ("Reduce wind noise in ANC"). Card toggle on My Device = off vs on. (Wire: 0 off, 1 max, 2 auto; table reads auto as 1.)
- **Phone calls**: toggle "Activate transparency during phone calls" + slider Low..High with 3 detents. => This is Keys.TransparencyStatus / TransparencyLevel (NOT a general transparency switch).
- **Auto-pause** toggle "Pause audio when Transparency is enabled." (Keys.TransparencyAutoPause).
Dependencies: Anti-wind only meaningful in Custom ANC; Phone-calls slider enabled only when its toggle is on.

## Immersive audio (05)
- Toggle **Immersive audio**; child card **Head Tracking** toggle ("Immersive sound that stays fixed as you move your head"), head-orientation dial (L/R arcs, text "You are facing the sound source - sound is balanced in both ears."), info button. Head tracking is a child of Immersive (disabled when immersive is off). Dolby Atmos badge.

## Sound zones (06, 07, 08)
- Intro: "Automatically apply sound settings when entering or leaving a zone"; Sign in or Register / Continue.
- Editor: name, map search, circle radius 100 m..1 km (default 200 m); **Enter zone**: EQ preset, Noise control (each "No action"); **Leave zone**: EQ preset, Noise control; Save sound zone (disabled until changed). Location permission prompt first.

## Controls (09)
- Master toggle **Controls**; tabs **General | Playback | Calls**; note "Headband gestures cannot be customized". General rows: Slide down = Volume down, Slide up = Volume up, Spread = Increase transparency, Pinch = Decrease transparency (info buttons). Read-only list.

## Device Settings (12), flat list under "Device - Configure your Sennheiser headphones"
Firmware (version 6.21.5 ->) | Audio mode priority (Advanced sound quality ->, 13) | Codec ("No audio stream" ->, 14) | **On-head Detection** toggle ("Enable smart pause, hold calls automatically, power saving mode") with dependent rows **Smart Pause** toggle, **Auto-Answer Calls** toggle, **Auto Power Off** (Never ->, 15) | **Comfort Calls** toggle | **Tone & voice prompts** (Tone only ->, 16) | **Battery Protection Mode** toggle ("Prolong battery lifetime - charging is slower and prevents charging to 100%") | Help | Contact | App feedback | Product info (MOMENTUM 5 Wireless Black) | **Name headphones** (Joe's Vibe Cans ->) | **Factory Reset**.
Dependencies: Smart Pause, Auto-Answer, Auto Power Off sit under On-head Detection (disable/grey when it is off).

## Audio mode priority (13)
Header "Advanced sound quality - Priority on highest sound quality." Toggles **Hi-Res 24 bit/96 kHz** and **Lossless 16 bit/44.1 kHz**. Footer: "Changing mode requires restarting the headphones."

## Codec (14), read-only
"The highest quality codec supported by both the source and headphones is automatically selected." Robust connection (SBC, AAC); Advanced sound quality (aptX, aptX Adaptive, aptX-HD, aptX Lossless).

## Auto Power Off (15)
Radio list: Never (selected) | 15 min | 30 min | 60 min. "Sets the time after which your device turns itself off to save power."

## Tone & Voice Prompts (16)
Radio: Tone & voice prompts (English) | Tone only (selected) | Off. **Volume** slider Quiet..Loud. **Language** English (greyed unless voice prompts are on).

## Windows port decisions
- Find headphones: omit (phone location). Sound zones: keep, use Windows location + this editor layout (EQ preset / noise mode on enter and leave).
- Nav order mirrors the app: Dashboard (My Device), Device Settings, Connections, Sound zones, Diagnostics, Settings.
