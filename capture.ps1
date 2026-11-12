# Capture the current display layout into one of the DisplaySwitch mode slots.
# Normally launched by double-clicking Capture-Display-Mode.cmd (interactive).
# Unattended form:  capture.ps1 -Slot 3 -Label "Gaming" -Audio none -Yes
#   -Audio: part of an audio output name, "none" = leave audio alone, omit = keep current

param(
    [int]$Slot = 0,
    [string]$Label = "",
    [string]$Audio = "",
    [switch]$Yes,
    [string]$ShortcutDir = ""
)

. (Join-Path $PSScriptRoot 'common.ps1')

function Finish([int]$code) {
    if (-not $Yes) { Write-Host ""; Read-Host "  Press Enter to close" | Out-Null }
    exit $code
}

Write-Host ""
Write-Host "  DisplaySwitch  -  capture current layout" -ForegroundColor Cyan
Write-Host ""

$status = Invoke-Tool $DS_MonExe @("status")
if ($status.Code -ne 0) {
    Write-Host "  Could not read the current display layout: $($status.StdErr)" -ForegroundColor Red
    Finish 1
}
Write-Host "  Current layout:"
foreach ($line in ($status.StdOut -split "`r?`n" | Where-Object { $_ -match '^\s*-\s' })) {
    Write-Host "    $($line.Trim())" -ForegroundColor White
}
$summary = Get-DSPrimarySummary $status.StdOut

$modes = @(Get-DSModes)
Write-Host ""
Write-Host "  Slots:"
foreach ($m in $modes) {
    $detail = if ($m.Ready) { $m.Subtitle } else { "(empty)" }
    Write-Host ("    [{0}]  {1,-16} {2}" -f $m.Slot, $m.Label, $detail)
}
Write-Host ""

if ($Slot -eq 0) {
    $answer = (Read-Host "  Save the current layout into which slot? (number, Enter to cancel)").Trim()
    if ($answer -eq '') { Write-Host "  Cancelled - nothing changed."; Finish 1 }
    [int]::TryParse($answer, [ref]$Slot) | Out-Null
}
$target = $modes | Where-Object { $_.Slot -eq $Slot } | Select-Object -First 1
if (-not $target) {
    Write-Host "  There is no slot '$Slot'. Nothing changed." -ForegroundColor Red
    Finish 1
}

if ($target.Ready -and -not $Yes) {
    $ok = (Read-Host "  Slot $Slot ('$($target.Label)') already holds a layout. Replace it? (y/N)").Trim()
    if ($ok -notmatch '^(y|yes)$') { Write-Host "  Cancelled - nothing changed."; Finish 1 }
}

if ($Label -eq '' -and -not $Yes) {
    $Label = (Read-Host "  Name for this mode [$($target.Label)]").Trim()
}
if ($Label -eq '') { $Label = $target.Label }

if ($Audio -eq '' -and -not $Yes) {
    $outputs = @()
    $list = Invoke-Tool $DS_AudExe @("list")
    foreach ($line in ($list.StdOut -split "`r?`n")) {
        $m = [regex]::Match($line, '^\s*-\s+(?<name>.+?)\s{2}\(active\)(?<def> \[DEFAULT\])?')
        if ($m.Success) { $outputs += New-Object PSObject -Property @{ Name = $m.Groups['name'].Value; Default = $m.Groups['def'].Success } }
    }
    Write-Host ""
    Write-Host "  Audio outputs available right now:"
    for ($i = 0; $i -lt $outputs.Count; $i++) {
        $mark = if ($outputs[$i].Default) { "  (current default)" } else { "" }
        Write-Host ("    [{0}]  {1}{2}" -f ($i + 1), $outputs[$i].Name, $mark)
    }
    $keep = if ($target.Audio) { "'$($target.Audio)'" } else { "leave audio alone" }
    Write-Host ""
    Write-Host "  When switching to this mode, which audio output should be selected?"
    $answer = (Read-Host "  Number, part of a device name, '-' to leave audio alone, or Enter to keep ($keep)").Trim()
    $n = 0
    if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $outputs.Count) {
        $Audio = $outputs[$n - 1].Name
    } else {
        $Audio = $answer
    }
}
if ($Audio -eq '') { $Audio = $target.Audio }
elseif ($Audio -eq '-' -or $Audio -eq 'none') { $Audio = '' }

Write-Host ""
Write-Host "  Saving..."
$save = Invoke-Tool $DS_MonExe @("save", $target.Profile)
if ($save.Code -ne 0) {
    Write-Host "  FAILED to save the layout: $($save.StdErr)" -ForegroundColor Red
    Finish 1
}
$check = Invoke-Tool $DS_MonExe @("validate", $target.Profile)
if ($check.Code -ne 0) {
    Write-Host "  WARNING: Windows did not accept the saved layout on a dry run: $($check.StdErr)" -ForegroundColor Yellow
}

Set-DSConfigValues ([ordered]@{
    "mode.$($target.Id).label"    = $Label
    "mode.$($target.Id).subtitle" = $summary
    "mode.$($target.Id).audio"    = $Audio
})

$shortcuts = @(Update-DSShortcuts $ShortcutDir)

$audioText = if ($Audio) { "select '$Audio'" } else { "leave audio alone" }
Write-Host ""
Write-Host "  Done. Slot $Slot is now '$Label'." -ForegroundColor Green
Write-Host "    Layout:  $summary"
Write-Host "    Audio:   $audioText"
Write-Host "    Desktop shortcuts: $($shortcuts -join ', ')"
Write-Host "    Your phone picks this up automatically within a few seconds."
Finish 0
