# One-click display mode switcher with feedback for a monitor you can't see.
#   switch.ps1 -Mode 4k                 (mode defined in agent.config)
#   switch.ps1 -ProfileName 4k -Fallback ultrawide -AudioDevice "LG TV SSCR2"   (legacy form)
# Behavior:
#   - Snapshots the current layout as a revert point (unless -Fallback names one).
#   - Applies the requested profile in one atomic call (no confirmation dialogs)
#     and verifies the resulting layout.
#   - If the mode has an audio device, the default audio output is then switched to
#     the first active device whose name contains that text (waits up to 20s for the
#     endpoint to appear, since HDMI audio comes alive shortly after the display).
#   - Success: two rising beeps, logged to switch.log, exit 0. If only the audio
#     step failed, the success beeps are followed by one long low beep.
#   - Failure: re-applies the revert point so you get your previous screens back,
#     three low beeps, details in switch.log, exit 1.
#   - -DryRun: validates the profile without changing anything.

param(
    [string]$Mode = "",
    [string]$ProfileName = "",
    [string]$Fallback = "",
    [string]$AudioDevice = "",
    [switch]$NoSound,
    [switch]$DryRun
)

. (Join-Path $PSScriptRoot 'common.ps1')
$log = Join-Path $DS_Dir "switch.log"

function Write-Log([string]$msg) {
    if ($DryRun) { Write-Output $msg; return }
    $line = ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg)
    Add-Content -Path $log -Value $line
}

function Beep-Success { if (-not $NoSound) { try { [console]::beep(880, 150); [console]::beep(1320, 250) } catch {} } }
function Beep-Failure { if (-not $NoSound) { try { 1..3 | ForEach-Object { [console]::beep(220, 350); Start-Sleep -Milliseconds 120 } } catch {} } }
function Beep-AudioWarn { if (-not $NoSound) { try { Start-Sleep -Milliseconds 250; [console]::beep(330, 600) } catch {} } }

if ($Mode -ne "") {
    $def = Get-DSMode $Mode
    if (-not $def) {
        Write-Log "---- Switch requested: mode '$Mode' ----"
        Write-Log "FAILED: no mode '$Mode' in agent.config."
        Beep-Failure
        exit 1
    }
    $ProfileName = $def.Profile
    if ($AudioDevice -eq "") { $AudioDevice = $def.Audio }
}
if ($ProfileName -eq "") {
    Write-Log "FAILED: switch.ps1 needs -Mode or -ProfileName."
    Beep-Failure
    exit 1
}

if ($DryRun) {
    $v = Invoke-Tool $DS_MonExe @("validate", $ProfileName)
    Write-Output "Mode '$Mode' -> profile '$ProfileName', audio '$AudioDevice'"
    if ($v.StdOut) { Write-Output $v.StdOut }
    if ($v.StdErr) { Write-Output ("ERR: " + $v.StdErr) }
    exit $v.Code
}

Write-Log "---- Switch requested: '$ProfileName' (mode: '$Mode', audio: '$AudioDevice') ----"

if ($Fallback -eq "") {
    $snap = Invoke-Tool $DS_MonExe @("save", "_previous")
    if ($snap.Code -eq 0) {
        $Fallback = "_previous"
        Write-Log "Saved the current layout as the revert point."
    } else {
        Write-Log "WARNING: could not snapshot the current layout; no automatic revert available. $($snap.StdErr)"
    }
}

$r = Invoke-Tool $DS_MonExe @("load", $ProfileName)
if ($r.StdOut) { Write-Log $r.StdOut }
if ($r.StdErr) { Write-Log ("ERR: " + $r.StdErr) }

if ($r.Code -eq 0) {
    Write-Log "SUCCESS: now on '$ProfileName'."

    $audioFailed = $false
    if ($AudioDevice -ne "") {
        Write-Log "Setting default audio output to match '$AudioDevice'..."
        $a = Invoke-Tool $DS_AudExe @("set", $AudioDevice, "20")
        if ($a.StdOut) { Write-Log $a.StdOut }
        if ($a.StdErr) { Write-Log ("ERR: " + $a.StdErr) }
        if ($a.Code -eq 0) {
            Write-Log "Audio output switched OK."
        } else {
            $audioFailed = $true
            Write-Log "WARNING: audio switch failed (exit $($a.Code)). Display switch itself succeeded."
        }
    }

    Beep-Success
    if ($audioFailed) { Beep-AudioWarn }
    exit 0
}

Write-Log "FAILED switching to '$ProfileName' (exit $($r.Code))."

if ($Fallback -ne "" -and $Fallback -ne $ProfileName) {
    Write-Log "Reverting to '$Fallback'..."
    $f = Invoke-Tool $DS_MonExe @("load", $Fallback)
    if ($f.StdOut) { Write-Log $f.StdOut }
    if ($f.StdErr) { Write-Log ("ERR: " + $f.StdErr) }
    if ($f.Code -eq 0) { Write-Log "Reverted to '$Fallback' OK." }
    else { Write-Log "Revert ALSO failed (exit $($f.Code)). Displays may be in an unexpected state." }
}

Beep-Failure
exit 1
