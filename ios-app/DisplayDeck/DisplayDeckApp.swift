import SwiftUI

@main
struct DisplayDeckApp: App {
    init() {
        UserDefaults.standard.register(defaults: [
            "agentHost": AgentDefaults.host,
            "agentPort": AgentDefaults.port,
            "agentToken": AgentDefaults.token,
        ])
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .preferredColorScheme(.dark)
        }
    }
}
