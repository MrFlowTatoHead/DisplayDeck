@echo off
rem Builds the four tools from source with the C# compiler that ships with Windows
rem (.NET Framework 4.x - C# 5 language level). The running agent is stopped for
rem the rebuild and restarted through its scheduled task afterwards.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
cd /d "%~dp0"

"%CSC%" /nologo /optimize+ /out:MonitorProfile.exe MonitorProfile.cs || goto :fail
"%CSC%" /nologo /optimize+ /out:AudioSwitch.exe AudioSwitch.cs || goto :fail
"%CSC%" /nologo /optimize+ /out:WindowMover.exe WindowMover.cs || goto :fail

taskkill /f /im SwitchAgent.exe >nul 2>&1
"%CSC%" /nologo /optimize+ /target:winexe /r:System.Management.dll /out:SwitchAgent.exe SwitchAgent.cs || goto :fail
schtasks /run /tn "DisplaySwitch Agent" >nul 2>&1

echo Built MonitorProfile.exe, AudioSwitch.exe, WindowMover.exe and SwitchAgent.exe.
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
