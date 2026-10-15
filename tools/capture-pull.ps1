# Pull the newest HCI snoop logs (Android 16 names them btsnoop_hci_YYMMDD_HHMMSS.log) -> research/cap/snoop/
param([int]$Last = 3)
$d = Join-Path $PSScriptRoot '..\research\cap\snoop'; New-Item -ItemType Directory -Force $d | Out-Null
$dir = '/data/misc/bluetooth/logs'
$names = @(adb shell "su -c 'ls $dir'") | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^btsnoop_hci_.*\.log$' } | Sort-Object | Select-Object -Last $Last
if (-not $names) { throw "no btsnoop files in $dir" }
foreach ($n in $names) {
  adb exec-out "su -c 'cat $dir/$n'" > (Join-Path $d $n)
  "$n  $([int]((Get-Item (Join-Path $d $n)).Length/1KB)) KB"
}
