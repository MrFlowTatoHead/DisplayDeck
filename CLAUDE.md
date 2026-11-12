# DisplayDeck / DisplaySwitch — notes for Claude

This folder is both the git repo and the **live install**: the scheduled task,
firewall rule, and desktop shortcuts all point at files here. Edits take effect
for real.

## Layout
- `MonitorProfile.cs` — save/load/verify display topologies (CCD API).
- `AudioSwitch.cs` — set the default audio output (IPolicyConfig).
- `WindowMover.cs` — move a window (Steam) to the primary monitor and maximize it.
- `SwitchAgent.cs` — LAN HTTP agent the iPhone app talks to (port 5990, token auth).
- `common.ps1`, `switch.ps1`, `capture.ps1` — mode switching and slot capture;
  modes live in `agent.config` (template: `agent.config.example`).
- `ios-app/` — DisplayDeck SwiftUI app (iPhone-only, iOS 17+), hand-written
  Xcode project.

## Building
- `build.cmd` compiles all four tools with the Windows-bundled csc
  (`Framework64\v4.0.30319`). That compiler is **C# 5 only**: no string
  interpolation, `?.`, `nameof`, expression-bodied members, or `out var`.
- Swift can't be compiled on this Windows machine. Review Swift changes by hand
  and stick to APIs the app already uses.
- Windows PowerShell 5.1 reads BOM-less .ps1 files as ANSI: keep non-ASCII
  characters out of .ps1 literals (use `[char]0x00B7` etc.). Read config files
  with `[IO.File]::ReadAllLines` (UTF-8).

## Running the agent
- The **"DisplaySwitch Agent" scheduled task** owns SwitchAgent.exe: it starts it
  at sign-in and relaunches it every 5 minutes if it has stopped
  (`install-agent-task.ps1`).
- **Never launch SwitchAgent.exe from a Claude shell.** Processes started there
  join the Claude app's job object and are killed when the app restarts, which
  silently takes the phone app offline. Restart it with
  `schtasks /run /tn "DisplaySwitch Agent"` (build.cmd does this).
- Stop processes by PID after checking `ExecutablePath`, never by name: a scratch
  test copy and the live agent share the same image name.

## Testing without disrupting the user
- The primary display may be a TV in another room that's in use (games,
  video). Don't steal focus or put windows on the primary display while testing.
- Prefer dry runs: `switch.ps1 -Mode <id> -DryRun`,
  `WindowMover.exe steam --dry-run`, `POST /steam?dryrun=1`,
  `MonitorProfile.exe validate <name>`.
- For agent tests, run a scratch copy with `bind=127.0.0.1` and a different
  port in its agent.config.

## Public repo hygiene
Never commit `agent.config`, `ios-app/DisplayDeck/Secrets.swift`, logs, or
`profiles/` (all git-ignored). They hold the auth token and machine-specific
data.
