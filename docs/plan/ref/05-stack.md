# ref/05 — Stack, pins, conventions

NuGet queried 2026-10-14 (`https://api.nuget.org/v3-flatcontainer/<id>/index.json`). Constraint #7 = latest version incl. prerelease. **Re-query before pinning in the bucket that first uses a package** (pins are added just-in-time to `Directory.Packages.props`, not up front).

| Package | Latest (pin) | Latest stable | Where |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.4-experimental (released 2026-09-29) | 2.5.1 | App only |
| Microsoft.Windows.SDK.BuildTools | 10.0.29648.1000-preview | 10.0.28000.2705 | App only (keep current repo pin — it IS the latest) |
| CommunityToolkit.Mvvm | 8.4.2 | 8.4.2 | App |
| Microsoft.Extensions.Hosting / DependencyInjection / Logging / Options | 11.0.0-rc.2.26504.105 | 10.0.13 | App, Cli, Client(abstractions only) |
| System.CommandLine | 3.0.0-rc.2.26504.105 (old plan said rc.1.26425 — stale) | 2.0.13 | Cli |
| H.NotifyIcon.WinUI | 2.5.0-dev.2 → fall back to 2.4.1 if restore/runtime fails (record in decisions) | 2.4.1 | App |
| xunit.v3 | 4.0.2 (old plan said 4.0.1) | 4.0.2 | Tests; runner = Microsoft.Testing.Platform |
| Microsoft.Windows.CsWin32 | 0.3.363 (analyzer, PrivateAssets=all) | 0.3.363 | Rfcomm (cfgmgr32, winsock) |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 (re-query; may have 11.x) | | Tests |
Not used: Serilog, VSTest packages (`Microsoft.NET.Test.Sdk`), Pipelines package, CsWinRT explicit pin (comes via TFM/SDK).

## Build config
- `Directory.Build.props`: `Nullable`, `ImplicitUsings`, `LangVersion=latest`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `WinTfm`.
- `global.json`: SDK = installed .NET 11 SDK (`dotnet --version`), `rollForward=latestMajor`, `allowPrerelease=true`, `"test": {"runner": "Microsoft.Testing.Platform"}`.
- App: `PublishTrimmed=false`, `PublishReadyToRun=true` (Release), platforms x64+ARM64 only, `WindowsAppSDKSelfContained` decided in I-02.
- Prerelease packages live only in App/Cli csprojs' dependency graph — never in Core/Protocol/Client.

## Agent output contract (all buckets)
- The user does all git commits/pushes. Agent never commits. Output = final file contents per touched file + a one-line suggested commit message (`feat(<track>): <ID> <summary>`). No explanations unless blocked.
- Do not shim or compile WinUI/WinRT code in a Linux container; verify against Microsoft Learn docs instead. If a Windows SDK is present, run the bucket's build/test command.
- Touch only paths in the bucket's `Touch:` line. One bucket per turn.
- Hex in logs/tests: uppercase, space-separated.
