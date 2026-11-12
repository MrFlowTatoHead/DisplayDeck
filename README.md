# DisplaySwitch

One-click (or one-tap, from the iPhone) switching between saved display modes,
replacing the manual Windows Settings ritual. Three slots out of the box:

| Slot | Mode        | Layout                                  | Audio               |
|------|-------------|-----------------------------------------|---------------------|
| 1    | Ultrawide   | Odyssey G95C 5120×1440 @ 240 Hz         | left alone          |
| 2    | 4K Theater  | LG TV SSCR2 3840×2160 @ 120 Hz          | switches to the TV  |
| 3    | (yours)     | whatever you capture                    | your choice         |

Built around `MonitorProfile.exe` (source: `MonitorProfile.cs`, compiles with the
stock Windows csc), which saves/restores complete display topologies via the
Win32 CCD API (`QueryDisplayConfig` / `SetDisplayConfig`). A profile captures
which monitors are active, the primary monitor, resolution, refresh rate,
rotation, and positions — and applies all of it in ONE atomic call. No
intermediate primary-monitor dance, no "Keep these display settings?" dialog.
Profiles survive reboots (adapter LUIDs are remapped by stable device path).

## Setting up from a fresh clone

1. Clone this repo to the folder the tools will run from.
2. Run `build.cmd` (uses the C# compiler built into Windows — nothing to install).
3. Copy `agent.config.example` to `agent.config` and set `token=` to a long
   random string (the iPhone app needs the same value).
4. Capture each display mode with `Capture-Display-Mode.cmd` (below).
5. Right-click `SETUP-remote.cmd` → Run as administrator (firewall rule for the
   local subnet + the auto-start/watchdog task).
6. Build the iPhone app — see `ios-app/README.md`.

`agent.config`, `profiles\`, logs, `*.exe`, and the app's `Secrets.swift` are
git-ignored: they hold machine-specific data and the auth token.

## Capturing a layout into a slot

1. Arrange your displays the way you want them (Windows Settings, NVIDIA panel…).
2. Double-click **`Capture-Display-Mode.cmd`**.
3. Pick the slot number, give the mode a name, and choose which audio output to
   select when switching to it (or leave audio alone).

It saves and validates the layout, updates `agent.config`, regenerates the
desktop shortcuts ("Switch to <name>"), and the iPhone app picks up the change
within a few seconds — no restarts or rebuilds. Re-running it on an existing
slot replaces that slot (it asks first).

## Daily use

- Desktop: one **Switch to <mode>** shortcut per captured slot.
- iPhone: DisplayDeck app (see below).
- Feedback (for a screen you can't see):
  - Success = two rising beeps; the screen you launched from goes dark and stays dark.
  - Failure = the switch is automatically reverted to exactly the layout you
    had before (snapshotted as `profiles\_previous.mon` right before every
    switch), three low beeps, and the reason is written to `switch.log`.
  - Display OK but audio failed = success beeps followed by one long low beep.

## How a mode is defined (agent.config)

```
mode.slot3.slot=3                 order shown in the app / capture menu
mode.slot3.label=Gaming 120       name (shortcut + app card)
mode.slot3.subtitle=...           filled in by the capture tool
mode.slot3.icon=display.2         SF Symbol shown on the iPhone
mode.slot3.profile=slot3          layout file: profiles\slot3.mon
mode.slot3.audio=                 part of an audio output name; blank = leave audio alone
```

The agent re-reads this file automatically when it changes. To add a fourth
slot, copy a block with a new id and `slot=4`, then capture into it.

"Which mode is active" is decided by comparing the live layout to every saved
profile: same monitors, same resolution, same refresh rate (±1 Hz), same primary.
A layout that matches no slot shows as "Custom layout" on the phone.

## Audio (AudioSwitch.exe)

`AudioSwitch.exe` (source: `AudioSwitch.cs`) sets the Windows default audio
output via the MMDevice/IPolicyConfig COM APIs. After a successful display
switch, switch.ps1 makes the first active device matching the mode's `audio`
text the default (Console + Multimedia roles), retrying up to 20 s while an HDMI
endpoint wakes up.

## Bring Steam to the main screen (WindowMover.exe)

`WindowMover.exe` (source: `WindowMover.cs`) moves Steam's window to the primary
monitor and maximizes it — the DisplayDeck **Bring Steam Here** button calls it
through the agent. It picks Big Picture if that's open, otherwise the main
"Steam" window; if Steam has no window (tray only) or isn't running, it asks
Steam to open its main window first. Big Picture / borderless-fullscreen windows
are moved to cover the primary monitor exactly; normal windows are restored,
moved, and maximized. It verifies the result and brings Steam to the front.
Results are appended to `switch.log`.

```
WindowMover.exe steam               # do it now
WindowMover.exe steam --dry-run     # show what it would do
WindowMover.exe list                # every visible window: process, title, monitor
WindowMover.exe move <process>      # same treatment for any app (e.g. move discord)
```

## Commands

```
MonitorProfile.exe status           # show active displays
MonitorProfile.exe current          # which saved profile matches right now (MATCH x / NONE)
MonitorProfile.exe show <name>      # what a saved profile contains
MonitorProfile.exe save <name>      # snapshot current layout
MonitorProfile.exe load <name>      # apply a profile (verifies the full layout)
MonitorProfile.exe validate <name>  # dry-run without changing anything
MonitorProfile.exe list             # list saved profiles
AudioSwitch.exe list                # show audio outputs + current default
AudioSwitch.exe set "LG TV" 15      # set default audio output (retry up to 15s)
powershell -File switch.ps1 -Mode slot3            # what shortcuts and the agent run
powershell -File switch.ps1 -Mode slot3 -DryRun    # validate without switching
powershell -File capture.ps1 -Slot 3 -Label "Gaming" -Audio none -Yes   # unattended capture
```

Exit codes for `load`: 0 = applied+verified, 2 = apply rejected, 3 = applied but
layout mismatch, 4 = profile not found, 5 = GPU/adapter missing.

The legacy form `switch.ps1 -ProfileName 4k -Fallback ultrawide -AudioDevice "LG TV SSCR2"`
still works (e.g. for DisplayFusion functions set up before modes existed).

## Remote control from iPhone (DisplayDeck)

`SwitchAgent.exe` (source: `SwitchAgent.cs`) is a tiny HTTP agent that lets the
DisplayDeck iOS app (Xcode project in `ios-app\`) trigger switches over the LAN.
It reads `agent.config`, runs `switch.ps1 -Mode <id>` synchronously, and only
replies once the whole switch is finished and verified — so the phone shows the
true outcome.

- One-time setup: right-click `SETUP-remote.cmd` → Run as administrator
  (firewall rule scoped to the local subnet + the scheduled task below).
- Always running: the **DisplaySwitch Agent** scheduled task (Task Scheduler)
  starts the agent at sign-in and checks every 5 minutes, relaunching it if it
  has stopped for any reason; a copy that finds the agent already running exits
  silently. Re-register with `install-agent-task.ps1` (no admin needed). To
  restart the agent: end SwitchAgent.exe in Task Manager and run
  `Start-ScheduledTask 'DisplaySwitch Agent'` (or wait up to 5 minutes).
  Automatic sign-in is on, so this survives reboots and update restarts.
  Each start is logged in `agent.log` with what launched it.
- Endpoints: `GET /ping` (no token), `GET /status` (active mode + mode list),
  `GET /log`, `POST /switch/<mode id>`, `POST /steam` (`?dryrun=1` to only
  report) — token via `X-Auth-Token` header or `?token=`.
- Quick test from any browser on the LAN: `http://<your PC's IP>:5990/ping`.
- Logs: `agent.log` (requests), `switch.log` (switch results).

## Optional: global hotkeys via DisplayFusion

DisplayFusion (installed at `C:\Program Files (x86)\DisplayFusion`) can bind a
key to each mode: Settings > Functions > Add Custom Function ("Run
Application"), pointing at the same powershell command line the desktop
shortcuts use (`... switch.ps1" -Mode <id>`).

## Notes

- HDR state is not part of the CCD topology; if a mode needs HDR toggled after
  switching, that can be added as an extra step in switch.ps1.
- `agent.config.bak` is the pre-slots config, kept for reference.
