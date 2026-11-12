import SwiftUI
import UIKit

/// A display mode slot as defined on the PC (agent.config). The list comes from
/// the agent, so capturing or renaming a slot on the PC shows up here without
/// rebuilding the app.
struct DisplayMode: Codable, Identifiable, Hashable {
    let id: String
    let label: String
    let subtitle: String
    let icon: String
    let slot: Int
    let ready: Bool

    var symbol: String {
        UIImage(systemName: icon) != nil ? icon : "display"
    }

    var accent: Color {
        let palette: [Color] = [
            Color(red: 0.30, green: 0.85, blue: 1.00),
            Color(red: 0.78, green: 0.46, blue: 1.00),
            Color(red: 1.00, green: 0.66, blue: 0.30),
            Color(red: 0.36, green: 0.92, blue: 0.62),
            Color(red: 1.00, green: 0.42, blue: 0.62),
        ]
        return palette[max(slot - 1, 0) % palette.count]
    }

    /// Shown until the app has heard from the PC at least once.
    static let builtIn: [DisplayMode] = [
        DisplayMode(id: "ultrawide", label: "Ultrawide", subtitle: "Odyssey G95C · 5120×1440 · 240 Hz",
                    icon: "display", slot: 1, ready: true),
        DisplayMode(id: "4k", label: "4K Theater", subtitle: "LG TV SSCR2 · 3840×2160 · 120 Hz",
                    icon: "tv", slot: 2, ready: true),
        DisplayMode(id: "slot3", label: "Slot 3", subtitle: "",
                    icon: "display.2", slot: 3, ready: false),
    ]
}

enum SteamPhase: Equatable {
    case idle
    case working
    case done(String)
}

/// Talks to SwitchAgent.exe on the PC. A switch request stays open until the
/// PC has finished the whole display + audio sequence and verified the result,
/// so what this service reports is the actual outcome, not just "sent".
@MainActor
@Observable
final class SwitchService {
    enum Phase: Equatable {
        case idle
        case switching(String)
    }

    var phase: Phase = .idle
    var modes: [DisplayMode] = SwitchService.loadCachedModes()
    var currentModeID: String?
    var displays: [String] = []
    var isReachable = false
    var pcBusy = false
    var steamPhase: SteamPhase = .idle
    var logLines: [String] = []
    var errorTitle = "Switch Failed"
    var lastError: String?
    var errorPresented = false

    var currentMode: DisplayMode? {
        modes.first { $0.id == currentModeID }
    }

    private var host: String { UserDefaults.standard.string(forKey: "agentHost") ?? "" }
    private var port: String { UserDefaults.standard.string(forKey: "agentPort") ?? "5990" }
    private var token: String { UserDefaults.standard.string(forKey: "agentToken") ?? "" }

    private struct StatusResponse: Decodable {
        let ok: Bool
        let mode: String
        let displays: [String]
        let modes: [DisplayMode]?
        let busy: Bool?
    }

    private struct SwitchResponse: Decodable {
        let ok: Bool
        let exitCode: Int
        let mode: String
        let displays: [String]?
        let log: [String]?
    }

    private struct LogResponse: Decodable {
        let ok: Bool
        let lines: [String]
    }

    private struct ErrorResponse: Decodable {
        let ok: Bool
        let error: String
    }

    private struct SteamResponse: Decodable {
        let ok: Bool
        let exitCode: Int
        let message: String
        let screen: String?
    }

    nonisolated private static func loadCachedModes() -> [DisplayMode] {
        guard let data = UserDefaults.standard.data(forKey: "cachedModes"),
              let cached = try? JSONDecoder().decode([DisplayMode].self, from: data),
              !cached.isEmpty else { return DisplayMode.builtIn }
        return cached
    }

    private func cacheModes() {
        if let data = try? JSONEncoder().encode(modes) {
            UserDefaults.standard.set(data, forKey: "cachedModes")
        }
    }

    func label(for modeID: String) -> String {
        modes.first { $0.id == modeID }?.label ?? modeID
    }

