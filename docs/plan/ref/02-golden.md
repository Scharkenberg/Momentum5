# ref/02 — Golden frames (M5AEBT fw 6.21.5, from Aria `m5-6.21.5-hardware.json`)

Hex, spaced. TX/RX are full frames.

| Exchange | TX | RX |
|---|---|---|
| Model 1206 | FF 03 00 00 04 95 12 06 | FF 03 00 0D 04 95 13 06 4D 35 41 45 42 54 20 42 6C 61 63 6B 00 |
| Firmware 1201 | FF 03 00 00 04 95 12 01 | FF 03 00 06 04 95 13 01 00 06 00 15 00 05 |
| Battery 0603 | FF 03 00 00 04 95 06 03 | FF 03 00 01 04 95 07 03 1E |
| ANC modes 1A01 | FF 03 00 00 04 95 1A 01 | FF 03 00 06 04 95 1B 01 01 00 03 00 02 00 |
| ANC set pair 1A00 (anti-wind=1) | FF 03 00 02 04 95 1A 00 01 01 | FF 03 00 00 04 95 1B 00 |
| ANC read after | FF 03 00 00 04 95 1A 01 | FF 03 00 06 04 95 1B 01 01 01 03 00 02 00 |
| ANC set adaptive=1 | FF 03 00 02 04 95 1A 00 03 01 | FF 03 00 00 04 95 1B 00 |
| ANC enabled 1A05 | FF 03 00 00 04 95 1A 05 | FF 03 00 01 04 95 1B 05 01 |
| TH status set 1804=1 | FF 03 00 01 04 95 18 04 01 | FF 03 00 00 04 95 19 04 |
| EQ config 1000 | FF 03 00 00 04 95 10 00 | FF 03 00 05 04 95 11 00 08 C4 3C 00 00 |
| EQ band set 1001 (band0=+0.1) | FF 03 00 02 04 95 10 01 00 01 | FF 03 00 00 04 95 11 01 |
| EQ band get 1002 | FF 03 00 01 04 95 10 02 00 | FF 03 00 01 04 95 11 02 01 |
| EQ all 1003 | FF 03 00 01 04 95 10 03 00 | FF 03 00 08 04 95 11 03 00 00 00 00 00 00 00 00 |
| EQ freq 100B band5 | FF 03 00 01 04 95 10 0B 05 | FF 03 00 03 04 95 11 0B 05 1F 40 |
| Sound mode get 0804 | FF 03 00 00 04 95 08 04 | FF 03 00 02 04 95 09 04 00 01 |
| Sound mode set 0803 | FF 03 00 02 04 95 08 03 00 02 | FF 03 00 00 04 95 09 03 |
| Prompt set 0801=2 | FF 03 00 01 04 95 08 01 02 | FF 03 00 00 04 95 09 01 |
| Touch lock set 1606=1 | FF 03 00 01 04 95 16 06 01 | FF 03 00 00 04 95 17 06 |

Payload-only captures (Aria immersive/battery-protection files, vendor S): `3605`→`01`, `3603`→`00`, `3601`→`01 0F`, `2001`→`00`, `060B`→`01`, `1803`→`64`.

Synthetic unit vectors (derive, not captured): Register feature 3 → `FF 03 00 01 04 95 00 07 03`; ModelId error reason 05 → `FF 03 00 01 04 95 13 86 05`.

License note: copying Aria fixtures requires the MIT notice in `THIRD_PARTY_NOTICES.md` (done in P-04).
