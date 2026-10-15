# Guided R-04 capture. Usage: .\tools\capture-log.ps1 [-Tier 1]  (1 = essentials, 2 = +extras, 3 = +restarts)
# For each step: read it, press Enter, THEN do it on the phone. s = skip, q = quit.
# Marks use the PHONE clock -> research/cap/actions-plan.csv (`epoch_ms,id`).
param([int]$Tier = 1,
      [string]$Plan = (Join-Path $PSScriptRoot '..\docs\capture-plan.txt'),
      [string]$Out = (Join-Path $PSScriptRoot '..\research\cap\actions-plan.csv'))
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$steps = Get-Content $Plan | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object {
  $p = $_ -split '\|', 3; [pscustomobject]@{ Tier = [int]$p[0]; Id = $p[1]; Text = $p[2] } } | Where-Object { $_.Tier -le $Tier }
$i = 0
foreach ($s in $steps) {
  $i++
  $a = Read-Host ("[{0}/{1}] {2}  >>  {3}   (Enter = go, s = skip, q = quit)" -f $i, $steps.Count, $s.Id, $s.Text)
  if ($a -eq 'q') { break }
  if ($a -eq 's') { continue }
  $t = [int64]((adb shell 'date +%s%3N').Trim())
  "$t,$($s.Id)" | Add-Content -Encoding utf8 $Out
}
'done - now run .\tools\capture-pull.ps1'
