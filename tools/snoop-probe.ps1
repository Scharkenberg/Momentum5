$sh = Join-Path $PSScriptRoot 'snoop-probe.sh'
adb push $sh /data/local/tmp/snoop-probe.sh | Out-Null
adb shell "su -c 'sh /data/local/tmp/snoop-probe.sh'" | Tee-Object (Join-Path $PSScriptRoot '..\research\snoop-probe.txt')
