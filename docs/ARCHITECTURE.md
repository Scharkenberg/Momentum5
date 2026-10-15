# Architecture

Condensed from the engineering plan's reference chunk
[docs/plan/ref/03-architecture.md](plan/ref/03-architecture.md) and updated
with the corrections recorded in [docs/plan/ERRATA.md](plan/ERRATA.md).
Companion docs: [README](../README.md) · [SAFETY](SAFETY.md) ·
[CONTRIBUTING](CONTRIBUTING.md) · [`m5ctl` reference](cli.md).

Scope: one product — the Sennheiser MOMENTUM 5 Wireless (`M5AEBT*`). The
handshake gates the model string read over GAIA (`S:1206`); anything else is
disconnected as "unsupported device". There is no generic Sennheiser mode.

## Project graph

All projects sit flat under the repo root
([Momentum5.slnx](../Momentum5.slnx); solution platforms x64 and ARM64):

| Project | TFM | References | Contents |
|---|---|---|---|
| `Momentum5.Core` | `net11.0` | — (BCL only) | Transport and settings contracts, `BtError`, `SettingKey<T>`, domain value types, safety token types. |
| `Momentum5.Protocol` | `net11.0` | Core | `GaiaFrame`, deframer with resync, command words, codecs, typed bindings, `GaiaCatalog`, notification map, `M5Profile`, formatter. |
| `Momentum5.Client` | `net11.0` | Core, Protocol | `GaiaLink`, `CommandPipeline`, `SafetyGate`, `ConsentService`, handshake, `HeadsetStateStore`, `SettingService`, `ConnectionSupervisor`, frame taps, persistence, firmware update check/prepare. No WinRT. |
| `Momentum5.Simulation` | `net11.0` | Core, Protocol | `LoopbackTransport` + `FakeM5` — the fake headset used by tests, `m5ctl --simulate`, and design-time data. |
| `Momentum5.Transport.Rfcomm` | `$(WinTfm)` | Core | WinRT RFCOMM GAIA transport, Classic presence, radio monitor, HFP battery fallback. |
| `Momentum5.Transport.Ble` | `$(WinTfm)` | Core | GATT explorer, BLE advertisement presence. |
| `Momentum5.App` | `$(WinTfm)` | all six above | WinUI 3 app; composition root; no business logic. |
| `Momentum5.Cli` (`m5ctl`) | `$(WinTfm)` | Core, Protocol, Client, Simulation, Transport.Rfcomm | CLI; composition root. |
| `Momentum5.Tests` | `net11.0` | Core, Protocol, Client, Simulation | Unit + integration tests, architecture rules test, capture fixtures. |

`$(WinTfm)` = `net11.0-windows10.0.26100.0`, defined once in
[Directory.Build.props](../Directory.Build.props) (which also sets
`Nullable`, `ImplicitUsings`, `LangVersion=latest`, `TreatWarningsAsErrors`,
`AnalysisLevel=latest-recommended`). Package versions are pinned centrally in
[Directory.Packages.props](../Directory.Packages.props) (WindowsAppSDK,
Windows.SDK.BuildTools, CommunityToolkit.Mvvm, H.NotifyIcon.WinUI,
Microsoft.Extensions.Hosting, System.CommandLine, xunit.v3,
TimeProvider.Testing); prerelease packages appear only in the App/Cli graphs,
never in Core/Protocol/Client. The .NET SDK is pinned by
[global.json](../global.json).

**Dependency rules** (enforced by the architecture-rules test in
`Momentum5.Tests`): Core references nothing. Protocol references Core only.
Client references Core + Protocol and contains no WinRT. Transports reference
Core only — never Protocol, never Client. Simulation references Core +
Protocol. Apps (App, Cli) are the only place that constructs a transport.

## Key contracts

The UI/CLI layer talks **only** in setting keys + domain values; there are no
per-feature facade classes.

```csharp
// Core.Transport — connection-oriented, transport-neutral link
public enum LinkFraming { ByteStream, Datagram }   // RFCOMM = ByteStream; GATT = Datagram
public interface ILinkTransport {
    string Id { get; }                              // "rfcomm-gaia" | "ble-gatt-gaia" | "loopback"
    LinkFraming Framing { get; }
    ValueTask<ILinkConnection> ConnectAsync(DeviceIdentity target, CancellationToken ct); // throws LinkException
}
public interface ILinkConnection : IAsyncDisposable {
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);   // 0 => link lost
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct); // Datagram: one message
    Task Closed { get; }
}
public interface IDevicePresence  { event Changed; ValueTask<IReadOnlyList<DeviceIdentity>> GetPairedAsync(ct); }
public interface IRadioMonitor    { event Changed; ValueTask<bool> IsOnAsync(ct); }
public interface IAdvertisementSource { event Sighted; void StartWatching(); void StopWatching(); }
public interface IBatteryFallback { ValueTask<int?> TryReadPercentAsync(ulong address, ct); }  // HFP battery
```

Transports are dumb pipes: no events, no read loops, no lifecycle locks. Loss
is `ReadAsync == 0` or an exception completing `Closed`; recovery is the
supervisor's job.

```csharp
// Core.Settings — the whole UI-facing surface
public sealed record SettingKey<T>(string Name, int Index = -1);
public interface ISettingService {
    ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct);
    ValueTask<SetResult<T>> SetAsync<T>(SettingKey<T> key, T value, CancellationToken ct); // gate → set → ack → re-get → compare
    ValueTask SetEqAsync(...);                                    // batch EQ write with rollback
    ValueTask RefreshAsync(SettingGroup group, CancellationToken ct);
}
public interface IHeadsetState { HeadsetSnapshot Current { get; } event EventHandler<HeadsetSnapshot>? Changed; }
```

