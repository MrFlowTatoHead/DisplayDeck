# DisplayDeck — iPhone remote for the DisplaySwitch rig

One-tap switching between your saved display modes (Ultrawide, 4K Theater, and
slot 3) from your phone, with live status so you always know which mode the PC
is actually in. The app waits for the PC to finish and verify the whole switch
(display + audio) before reporting success.

The mode cards come from the PC: capturing or renaming a slot with
`Capture-Display-Mode.cmd` updates the app within a few seconds — no rebuild.

This folder is a **complete, ready-to-open Xcode project**, already configured
as an iPhone-only iOS app (iOS 17+, no Mac Catalyst, no "Designed for iPad").
Do NOT create a new project from an Xcode template — that's how a Mac/desktop
target sneaks in. Just open this one.

## Build & run

1. Clone the repo on your Mac (GitHub Desktop → Clone, or
   `git clone https://github.com/MrFlowTatoHead/DisplayDeck.git`).
2. **One-time:** in `ios-app/DisplayDeck/`, copy `Secrets.example.swift` to
   `Secrets.swift` and fill in your PC's name (or IP) and the `token=` value
   from `agent.config` on the PC. `Secrets.swift` is git-ignored, so the token
   never goes to GitHub. (If Xcode says "Secrets.swift cannot be found", this
   step was skipped.)
3. Double-click **DisplayDeck.xcodeproj**.
4. Project → target **DisplayDeck** → **Signing & Capabilities** → pick your
   Team. (Change the bundle identifier if it collides with one you own.)
5. Select **your iPhone** as the run destination — not "My Mac" and not a
   simulator — and Run.

To update later: pull in GitHub Desktop (or `git pull`) and Run again.
`Secrets.swift` and your signing choice stay put.

Already set up inside the project:
- iPhone-only target (`TARGETED_DEVICE_FAMILY = 1`), iOS 17 deployment target.
- `Info.plist` contains **Allows Local Networking** (ATS) and the **Local
  Network** privacy string.

## Using it

- Tap a card to switch; a spinner runs while the PC works (~10–30 s when an
  HDMI audio handover is involved).
- Success: haptic tap, card lights up ACTIVE — verified by the PC, not assumed.
- Failure: the PC reverts to exactly the layout you had before, and the app
  shows why; the list icon opens the PC's switch.log.
- **Bring Steam Here** (below the cards) moves Steam's window — Big Picture if
  it's open, otherwise the main Steam window — to the PC's current main screen,
  maximizes it, and brings it to the front. If Steam is hidden in the tray, the
  PC opens its window first. A green check confirms which screen it landed on.
- A slot that hasn't been captured yet shows as EMPTY and can't be tapped.
- "Custom layout — not saved to a slot" means the PC's current layout doesn't
  match any slot; every captured card is tappable.
- Pull down to refresh. Settings (gear) shows all slots and the connection.

## Troubleshooting

- Red dot? Tap the gear icon, replace the host with the PC's IP address, and
  Test Connection.
- Phone and PC must be on the same subnet — guest Wi-Fi won't reach it.
- On the first switch, iOS asks for **Local Network** permission → Allow.

## Security notes

- Every request except `/ping` requires the shared token, and the PC firewall
  rule only allows the local subnet. Nothing is internet-exposed.
- To rotate the token: change it in `Secrets.swift` and in `agent.config` on
  the PC, restart SwitchAgent.exe, and update it in the app's Settings (the app
  keeps a token you've edited there over the built-in default).
