@echo off
rem Set up your displays the way you want them, then double-click this.
rem It saves the current layout into a mode slot (Ultrawide / 4K / slot 3),
rem lets you name it and pick its audio output, and updates the desktop
rem shortcuts and the iPhone app automatically.
title DisplaySwitch - Capture current layout
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0capture.ps1"
