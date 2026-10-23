# Checks that the built-in reverb and the Windows component never run at the same time,
# and that both give a similar reverb level.
#
# A: component installed  -> page must NOT attach built-in reverb; Firefox output = dry clicks only,
#                            the component's own output carries the reverb tail.
# B: component hidden     -> page attaches built-in reverb; Firefox output carries the tail.
#
# Plays quiet clicks for ~1 minute. Usage: powershell -File tools\conflict-test.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "dist\nightverb.exe"
$ff = "C:\Program Files\Mozilla Firefox\firefox.exe"
$key = "HKCU:\Software\Mozilla\NativeMessagingHosts\nightverb"
$log = Join-Path $env:TEMP "nightverb-webext.log"

function Start-TestFirefox {
  Start-Process -FilePath "cmd.exe" -WindowStyle Hidden -ArgumentList ("/c npx --yes web-ext@8.3.0 run --source-dir `"$root\extension`" " +
    "--firefox=`"$ff`" --pref media.autoplay.default=0 --start-url http://127.0.0.1:8737/reverb-test.html --no-input --no-reload > `"$log`" 2>&1")
  $deadline = (Get-Date).AddSeconds(100)
  while ((Get-Date) -lt $deadline) {
    $w = Get-Process firefox -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "NVR media*" } | Select-Object -First 1
    if ($w) { return $w }
    Start-Sleep -Milliseconds 1000
  }
  throw "test page did not report"
}

function Stop-TestFirefox {
  Get-Process firefox -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "NVR*" } | Stop-Process -Force
  Start-Sleep -Seconds 2
  Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -like "*web-ext*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Listen to one process for 5 s; return peak per 0.5 s slice.
function Get-Levels([int]$procId) {
  $out = & $exe --probe 5 --pid $procId | Out-String
  return [regex]::Matches($out, "(\d+\.\d{3})\s+\(") | ForEach-Object { [double]$_.Groups[1].Value }
}

# Level between clicks: slices that don't contain a click (< 0.1), loudest of them.
function Get-GapLevel($levels) { ($levels | Where-Object { $_ -lt 0.1 } | Measure-Object -Maximum).Maximum }
function Get-Db($v) { if ($v -le 0) { return "-inf" } ; "{0:N1} dB" -f (20 * [math]::Log10($v)) }

$saved = (Get-ItemProperty $key).'(default)'
$server = Start-Process node -ArgumentList "`"$root\tools\serve.mjs`"" -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 800
$results = @{}
try {
  # ---------------- A: component installed ----------------
  & $exe --stop                      # fresh engine, so it follows the test Firefox
  Start-Sleep -Seconds 1
  $w = Start-TestFirefox
  Start-Sleep -Seconds 2
  $media = [regex]::Match($w.MainWindowTitle, 'media=(\{[^}]*\})').Groups[1].Value
  $engine = Get-CimInstance Win32_Process -Filter "Name='nightverb.exe'" | Where-Object { $_.CommandLine -like "*--engine*" } | Select-Object -First 1
  "A) component installed"
  "   page report: $media"
  if (-not $engine) { "   ENGINE NOT RUNNING" } else {
    $ffLv = Get-Levels $w.Id
    $engLv = Get-Levels $engine.ProcessId
    "   Firefox output  : $($ffLv -join ' ')"
    "   component output: $($engLv -join ' ')"
    $results.A = @{ media = $media; ffGap = (Get-GapLevel $ffLv); engTail = ($engLv | Measure-Object -Maximum).Maximum; ff = $ffLv; eng = $engLv }
  }
  Stop-TestFirefox

  # ---------------- B: component hidden ----------------
  Remove-Item $key -Force
  $w = Start-TestFirefox
  Start-Sleep -Seconds 2
  $media = [regex]::Match($w.MainWindowTitle, 'media=(\{[^}]*\})').Groups[1].Value
  $ffLv = Get-Levels $w.Id
  "B) component hidden (built-in reverb)"
  "   page report: $media"
  "   Firefox output  : $($ffLv -join ' ')"
  $results.B = @{ media = $media; ffGap = (Get-GapLevel $ffLv); levels = $ffLv }
  Stop-TestFirefox

  # ---------------- C: component disappears while playing ----------------
  New-Item $key -Force | Out-Null; Set-ItemProperty $key -Name "(default)" -Value $saved
  & $exe --stop
  Start-Sleep -Seconds 1
  $w = Start-TestFirefox
  $started = Get-Date
  Start-Sleep -Seconds 3
  $before = Get-Levels $w.Id
  Remove-Item $key -Force          # "uninstalled"
  & $exe --stop                    # and its engine is gone
  "C) component removed while playing; waiting for the once-a-minute health check..."
  $wait = 75 - ((Get-Date) - $started).TotalSeconds
  if ($wait -gt 0) { Start-Sleep -Seconds ([int]$wait) }
  $after = Get-Levels $w.Id
  "   Firefox output before removal: $($before -join ' ')"
  "   Firefox output ~75 s later   : $($after -join ' ')"
  $results.C = @{ before = (Get-GapLevel $before); after = (Get-GapLevel $after) }
  Stop-TestFirefox
}
finally {
  if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null; Set-ItemProperty $key -Name "(default)" -Value $saved }
  Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
}

""
"===== verdict ====="
$a = $results.A; $b = $results.B
if ($a) {
  $noDouble = ($a.media -match '"builtin":0') -and ($a.ffGap -lt 0.003)
  "A no double reverb (built-in reports 0, Firefox gaps $(Get-Db $a.ffGap)): $noDouble"
  "A component adds the tail (its output peak $(Get-Db $a.engTail)): $($a.engTail -gt 0.005)"
}
if ($b) {
  "B built-in reverb active (reports 3, Firefox gaps $(Get-Db $b.ffGap)): $(($b.media -match '"builtin":3') -and ($b.ffGap -gt 0.005))"
}
if ($a -and $b) {
  # Whole-window tail level, so it doesn't matter where in a slice a click lands.
  # Component output is pure reverb. For built-in, Firefox output = clicks + reverb, so each
  # click slice is swapped for the component's matching slice (the early tail hidden under the click).
  $sumA = ($a.eng | Measure-Object -Sum).Sum
  $clickA = ($a.eng | Sort-Object -Descending | Select-Object -First 2 | Measure-Object -Sum).Sum
  $sumB = ($b.levels | Where-Object { $_ -lt 0.1 } | Measure-Object -Sum).Sum + $clickA
  $diff = 20 * [math]::Log10($sumA / $sumB)
  "A/B live level, rough (component $(Get-Db $sumA), built-in $(Get-Db $sumB), diff {0:N1} dB); exact check:" -f $diff
  Push-Location $root
  node tools\calibrate-builtin.mjs | Select-Object -Last 1 | ForEach-Object { "   calibration: $_" }
  Pop-Location
}
if ($results.C) {
  $c = $results.C
  "C takes over when the component vanishes (gaps before $(Get-Db $c.before), after $(Get-Db $c.after)): $($c.after -gt 0.005)"
}
