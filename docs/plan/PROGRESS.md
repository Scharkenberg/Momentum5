# PROGRESS

Statuses: TODO · DOING · DONE · BLOCKED-HW · SKIPPED. Update only your bucket's row.

|ID|Status|Note|
|-|-|-|
|A-01|DONE|unverified: no dotnet in agent shell|
|A-02|DONE|unverified: no dotnet in agent shell|
|A-03|DONE|unverified: no dotnet in agent shell|
|A-04|DONE|unverified: no dotnet in agent shell|
|A-05|DONE|unverified: no dotnet in agent shell|
|A-06|DONE|unverified: no dotnet in agent shell|
|I-01|DONE|unverified: first CI run pending|
|R-01|DONE|models encrypted (Flutter); see ERRATA|
|R-02|DONE|negative: unresolved ids move to R-04/R-05|
|R-03|DONE|partial; see ERRATA|
|R-04|DONE|all 4 semantics resolved (tier-1 capture); see ERRATA|
|P-01|DONE|dotnet test 45/45 pass|
|P-02|DONE|dotnet test 45/45 pass|
|P-03|DONE|dotnet test 45/45 pass|
|P-04|DONE|dotnet test 45/45 pass|
|T-01|DONE|unverified: build+arch test pending; RfcommConnection I/O already implemented (T-02 = hardening)|
|T-02|DONE|unverified: build pending (done with T-01 + CloseReason)|
|T-03|DONE|G1 PASSED on Win11 (see ERRATA)|
|T-04|SKIPPED|G1 passed, Winsock fallback not needed|
|P-05|DONE|dotnet test pass (codec primitives + binding model)|
|P-06|DONE|dotnet test pass (identity & power bindings)|
|P-07|DONE|dotnet test pass (ANC & transparency bindings)|
|P-08|DONE|dotnet test pass (EQ & sound-mode bindings)|
|P-09|DONE|dotnet test pass (spatial, calls & toggle bindings)|
|P-10|DONE|dotnet test pass (multipoint bindings)|
|P-11|DONE|dotnet test pass (catalog aggregate + raw rows)|
|P-12|DONE|dotnet test pass (notification map & registration)|
|P-13|DONE|dotnet test pass (M5 profile & trust)|
|P-14|DONE|dotnet test pass (frame formatter)|
|S-01|DONE|dotnet test pass|
|S-02|DONE|dotnet test pass|
|S-03|DONE|dotnet test pass|
|C-01|DONE|unverified: build pending; no tests written (by request)|
|C-02|DONE|unverified: build pending; no tests written (by request)|
|C-03|DONE|unverified: build pending; no tests written (by request)|
|C-04|DONE|unverified: build pending; no tests written (by request)|
|C-05|DONE|unverified: build pending; no tests written (by request)|
|C-06|DONE|unverified: build pending; no tests written (by request)|
|C-07|DONE|unverified: build pending; no tests written (by request)|
|C-08|DONE|unverified: build pending; no tests written (by request)|
|C-09|DONE|unverified: build pending; no tests written (by request)|
|C-10|DONE|unverified: build pending; no tests written (by request)|
|C-11|DONE|unverified: build pending; no tests written (by request)|
|C-12|DONE|integration suite written; unverified|
|K-01|DONE|list/status/watch; --simulate; unverified|
|K-02|DONE|get/set/eq/keys; unverified|
|K-03|DONE|G2 PASSED on HW (fw 6.21.5): selftest 9/9 PASS in 2.9 s|
|V-01|DONE|matrix command; run `m5ctl matrix --out docs/verification-matrix.md` and commit|
|R-05|TODO||
|T-05|DONE|ClassicPresence + BluetoothRadioMonitor; builds unverified; HW check pending|
|T-06|DONE|HW verified: pairing-mode Swift Pair beacon only; no adverts when idle/connected|
|T-07|DONE|HW: M5 exposes no HFP battery to Windows (mic active too); best-effort only, GAIA 0603 is the source|
|T-08|SKIPPED|G3: no GAIA GATT service (00001100); live LE link never established from Windows; cached table only. GAIA stays RFCOMM-only|
|U-01|DONE|unverified: WinUI not compiled here; HW/UI check pending|
|U-02|DONE|unverified: WinUI not compiled here; HW/UI check pending|
|U-03|DONE|unverified: WinUI not compiled here; HW/UI check pending|
|U-04|DONE|unverified: WinUI not compiled here; HW/UI check pending|
|U-05|DONE|unverified: WinUI not compiled here; HW check pending|
|U-06|DONE|unverified: WinUI not compiled here; HW check pending|
|U-07|DONE|unverified: WinUI not compiled here; HW check pending|
|U-08|DONE unverified|SpatialPage; refused-immersive banner shows GAIA reason|
|U-09|DONE unverified|CallsPage; sidetone 0-4 (key range), low-latency hidden unless Verified; touch polarity per key doc|
|U-10|DONE unverified|PowerPage + DevicePage; rename blocks >16 UTF-8 bytes, confirm dialog|
|U-11|DONE unverified|MultipointPage (nav "Multipoint"); no forget/delete; connect confirm dialog|
|U-12|SUPERSEDED|GATT lab (GattLabViewModel over GattConnection/GattLab); IGattExplorer removed|
|U-13|SUPERSEDED|GattLabPage replaces MainWindow; reachable from Diagnostics|
|U-14|DONE unverified|TracePage + InMemoryTap (10000 ring) + CompositeFrameTap; incremental list|
|U-15|DONE unverified|AdvertsPage (live + scan nearby), ExportBundle zip, deterministic MacAnonymizer; export button on Diagnostics|
|U-16|DONE unverified|SettingsPage; GATT-GAIA toggle omitted (T-08 skipped); log level persisted but no logger consumes it yet|
|U-17|DONE unverified|TrayService (H.NotifyIcon.WinUI 2.4.1) + SingleInstance mutex/event + close-to-tray|
|U-18|DONE unverified|ToastService, edge-triggered, all flags default off|
|U-19|DONE unverified|HotkeyService via RegisterHotKey + window subclass on shell HWND (not a separate message window)|
|U-20|PARTIAL|theme switch, spacing/44 px tokens; no .resw strings, no Accessibility Insights pass yet|
|U-21|DONE unverified|ZoneService (off by default, IGeoSource), settings UI|
|I-02|TODO||
|I-03|TODO||
|I-04|TODO||
|X-01|TODO||
|X-02|TODO||
|X-03|TODO||
|X-04|TODO||
|X-05|TODO||
|X-06|TODO||
|X-07|TODO||
|X-08|TODO||
|R-06|TODO||
|R-07|TODO||
|R-08|TODO||
|R-09|TODO||
|X-09|TODO||
|X-10|TODO||
|X-11|TODO||
|X-12|TODO||
|X-13|TODO||
|V-02|TODO||
|GATT-LAB|DONE HW verified (G3 answered: cached table only, no live LE link)|GattLab/GattConnection, `m5ctl gatt scan` + `gatt diag`, GattLabPage; legacy MainWindow/GattExplorer removed; run in pairing mode (feeds G3)|

## Merge of second-agent work (2026-10-15)
Adopted (unbuilt, untested by decision): Core/Client safety stack (CommandPipeline, SafetyGate, ConsentService/Types), EQ + MMI bindings and catalog, FakeM5, Firmware client (UpdateCheck, PackagePrep, UpgradeSession, profile), docs (ARCHITECTURE, SAFETY, PROTOCOL-upgrade, fw-format, recovery, release, ADR-001, coexistence), `release.yml`, `tools/fw/decrypt.py`.
App: Advanced hub (DevMode-gated nav item) with GAIA console, parametric EQ, touch buttons (MMI), factory reset, firmware update; wired through `Shell/ShellAdapters.cs` (`App.Service<T>()` locator, Shell `IUiDispatcher`, `StartupController` shim) instead of their Hosting/DI shell.
Rejected: DI app shell and their U-03..U-21 pages (superseded by screenshot-driven UI), GATT explorer, Pairings by-index UI (M5 pairs by MAC, see ERRATA), their zones/hotkeys/diagnostics.
Open: M5 forget/connect/factory-reset commands, sound-zone editor, audio-mode priority keys, rename SET verification, phone-calls slider range, hardware checklist.
