# Root path for R-01/R-04. Run once:  .\tools\root-setup.ps1   (grant the su prompt on the phone if shown)
$ErrorActionPreference = 'Stop'
$pkg = 'com.sonova.chb.control'
$d = Join-Path $PSScriptRoot '..\research\root'; New-Item -ItemType Directory -Force $d | Out-Null
function su($c) { adb shell "su -c '$c'" }
"uid: " + (su 'id')
# 1) enable full HCI snoop without Developer options, restart BT
su 'setprop persist.bluetooth.btsnooplogmode full'
su 'settings put global bluetooth_hci_log 1' | Out-Null
su 'svc bluetooth disable'; Start-Sleep 3; su 'svc bluetooth enable'; Start-Sleep 5
"snoop mode: " + (su 'getprop persist.bluetooth.btsnooplogmode')
"snoop files:"; su 'find /data/misc/bluetooth /data/log -name "btsnoop*" 2>/dev/null'
# 2) app private data (models/cache/prefs may be plaintext here)
adb exec-out "su -c 'tar -C /data/data -cf - $pkg'" > (Join-Path $d 'appdata.tar')
tar -xf (Join-Path $d 'appdata.tar') -C $d
"appdata -> research\root\$pkg"; Get-ChildItem (Join-Path $d $pkg) -Recurse -File | Measure-Object Length -Sum | Format-List Count,Sum
