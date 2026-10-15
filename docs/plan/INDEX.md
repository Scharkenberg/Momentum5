# Bucket index (execution order)

Run top-to-bottom. A bucket is eligible when all Deps are DONE (see PROGRESS.md). L2 buckets may run concurrently with the next L1 bucket (max 2 lanes total).

| # | ID | Title | Lane | Size | Deps | HW |
|---|---|---|---|---|---|---|
| 1 | [A-01](buckets/A-01.md) | Baseline build hygiene | L1 | S | — | no |
| 2 | [A-02](buckets/A-02.md) | Shared build props, central packages, global.json | L1 | S | A-01 | no |
| 3 | [A-03](buckets/A-03.md) | Tests project + architecture rules test | L1 | S | A-02 | no |
| 4 | [A-04](buckets/A-04.md) | Core transport contracts + BtError map | L1 | M | A-02 | no |
| 5 | [A-05](buckets/A-05.md) | GATT explorer rename (in place) | L1 | M | A-04 | no |
| 6 | [A-06](buckets/A-06.md) | Core settings primitives | L1 | S | A-02 | no |
| 7 | [I-01](buckets/I-01.md) | CI workflow | L1 | S | A-03 | no |
| 8 | [R-01](buckets/R-01.md) | APK acquisition + unpack | L2 | M | — | phone |
| 9 | [R-02](buckets/R-02.md) | Extract M5 command model & diff vs catalog | L2 | M | R-01 | no |
| 10 | [R-03](buckets/R-03.md) | jadx study: transport, registrations, URLs, formats | L2 | M | R-01 | no |
| 11 | [R-04](buckets/R-04.md) | Android 16 HCI capture baseline | L2 | M | — | phone+M5 |
| 12 | [P-01](buckets/P-01.md) | GaiaFrame encode | L1 | S | A-04 | no |
| 13 | [P-02](buckets/P-02.md) | GaiaDeframer with resync | L1 | M | P-01 | no |
| 14 | [P-03](buckets/P-03.md) | Command words, errors, message kinds | L1 | S | P-01 | no |
| 15 | [P-04](buckets/P-04.md) | Capture fixture harness | L1 | M | P-02, P-03, A-03 | no |
| 16 | [T-01](buckets/T-01.md) | RFCOMM channel resolver + connect | L1 | M | A-04 | no |
| 17 | [T-02](buckets/T-02.md) | RFCOMM connection I/O | L1 | M | T-01 | no |
| 18 | [T-03](buckets/T-03.md) | m5ctl probe (hardware spike, gate G1) | L1 | M | T-02, P-02, P-03 | yes |
| 19 | [T-04](buckets/T-04.md) | Winsock RFCOMM fallback (conditional on G1 fail) | L1 | M | T-03 | cond |
| 20 | [P-05](buckets/P-05.md) | Codec primitives + binding model | L1 | M | A-06, P-03 | no |
| 21 | [P-06](buckets/P-06.md) | Identity & power bindings | L1 | M | P-05, P-04 | no |
| 22 | [P-07](buckets/P-07.md) | ANC & transparency bindings | L1 | M | P-05 | no |
| 23 | [P-08](buckets/P-08.md) | EQ & sound-mode bindings | L1 | M | P-05 | no |
| 24 | [P-09](buckets/P-09.md) | Spatial, calls & toggle bindings | L1 | M | P-05 | no |
| 25 | [P-10](buckets/P-10.md) | Multipoint bindings | L1 | S | P-05 | no |
| 26 | [P-11](buckets/P-11.md) | Catalog aggregate + invariants | L1 | M | P-06, P-07, P-08, P-09, P-10 | no |
| 27 | [P-12](buckets/P-12.md) | Notification map & registration | L1 | S | P-11 | no |
| 28 | [P-13](buckets/P-13.md) | M5 profile, trust tiers, model gate | L1 | S | P-11 | no |
| 29 | [P-14](buckets/P-14.md) | Frame formatter | L1 | S | P-11 | no |
| 30 | [S-01](buckets/S-01.md) | LoopbackTransport | L1 | M | P-02 | no |
| 31 | [S-02](buckets/S-02.md) | FakeM5 core | L1 | M | S-01, P-11 | no |
| 32 | [S-03](buckets/S-03.md) | FakeM5 behaviors & failure knobs | L1 | M | S-02 | no |
| 33 | [C-01](buckets/C-01.md) | GaiaLink | L1 | M | P-02, S-01, A-04 | no |
| 34 | [C-02](buckets/C-02.md) | SafetyGate | L1 | M | P-11, P-13 | no |
| 35 | [C-03](buckets/C-03.md) | CommandPipeline (single-flight) | L1 | M | C-01, C-02, P-03 | no |
| 36 | [C-04](buckets/C-04.md) | NotificationHub & registration | L1 | M | C-03, P-12 | no |
| 37 | [C-05](buckets/C-05.md) | Handshake | L1 | M | C-03, C-04, P-13, P-06 | no |
| 38 | [C-06](buckets/C-06.md) | HeadsetStateStore | L1 | S | C-04, A-06 | no |
| 39 | [C-07](buckets/C-07.md) | SettingService (get/set/verify) | L1 | M | C-05, C-06 | no |
| 40 | [C-08](buckets/C-08.md) | EQ batch + group refresh | L1 | M | C-07, P-08 | no |
| 41 | [C-09](buckets/C-09.md) | Frame tap (JSONL) | L1 | M | C-01 | no |
| 42 | [C-10](buckets/C-10.md) | ConnectionSupervisor | L1 | M | C-05, S-01 | no |
| 43 | [C-11](buckets/C-11.md) | App settings & presets persistence | L1 | M | A-06 | no |
| 44 | [C-12](buckets/C-12.md) | Client integration suite | L1 | M | C-08, C-10, S-03 | no |
| 45 | [K-01](buckets/K-01.md) | m5ctl DI + list/status/watch | L1 | M | C-10, C-08, T-03 | no |
| 46 | [K-02](buckets/K-02.md) | m5ctl get/set (descriptor-driven) | L1 | M | K-01 | no |
| 47 | [K-03](buckets/K-03.md) | m5ctl selftest (gate G2) | L1 | S | K-02 | yes |
| 48 | [V-01](buckets/V-01.md) | Verification matrix generator | L1 | S | K-02 | no |
| 49 | [R-05](buckets/R-05.md) | Windows hardware questions | L2 | M | K-03 | yes |
| 50 | [T-05](buckets/T-05.md) | Classic presence + radio monitor | L1 | M | T-01 | no |
| 51 | [T-06](buckets/T-06.md) | BLE advertisement presence | L1 | S | A-05 | no |
| 52 | [T-07](buckets/T-07.md) | HFP battery fallback | L1 | S | T-01 | no |
| 53 | [T-08](buckets/T-08.md) | GAIA-over-GATT transport (conditional on G3) | L1 | M | R-05, A-05 | cond |
| 54 | [U-01](buckets/U-01.md) | App shell: Host/DI + navigation + status chip | L1 | M | C-10, C-11, T-05 | no |
| 55 | [U-02](buckets/U-02.md) | ViewModel base + SettingViewModel<T> | L1 | M | U-01, S-02 | no |
| 56 | [U-03](buckets/U-03.md) | Dashboard page | L1 | M | U-02 | no |
| 57 | [U-04](buckets/U-04.md) | Device picker & connection UX | L1 | M | U-02, T-05, C-10 | no |
| 58 | [U-05](buckets/U-05.md) | Noise control page | L1 | M | U-02 | no |
| 59 | [U-06](buckets/U-06.md) | Sound / EQ page | L1 | M | U-02, C-08 | no |
| 60 | [U-07](buckets/U-07.md) | EQ presets | L1 | M | U-06, C-11 | no |
| 61 | [U-08](buckets/U-08.md) | Spatial & battery protection page | L1 | S | U-02 | no |
| 62 | [U-09](buckets/U-09.md) | Calls & comfort page | L1 | M | U-02 | no |
| 63 | [U-10](buckets/U-10.md) | Power, device info & rename | L1 | S | U-02 | no |
| 64 | [U-11](buckets/U-11.md) | Connections page (multipoint) | L1 | M | U-02 | no |
| 65 | [U-12](buckets/U-12.md) | GATT explorer ViewModel extraction | L1 | M | U-01, A-05 | no |
| 66 | [U-13](buckets/U-13.md) | GATT explorer page (parity) | L1 | M | U-12 | no |
| 67 | [U-14](buckets/U-14.md) | GAIA trace tab | L1 | M | U-01, C-09, P-14 | no |
| 68 | [U-15](buckets/U-15.md) | Adverts tab + diagnostics export | L1 | M | U-14, T-06 | no |
| 69 | [U-16](buckets/U-16.md) | Settings page | L1 | S | U-02, C-11 | no |
| 70 | [U-17](buckets/U-17.md) | Tray icon + single instance | L1 | M | U-03 | no |
| 71 | [U-18](buckets/U-18.md) | Toasts (opt-in) | L1 | S | U-17 | no |
| 72 | [U-19](buckets/U-19.md) | Global hotkeys | L1 | M | U-17 | no |
| 73 | [U-20](buckets/U-20.md) | Theming & accessibility pass | L1 | M | U-11, U-10, U-09, U-08, U-06, U-05 | no |
| 74 | [U-21](buckets/U-21.md) | Location zones (stretch, flag-gated) | L1 | M | U-07 | no |
| 75 | [I-02](buckets/I-02.md) | Packaging | L1 | M | U-17, K-03 | no |
| 76 | [I-03](buckets/I-03.md) | Documentation set | L1 | M | R-02 | no |
| 77 | [I-04](buckets/I-04.md) | Release checklist | L1 | S | I-02, I-01 | no |
| 78 | [X-01](buckets/X-01.md) | Consent tokens & DevMode gating | L1 | M | C-02, U-16 | no |
| 79 | [X-02](buckets/X-02.md) | Consent dialog + Advanced page shell | L1 | M | X-01, U-01 | no |
| 80 | [X-03](buckets/X-03.md) | DevTools raw console | L1 | M | X-02 | no |
| 81 | [X-04](buckets/X-04.md) | Parametric EQ internals | L1 | M | X-02, U-06 | yes |
| 82 | [X-05](buckets/X-05.md) | MMI (touch/button) configuration | L1 | M | X-02, R-03 | yes |
| 83 | [X-06](buckets/X-06.md) | Forget pairings (gated) | L1 | M | X-02, U-11, R-02 | yes |
| 84 | [X-07](buckets/X-07.md) | Link/system commands (`0607/0613/0685`) | L1 | S | X-02, R-02 | yes |
| 85 | [X-08](buckets/X-08.md) | Factory reset | L1 | S | X-02 | yes |
| 86 | [R-06](buckets/R-06.md) | OTA update session capture | L2 | M | R-04 | phone+M5 |
| 87 | [R-07](buckets/R-07.md) | Frida confirmation (optional) | L2 | M | R-04 | phone |
| 88 | [R-08](buckets/R-08.md) | Personalization read-only RE (optional) | L2 | M | R-04 | phone+M5 |
| 89 | [R-09](buckets/R-09.md) | Firmware package decrypt validation | L2 | S | — | no |
| 90 | [X-09](buckets/X-09.md) | Firmware stage 0: version check | L1 | S | X-02, R-03 | no |
| 91 | [X-10](buckets/X-10.md) | Firmware stage 1: package prepare | L1 | M | X-09, R-09 | no |
| 92 | [X-11](buckets/X-11.md) | Firmware stage 2a: upgrade state machine (pure) | L1 | M | X-10, R-06, C-03 | no |
| 93 | [X-12](buckets/X-12.md) | Firmware stage 2b: capture replay verification | L1 | M | X-11, S-03 | no |
| 94 | [X-13](buckets/X-13.md) | Firmware stage 3: UI + recovery guide | L1 | M | X-12 | yes |
| 95 | [V-02](buckets/V-02.md) | Coexistence procedures | L2 | M | R-05, U-04 | yes |
