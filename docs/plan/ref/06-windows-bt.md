# ref/06 — Windows Bluetooth engineering notes

Docs: Microsoft Learn "Bluetooth RFCOMM" (client = enumerate/resolve service → `StreamSocket.ConnectAsync(ConnectionHostName, ConnectionServiceName)`), `Windows.Devices.Bluetooth.Rfcomm`. Verify API details there before coding T-buckets.

## RFCOMM client sequence (T-01/T-02)
1. `BluetoothDevice.FromBluetoothAddressAsync(addr)` (fallback `FromIdAsync(deviceId)`). Device must be **paired in Windows Settings**.
2. `device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(A2129FF3-081B-4C45-8AFE-469D9C4842EC), BluetoothCacheMode.Cached)`; if `Services` empty or `Error==DeviceNotConnected/Unreachable` → retry `Uncached` (live SDP). Never use deprecated `BluetoothDevice.RfcommServices`.
3. Iterate `Services[0]`, then `[1]`: `RequestAccessAsync()` (consent; UI thread if packaged app) then `new StreamSocket().ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)`; `ConnectionServiceName` is the channel number as string.
4. Connect timeout 10 s (linked CTS; `.AsTask(ct)`).
5. I/O: `DataReader` with `InputStreamOptions.Partial`, `LoadAsync(1024)`; `Length==0` = EOF. `DataWriter` + `StoreAsync`, single-writer semaphore. (`AsStreamForRead/Write` is an acceptable alternative if verified against docs.)
6. Dispose order: cancel read → dispose socket → unsubscribe `ConnectionStatusChanged` → dispose `BluetoothDevice`.

## HRESULT → `BtError` (A-04)
`0x80070490` ElementNotFound (service/channel missing or busy) · `0x80070005` AccessDenied · `0x8007048F` DeviceNotConnected · `0x8007274C` Timeout · `0x8007274D` ConnectionRefused · `0x80072745`/`0x80072746` Aborted/Reset · `0x8007277C` ServiceNotFound (device cycling) · `0x800710DF` DeviceUnavailable (radio off).

## Presence
- Classic: `DeviceWatcher` over `BluetoothDevice.GetDeviceSelectorFromPairingState(true)` + props `System.Devices.Aep.IsConnected`, `System.Devices.Aep.DeviceAddress`; name filter contains `MOMENTUM`; `BluetoothDevice.ConnectionStatusChanged` on the active target; `Windows.Devices.Radios` for radio state.
- BLE: `BluetoothLEAdvertisementWatcher` (Active), local name `LE-MOMENTUM 5`; advisory only.
- Pairing in Windows Settings is mandatory (unpaired → 0x80070490/0x8007274D).

## BLE GATT gotchas (explorer)
`FromIdAsync` creates object without connecting; use `GattSession.MaintainConnection` or uncached discovery; subscribing to encrypted characteristics triggers Windows auto-pairing; use `BluetoothCacheMode.Uncached`; `AccessDenied` → `DeviceAccessInformation`. GAIA-over-GATT (if present): service `00001100-d102-11e1-9b23-00025b00a5a5`, command char `…1101` (write **without** response only), notify `…1102`, data `…1103`. Bare `vendor+cmd+payload`.

## Contingency: Winsock RFCOMM (T-04, only if G1 fails)
`AF_BTH`/`SOCK_STREAM`/`BTHPROTO_RFCOMM`, `SOCKADDR_BTH{btAddr, serviceClassId=GUID, port=0}` (SDP resolves channel on connect). Generate bindings with CsWin32.

## Coexistence facts
RFCOMM GAIA runs alongside A2DP (same controller, separate channel). RFCOMM during USB-C audio = open question (R-05 Q1, gate G4). HFP battery fallback: devnode property `{104EA319-6E3C-4A49-AB25-2E9B1A11E929},2` on the Hands-Free AG devnode (cfgmgr32), values match GAIA battery.
