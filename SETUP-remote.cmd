@echo off
rem ONE-TIME SETUP for phone remote control. Right-click this file and choose
rem "Run as administrator". It:
rem   1. Opens firewall port 5990 for SwitchAgent.exe (local subnet ONLY - the
rem      agent is never reachable from outside your home network).
rem   2. Registers the "DisplaySwitch Agent" scheduled task: starts the agent when
rem      you sign in and relaunches it within 5 minutes if it ever stops.
rem   3. (Re)starts the agent now, through that task.

net session >nul 2>&1
if errorlevel 1 (
    echo This script must be run as administrator.
    echo Right-click SETUP-remote.cmd and choose "Run as administrator".
    pause
    exit /b 1
)

echo Adding firewall rule (local subnet only)...
netsh advfirewall firewall delete rule name="DisplaySwitch Agent" >nul 2>&1
netsh advfirewall firewall add rule name="DisplaySwitch Agent" dir=in action=allow protocol=TCP localport=5990 remoteip=localsubnet program="%~dp0SwitchAgent.exe"
if errorlevel 1 (
    echo FAILED to add the firewall rule.
    pause
    exit /b 1
)

echo Registering the auto-start + watchdog task and restarting the agent...
taskkill /f /im SwitchAgent.exe >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-agent-task.ps1" -Start
if errorlevel 1 (
    echo FAILED to register the scheduled task.
    pause
    exit /b 1
)

echo.
echo Done. The agent is running, starts automatically at sign-in, and is
echo relaunched within 5 minutes if it ever stops.
echo Test from your phone's browser: http://YOUR-PC-IP:5990/ping  (should say "pong")
pause
