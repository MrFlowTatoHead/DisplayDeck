// TEMPLATE - not compiled. Copy this file to Secrets.swift (same folder) and fill
// in your PC's details. Secrets.swift is git-ignored so your token stays private.
//   host  - your PC's name with ".local" (e.g. "DESKTOP-ABC123.local") or its LAN IP
//   token - must match token= in agent.config on the PC

/// Seeds the app on first launch; everything can be changed later in Settings.
enum AgentDefaults {
    static let host = "YOUR-PC-NAME.local"
    static let port = "5990"
    static let token = "paste-the-token-from-agent.config"
    /// Shown as a hint in Settings in case .local name resolution fails.
    static let fallbackIPHint = "192.168.1.x"
}