Protocol side: a `SettingBinding<T>` (Key, Vendor, GetCmd, GetSelector,
SetCmd?, Codec, Timeout, Safety, Group, BusyGrace, MinTrust) per feature,
aggregated in `GaiaCatalog` with the raw/unbound rows (`RawCommands`) and the
notification map. **Adding a feature = 1 Key + 1 Binding (+ codec if new) + 1
`SettingViewModel<T>` line in a XAML page.** The `ISafetyGate` check takes
`(vendor, command word, isWrite, trust, devMode, consentToken?, overrideToken?)`
— the gate resolves safety by word; see [SAFETY](SAFETY.md).

## Data flow

```
UI / m5ctl
  → ISettingService  (Client.SettingService: safety gate → CommandPipeline → verify → HeadsetStateStore)
  → CommandPipeline   (single-flight; one command in flight; consent consumed here)
  → GaiaLink          (read loop; ByteStream deframe or Datagram unwrap; IFrameTap)
  → ILinkConnection ← ILinkTransport

NotificationHub ← GaiaLink (unsolicited frames) → HeadsetStateStore → UI snapshot
ConnectionSupervisor: presence/radio/advert wake → ordered transports → handshake → SessionReady
```

## Command pipeline

`CommandPipeline` serializes every GAIA exchange through one semaphore
(single-flight; the phone app competes for the same channel, so this is a
protocol requirement, not a style choice). Behavior:

- Default response timeout 5 s; per-command overrides: battery `0x0603` 9 s,
  firmware `0x1201` 10 s, paired-device count `0x1400` 9 s.
- Retry **once for reads only**; writes are never auto-retried.
- After mode-changing writes (ANC/transparency/sound mode/EQ) a busy grace of
  +2 s is allowed before declaring timeout.
- SET ACKs carry no state (empty payload) → the pipeline always re-GETs and
  compares; `SetResult.Verified` is false on mismatch.
- Dangerous executions log intent before the write and the outcome after it.

## Connection supervisor

`ConnectionSupervisor` keeps one session alive: connect (ordered transports) →
`GaiaLink` → handshake → ready → wait for loss → backoff. Wake sources:
`IDevicePresence`, `IRadioMonitor`, `IAdvertisementSource`, and manual
connect. The backoff ladder is 1 / 2 / 5 / 10 / 30 / 60 s with ±20 % jitter;
`SessionPhase` is `Idle · Connecting · Backoff · Ready · ReadOnly ·
Unsupported · RadioOff`. `PauseUntilWake()` drops the session and stops
retrying until a wake source fires — used after commands expected to kill the
link. Only the supervisor owns reconnect policy; nothing else reconnects.

## Frame taps

`IFrameTap` implementations observe every frame in both directions:
`JsonlFrameTap` (JSONL trace file — the Diagnostics trace tab and captures),
`InMemoryTap` (tests, DevTools console), `CompositeFrameTap`.

## Transports

**RFCOMM GAIA v3 — primary control plane (G1 hardware spike PASSED on
Windows 11; see [ERRATA](plan/ERRATA.md) T-03).** WinRT
`RfcommDeviceService` + `StreamSocket`, ByteStream framing. Two RFCOMM
services are advertised under UUID `A2129FF3-081B-4C45-8AFE-469D9C4842EC`;
the resolver tries index 0, then 1. G1 findings that shape the code:

- Service names returned by the WinRT API are
  `Bluetooth#Bluetooth<localAdapter>-<headsetAddr>#RFCOMM:00000000:{uuid}` —
  pass `ConnectionServiceName` through unchanged (not a bare channel number).
- `BluetoothDevice.ConnectionStatus` and the paired-device list can report
  connected = false while RFCOMM works — ConnectionStatus must **not** gate
  connect or be the sole presence signal (also use Aep.IsConnected; the
  connect attempt is the truth).
- Device names are user-editable; never select a device by name. Use the
  address or the `M5AEBT*` model string read over GAIA.

**BLE — presence and explorer.** `Transport.Ble` provides advertisement
presence and the GATT explorer (diagnostics page). GAIA-over-GATT (T-08) is
conditional on gate G3 (whether the M5 exposes the GAIA GATT service) and is
currently TODO. For datagram transports, frames are bare
`VENDOR | COMMAND | PAYLOAD`; `GaiaLink` adds/strips the 4-byte
`FF 03 LEN` header — transports never interpret GAIA.

## Threading and style

Client/transport code runs on the thread pool and never touches UI;
ViewModels marshal through `IUiDispatcher` (the App implements it with
`DispatcherQueue.TryEnqueue`). All public async APIs take a
`CancellationToken`. Style: file-scoped namespaces, tab indentation, `sealed`
by default, nullable on, `TimeProvider` for all time logic, no `async void`
outside event handlers, no exceptions in decode paths. House rules for
contributors (including the tab-preserving tooling pitfalls) are in
[CONTRIBUTING](CONTRIBUTING.md).

## Protocol reference

The protocol document — frame format, command-word algebra, vendors, session
rules, handshake order, and the M5-specific semantics with evidence tags —
is [docs/plan/ref/00-protocol.md](plan/ref/00-protocol.md). Its catalog table
is [ref/01](plan/ref/01-commands.md); golden byte vectors live in
[ref/02](plan/ref/02-golden.md); corrections are appended to
[ERRATA](plan/ERRATA.md). Current build status per bucket:
[PROGRESS](plan/PROGRESS.md).