    private func makeRequest(_ path: String, method: String = "GET",
                             timeout: TimeInterval = 8) -> URLRequest? {
        let trimmedHost = host.trimmingCharacters(in: .whitespaces)
        guard !trimmedHost.isEmpty,
              let url = URL(string: "http://\(trimmedHost):\(port)\(path)") else { return nil }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData,
                             timeoutInterval: timeout)
        req.httpMethod = method
        req.setValue(token, forHTTPHeaderField: "X-Auth-Token")
        return req
    }

    func refreshStatus() async {
        guard case .idle = phase, let req = makeRequest("/status") else { return }
        do {
            let (data, _) = try await URLSession.shared.data(for: req)
            let status = try JSONDecoder().decode(StatusResponse.self, from: data)
            isReachable = true
            currentModeID = status.mode
            displays = status.displays
            pcBusy = status.busy ?? false
            if let received = status.modes, !received.isEmpty {
                let sorted = received.sorted { $0.slot < $1.slot }
                if sorted != modes {
                    modes = sorted
                    cacheModes()
                }
            }
        } catch {
            isReachable = false
        }
    }

    func switchTo(_ mode: DisplayMode) async {
        guard case .idle = phase,
              let req = makeRequest("/switch/\(mode.id)", method: "POST", timeout: 100) else { return }
        phase = .switching(mode.id)
        defer { phase = .idle }
        do {
            let (data, response) = try await URLSession.shared.data(for: req)
            isReachable = true
            if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                let message = (try? JSONDecoder().decode(ErrorResponse.self, from: data))?.error
                presentError(message ?? "The PC refused the request (HTTP \(http.statusCode)).")
                return
            }
            let result = try JSONDecoder().decode(SwitchResponse.self, from: data)
            currentModeID = result.mode
            if let newDisplays = result.displays { displays = newDisplays }
            if let log = result.log { logLines = log }
            if result.ok {
                UINotificationFeedbackGenerator().notificationOccurred(.success)
            } else {
                presentError("The PC couldn't switch to \(mode.label) (exit code \(result.exitCode)) "
                    + "and reverted to the previous layout where possible. See the log for details.")
            }
        } catch {
            presentError("No answer from the PC: \(error.localizedDescription)")
        }
    }

    /// Asks the PC to move Steam's window to the primary monitor and maximize it.
    func bringSteam() async {
        guard case .idle = phase, steamPhase == .idle,
              let req = makeRequest("/steam", method: "POST", timeout: 80) else { return }
        steamPhase = .working
        do {
            let (data, response) = try await URLSession.shared.data(for: req)
            isReachable = true
            if let http = response as? HTTPURLResponse, http.statusCode != 200 {
                let message = (try? JSONDecoder().decode(ErrorResponse.self, from: data))?.error
                steamPhase = .idle
                presentError(message ?? "The PC refused the request (HTTP \(http.statusCode)).",
                             title: "Couldn't Move Steam")
                return
            }
            let result = try JSONDecoder().decode(SteamResponse.self, from: data)
            guard result.ok else {
                steamPhase = .idle
                presentError(result.message, title: "Couldn't Move Steam")
                return
            }
            UINotificationFeedbackGenerator().notificationOccurred(.success)
            let screen = result.screen.flatMap { $0.isEmpty ? nil : $0 } ?? "the main screen"
            steamPhase = .done("Steam is up on \(screen)")
            try? await Task.sleep(for: .seconds(3))
            if case .done = steamPhase { steamPhase = .idle }
        } catch {
            steamPhase = .idle
            presentError("No answer from the PC: \(error.localizedDescription)", title: "Couldn't Move Steam")
        }
    }

    func fetchLog() async {
        guard let req = makeRequest("/log") else { return }
        do {
            let (data, _) = try await URLSession.shared.data(for: req)
            let result = try JSONDecoder().decode(LogResponse.self, from: data)
            logLines = result.lines
        } catch { }
    }

    func testConnection() async -> String {
        guard let ping = makeRequest("/ping", timeout: 5) else { return "Host or port is invalid." }
        do {
            let (data, _) = try await URLSession.shared.data(for: ping)
            guard String(data: data, encoding: .utf8) == "pong" else {
                return "Something answered, but it isn't the switch agent."
            }
        } catch {
            return "No response: \(error.localizedDescription)"
        }
        guard let status = makeRequest("/status") else { return "Host or port is invalid." }
        do {
            let (data, response) = try await URLSession.shared.data(for: status)
            if (response as? HTTPURLResponse)?.statusCode == 401 {
                return "Agent found, but it rejected the token."
            }
            _ = try JSONDecoder().decode(StatusResponse.self, from: data)
            await refreshStatus()
            return "Connected. Everything looks good."
        } catch {
            return "Agent found, but the status check failed."
        }
    }

    private func presentError(_ message: String, title: String = "Switch Failed") {
        errorTitle = title
        lastError = message
        errorPresented = true
        UINotificationFeedbackGenerator().notificationOccurred(.error)
    }
}
