# Registers the "DisplaySwitch Agent" scheduled task (no admin needed):
#   - starts SwitchAgent.exe when you sign in
#   - re-checks every 5 minutes and relaunches it if it has stopped for any reason
#     (a copy that finds the agent already running exits immediately)
# Runs in your interactive session, which display switching requires.
# Use -Start to (re)launch the agent through the task right away.

param([switch]$Start)

$dir = $PSScriptRoot
$exe = Join-Path $dir 'SwitchAgent.exe'
$user = "$env:USERDOMAIN\$env:USERNAME"
$name = 'DisplaySwitch Agent'

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dir
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -Priority 5
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $atLogon, $watchdog -Settings $settings `
    -Principal $principal -Force `
    -Description "Runs the DisplaySwitch phone-control agent ($dir) and relaunches it if it stops." | Out-Null
Write-Output "Registered scheduled task '$name' (at sign-in + every 5 minutes)."

if ($Start) {
    Start-ScheduledTask -TaskName $name
    Write-Output "Started the agent through the task."
}
