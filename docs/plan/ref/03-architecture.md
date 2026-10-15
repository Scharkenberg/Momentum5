# ref/03 — Architecture & contracts

## Projects (all under repo root, flat like today)
```
Momentum5.Core               net11.0     BCL only. Transport contracts, BtError, SettingKey<T>, domain value types, ISettingService/IHeadsetState
Momentum5.Protocol           net11.0     → Core. Frame, deframer, command words, codecs, bindings, catalog, notification map, M5 profile, formatter
Momentum5.Client             net11.0     → Core, Protocol. GaiaLink, pipeline, safety gate, notifications, handshake, state, SettingService, supervisor, tap, persistence
Momentum5.Simulation         net11.0     → Core, Protocol. LoopbackTransport + FakeM5 (used by Tests, Cli --simulate, App design-time)
Momentum5.Transport.Rfcomm   $(WinTfm)   → Core. RFCOMM (WinRT) transport, Classic presence, radio monitor, HFP battery, [Winsock fallback]
Momentum5.Transport.Ble      $(WinTfm)   → Core. GattExplorer (existing probe), advert presence, [GATT-GAIA]
Momentum5.App                $(WinTfm)   → all. WinUI 3; composition root; no business logic
Momentum5.Cli                $(WinTfm)   → all. m5ctl; composition root
Momentum5.Tests              net11.0     → Core, Protocol, Client, Simulation
```
`$(WinTfm)` = `net11.0-windows10.0.26100.0` (defined once in Directory.Build.props). Platforms: x64, ARM64.

**Rules (enforced by Architecture test, A-03):** Core refs nothing. Protocol refs Core only. Client refs Core+Protocol, no WinRT. Transports ref Core only (never Protocol, never Client). Simulation refs Core+Protocol. Apps are the only place that `new` a transport.

## Key contracts (binding; may add members, not reshape)
```csharp
// Core.Transport
public enum LinkFraming { ByteStream, Datagram }
public sealed record DeviceIdentity(ulong Address, string? Name, string? DeviceId, bool IsPaired, bool IsConnected);
public interface ILinkTransport {
    string Id { get; }                      // "rfcomm-gaia" | "winsock-rfcomm" | "ble-gatt-gaia" | "loopback"
    LinkFraming Framing { get; }
    ValueTask<ILinkConnection> ConnectAsync(DeviceIdentity target, CancellationToken ct); // throws LinkException
}
public interface ILinkConnection : IAsyncDisposable {
    string TransportId { get; }
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);   // 0 => peer closed / link lost
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct); // ByteStream: any bytes; Datagram: one message
    Task Closed { get; }                                                  // completes on loss or dispose
}
public sealed class LinkException : Exception { public BtError Error { get; } /* ctor(BtError, int hresult, string, Exception?) sets base.HResult */ }
public interface IDevicePresence { event EventHandler<DeviceIdentity>? Changed; ValueTask<IReadOnlyList<DeviceIdentity>> GetPairedAsync(CancellationToken ct); }
public interface IRadioMonitor { event EventHandler<bool>? Changed; ValueTask<bool> IsOnAsync(CancellationToken ct); }
public interface IAdvertisementSource { event EventHandler<AdvertisementSighting>? Sighted; void StartWatching(); void StopWatching(); }
public interface IBatteryFallback { ValueTask<int?> TryReadPercentAsync(ulong address, CancellationToken ct); }

// Core.Settings — UI talks ONLY in keys + domain values
public sealed record SettingKey<T>(string Name, int Index = -1);
public interface ISettingService {
    ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct);
    ValueTask<SetResult<T>> SetAsync<T>(SettingKey<T> key, T value, CancellationToken ct); // gate → set → ack → re-get → compare
    ValueTask RefreshAsync(SettingGroup group, CancellationToken ct);
}
public interface IHeadsetState { HeadsetSnapshot Current { get; } event EventHandler<HeadsetSnapshot>? Changed; }
// HeadsetSnapshot: immutable; T? Get<T>(SettingKey<T>); plus SessionInfo (model, fw, serial, trust, transportId)
```
Protocol side: `SettingBinding<T>` (Key, Vendor, GetCmd, GetSelector, SetCmd?, Codec, Timeout, Safety, Group, BusyGrace, MinTrust) in a `BindingRegistry`. Adding a feature = 1 Key + 1 Binding (+ codec if new) + 1 `SettingViewModel<T>` line in XAML page. No per-feature facade classes.

## Data flow
```
UI/CLI → ISettingService (Client.SettingService: gate → CommandPipeline → verify → HeadsetStateStore)
CommandPipeline ← GaiaLink (read loop, deframe or datagram unwrap, IFrameTap) ← ILinkConnection ← ILinkTransport
NotificationHub ← GaiaLink (unsolicited frames) → HeadsetStateStore
ConnectionSupervisor: IDevicePresence + IRadioMonitor + IAdvertisementSource wake → transports (ordered) → handshake → SessionReady
```
Transport is connection-oriented: no events, no read loops, no lifecycle locks in transports. Loss = `ReadAsync==0` or exception → `Closed`.

## Threading
Client/transport code runs on thread pool, never touches UI. ViewModels marshal via `IUiDispatcher` (App implements with `DispatcherQueue.TryEnqueue`). All public async APIs take `CancellationToken`. WinRT calls that show consent UI go through `IUiDispatcher` where one exists.

## Code style
File-scoped namespaces, tabs, `sealed` by default, nullable on, `TimeProvider` for any time logic, no `async void` outside event handlers, no exceptions in decode paths (return failure).
