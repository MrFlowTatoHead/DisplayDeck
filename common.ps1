# Shared helpers for the DisplaySwitch scripts (dot-source this file).
# The mode definitions live in agent.config, shared with SwitchAgent.exe:
#   mode.<id>.slot / label / subtitle / icon / profile / audio

$DS_Dir = $PSScriptRoot
$DS_Config = Join-Path $DS_Dir 'agent.config'
$DS_MonExe = Join-Path $DS_Dir 'MonitorProfile.exe'
$DS_AudExe = Join-Path $DS_Dir 'AudioSwitch.exe'
$DS_Switch = Join-Path $DS_Dir 'switch.ps1'
$DS_Utf8 = New-Object System.Text.UTF8Encoding($false)

function Invoke-Tool([string]$file, [string[]]$toolArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $file
    $psi.Arguments = ($toolArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $stdout = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    New-Object PSObject -Property @{ Code = $p.ExitCode; StdOut = $stdout.Trim(); StdErr = $errTask.Result.Trim() }
}

function Get-DSModes {
    $modes = @{}
    foreach ($raw in [System.IO.File]::ReadAllLines($DS_Config)) {
        $line = $raw.Trim()
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 0) { continue }
        $parts = $line.Substring(0, $eq).Trim().Split('.')
        $val = $line.Substring($eq + 1).Trim()
        if ($parts.Count -ne 3 -or $parts[0] -ne 'mode') { continue }
        $id = $parts[1].ToLowerInvariant()
        if (-not $modes.ContainsKey($id)) {
            $modes[$id] = New-Object PSObject -Property @{
                Id = $id; Slot = 99; Label = $id; Subtitle = ''; Icon = 'display'; Profile = $id; Audio = ''; Ready = $false
            }
        }
        $m = $modes[$id]
        $field = $parts[2].ToLowerInvariant()
        if ($field -eq 'slot') { $n = 0; if ([int]::TryParse($val, [ref]$n)) { $m.Slot = $n } }
        elseif ($field -eq 'label' -and $val -ne '') { $m.Label = $val }
        elseif ($field -eq 'subtitle') { $m.Subtitle = $val }
        elseif ($field -eq 'icon' -and $val -ne '') { $m.Icon = $val }
        elseif ($field -eq 'profile' -and $val -ne '') { $m.Profile = $val }
        elseif ($field -eq 'audio') { $m.Audio = $val }
    }
    foreach ($m in $modes.Values) {
        $m.Ready = Test-Path (Join-Path $DS_Dir "profiles\$($m.Profile).mon")
    }
    $modes.Values | Sort-Object Slot, Id
}

function Get-DSMode([string]$id) {
    Get-DSModes | Where-Object { $_.Id -eq $id.ToLowerInvariant() } | Select-Object -First 1
}

# Updates (or appends) key=value lines in agent.config, preserving everything else.
function Set-DSConfigValues($values) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.AddRange([string[]][System.IO.File]::ReadAllLines($DS_Config))
    foreach ($key in $values.Keys) {
        $found = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim().StartsWith("$key=", [System.StringComparison]::OrdinalIgnoreCase)) {
                $lines[$i] = "$key=$($values[$key])"
                $found = $true
            }
        }
        if (-not $found) { $lines.Add("$key=$($values[$key])") }
    }
    $tmp = "$DS_Config.tmp"
    [System.IO.File]::WriteAllLines($tmp, $lines.ToArray(), $DS_Utf8)
    Move-Item -Force $tmp $DS_Config
}

# "Odyssey G95C · 2560×1440 · 120 Hz" from the [PRIMARY] line of MonitorProfile output.
function Get-DSPrimarySummary([string]$monitorOutput) {
    foreach ($line in ($monitorOutput -split "`r?`n")) {
        $m = [regex]::Match($line, '^\s*-\s+(?<name>.+?)\s{2}(?<w>\d+)x(?<h>\d+) at \([-\d]+,[-\d]+\)\s+\[PRIMARY\](?:\s+(?<hz>[\d.]+) Hz)?')
        if ($m.Success) {
            $dot = [char]0x00B7
            $times = [char]0x00D7
            $text = "$($m.Groups['name'].Value) $dot $($m.Groups['w'].Value)$times$($m.Groups['h'].Value)"
            if ($m.Groups['hz'].Success) {
                $hz = [math]::Round([double]::Parse($m.Groups['hz'].Value, [System.Globalization.CultureInfo]::InvariantCulture))
                $text += " $dot $hz Hz"
            }
            return $text
        }
    }
    return ''
}

# Recreates one "Switch to <label>" shortcut per captured mode in $dir, removing
# any previous shortcuts that point at this switch.ps1 (handles renames).
function Update-DSShortcuts([string]$dir) {
    if ($dir -eq '') { $dir = [Environment]::GetFolderPath('Desktop') }
    $ws = New-Object -ComObject WScript.Shell
    foreach ($f in (Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -ErrorAction SilentlyContinue)) {
        $existing = $ws.CreateShortcut($f.FullName)
        if ($existing.Arguments.Contains($DS_Switch)) { Remove-Item -LiteralPath $f.FullName -Force }
    }
    $made = @()
    foreach ($m in (Get-DSModes | Where-Object { $_.Ready })) {
        $safe = ($m.Label -replace '[\\/:*?"<>|]', '').Trim()
        if ($safe -eq '') { $safe = $m.Id }
        $lnkPath = Join-Path $dir "Switch to $safe.lnk"
        $lnk = $ws.CreateShortcut($lnkPath)
        $lnk.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$DS_Switch`" -Mode $($m.Id)"
        $lnk.WorkingDirectory = $DS_Dir
        $lnk.WindowStyle = 7
        $lnk.IconLocation = "$env:SystemRoot\System32\DisplaySwitch.exe,0"
        $lnk.Save()
        $made += "Switch to $safe"
    }
    $made
}
