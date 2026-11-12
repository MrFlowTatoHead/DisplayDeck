import SwiftUI

struct ContentView: View {
    @State private var service = SwitchService()
    @State private var showSettings = false
    @State private var showLog = false
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        @Bindable var service = service
        ZStack {
            Background()
            VStack(spacing: 10) {
                header
                    .padding(.horizontal, 20)
                ScrollView {
                    VStack(spacing: 14) {
                        ForEach(service.modes) { mode in
                            ModeCard(
                                mode: mode,
                                height: service.modes.count > 2 ? 176 : 200,
                                isActive: service.currentModeID == mode.id,
                                isBusy: service.phase == .switching(mode.id),
                                locked: service.phase != .idle || service.pcBusy
                                    || service.steamPhase == .working
                            ) {
                                Task { await service.switchTo(mode) }
                            }
                        }
                    }
                    .padding(.horizontal, 20)
                    .padding(.vertical, 12)
                }
                .scrollIndicators(.hidden)
                .refreshable { await service.refreshStatus() }
                SteamButton(
                    phase: service.steamPhase,
                    locked: service.phase != .idle || service.pcBusy || !service.isReachable
                ) {
                    Task { await service.bringSteam() }
                }
                .padding(.horizontal, 20)
                footer
                    .padding(.horizontal, 20)
            }
            .padding(.vertical, 16)
        }
        .sheet(isPresented: $showSettings) { SettingsView(service: service) }
        .sheet(isPresented: $showLog) { LogView(service: service) }
        .alert(service.errorTitle, isPresented: $service.errorPresented) {
            Button("View Log") { showLog = true }
            Button("OK", role: .cancel) { }
        } message: {
            Text(service.lastError ?? "Unknown error.")
        }
        .task {
            while !Task.isCancelled {
                await service.refreshStatus()
                try? await Task.sleep(for: .seconds(5))
            }
        }
        .onChange(of: scenePhase) { _, newPhase in
            if newPhase == .active {
                Task { await service.refreshStatus() }
            }
        }
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 5) {
                Text("DisplayDeck")
                    .font(.system(.largeTitle, design: .rounded, weight: .bold))
                HStack(spacing: 7) {
                    Circle()
                        .fill(service.isReachable ? Color.green : Color.red)
                        .frame(width: 8, height: 8)
                    Text(statusLine)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer()
            iconButton("list.bullet.rectangle") { showLog = true }
            iconButton("gearshape.fill") { showSettings = true }
        }
    }

    private func iconButton(_ symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.body.weight(.medium))
                .foregroundStyle(.secondary)
                .frame(width: 40, height: 40)
                .background(Circle().fill(.white.opacity(0.06)))
        }
        .buttonStyle(.plain)
    }

    private var statusLine: String {
        if case .switching(let id) = service.phase { return "Switching to \(service.label(for: id))…" }
        if !service.isReachable { return "PC unreachable — check the agent" }
        if service.pcBusy { return "PC is switching displays…" }
        if let mode = service.currentMode { return "\(mode.label) is active" }
        return "Custom layout — not saved to a slot"
    }

    private var footer: some View {
        Group {
            if case .switching = service.phase {
                Text("Hold on — the PC is switching displays and audio.\nThis can take up to half a minute.")
                    .multilineTextAlignment(.center)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            } else if !service.displays.isEmpty {
                Text(service.displays
                    .map { $0.components(separatedBy: "  ").first ?? $0 }
                    .joined(separator: "  ·  "))
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            } else {
                Text(" ").font(.caption2)
            }
        }
        .frame(maxWidth: .infinity)
    }
}

private struct Background: View {
    var body: some View {
        LinearGradient(
            colors: [
                Color(red: 0.055, green: 0.06, blue: 0.11),
                Color(red: 0.01, green: 0.01, blue: 0.03),
            ],
            startPoint: .top, endPoint: .bottom
        )
        .overlay(
            Circle()
                .fill(Color(red: 0.30, green: 0.55, blue: 1.0).opacity(0.09))
                .frame(width: 420, height: 420)
                .blur(radius: 90)
                .offset(x: -130, y: -290)
        )
        .overlay(
            Circle()
                .fill(Color(red: 0.70, green: 0.40, blue: 1.0).opacity(0.08))
                .frame(width: 380, height: 380)
                .blur(radius: 90)
                .offset(x: 150, y: 330)
        )
        .ignoresSafeArea()
    }
}

private struct ModeCard: View {
    let mode: DisplayMode
    let height: CGFloat
    let isActive: Bool
    let isBusy: Bool
    let locked: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top) {
                    Image(systemName: mode.symbol)
                        .font(.system(size: 34, weight: .medium))
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(isActive ? AnyShapeStyle(mode.accent) : AnyShapeStyle(.secondary))
                    Spacer()
                    trailingBadge
                }
                Spacer(minLength: 14)
                Text(mode.label)
                    .font(.system(.title, design: .rounded, weight: .semibold))
                    .foregroundStyle(isActive ? .white : .white.opacity(0.85))
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                Text(subtitleText)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                    .padding(.top, 3)
            }
            .padding(22)
            .frame(maxWidth: .infinity, minHeight: height, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 30, style: .continuous)
                    .fill(.white.opacity(isActive ? 0.085 : 0.045))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 30, style: .continuous)
                    .strokeBorder(
                        isActive ? mode.accent.opacity(0.85) : Color.white.opacity(0.09),
                        lineWidth: isActive ? 1.5 : 1
                    )
            )
            .shadow(color: isActive ? mode.accent.opacity(0.35) : .clear, radius: 22, y: 6)
            .opacity(mode.ready ? 1 : 0.55)
        }
        .buttonStyle(.plain)
        .disabled(locked || isActive || !mode.ready)
        .animation(.easeInOut(duration: 0.25), value: isActive)
        .animation(.easeInOut(duration: 0.25), value: isBusy)
    }

    private var subtitleText: String {
        if !mode.ready { return "Not set up — run Capture on the PC" }
        return mode.subtitle.isEmpty ? " " : mode.subtitle
    }

    @ViewBuilder
    private var trailingBadge: some View {
        if isBusy {
            ProgressView()
                .tint(mode.accent)
        } else if isActive {
            Text("ACTIVE")
                .font(.caption2.weight(.heavy))
                .tracking(1.6)
                .foregroundStyle(mode.accent)
                .padding(.horizontal, 11)
                .padding(.vertical, 6)
                .background(Capsule().fill(mode.accent.opacity(0.16)))
        } else if !mode.ready {
            Text("EMPTY")
                .font(.caption2.weight(.heavy))
                .tracking(1.6)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 11)
                .padding(.vertical, 6)
                .background(Capsule().fill(.white.opacity(0.08)))
        } else {
            Image(systemName: "arrow.right.circle.fill")
                .font(.title2)
                .foregroundStyle(.white.opacity(0.25))
        }
    }
}

private struct SteamButton: View {
    let phase: SteamPhase
    let locked: Bool
    let action: () -> Void

    private let tint = Color(red: 0.42, green: 0.74, blue: 1.00)

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                ZStack {
                    Circle()
                        .fill((isDone ? Color.green : tint).opacity(0.16))
                    if phase == .working {
                        ProgressView()
                            .tint(tint)
                    } else {
                        Image(systemName: isDone ? "checkmark" : "gamecontroller.fill")
                            .font(.system(size: 17, weight: .semibold))
                            .foregroundStyle(isDone ? Color.green : tint)
                    }
                }
                .frame(width: 42, height: 42)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.system(.headline, design: .rounded))
                        .foregroundStyle(.white)
                    Text(subtitle)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                }
                Spacer(minLength: 0)
                if phase == .idle {
                    Image(systemName: "arrow.up.left.and.arrow.down.right")
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.3))
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity)
            .background(
                RoundedRectangle(cornerRadius: 24, style: .continuous)
                    .fill(.white.opacity(0.05))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 24, style: .continuous)
                    .strokeBorder(isDone ? Color.green.opacity(0.5) : Color.white.opacity(0.09), lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
        .disabled(locked || phase != .idle)
        .opacity(locked && phase == .idle ? 0.45 : 1)
        .animation(.easeInOut(duration: 0.2), value: phase)
    }

    private var isDone: Bool {
        if case .done = phase { return true }
        return false
    }

    private var title: String {
        switch phase {
        case .idle: return "Bring Steam Here"
        case .working: return "Moving Steam…"
        case .done: return "Done"
        }
    }

    private var subtitle: String {
        switch phase {
        case .idle: return "Move Steam to the main screen and maximize it"
        case .working: return "Finding the Steam window on the PC"
        case .done(let message): return message
        }
    }
}

struct SettingsView: View {
    let service: SwitchService
    @AppStorage("agentHost") private var host = AgentDefaults.host
    @AppStorage("agentPort") private var port = AgentDefaults.port
    @AppStorage("agentToken") private var token = AgentDefaults.token
    @Environment(\.dismiss) private var dismiss
    @State private var testResult: String?
    @State private var testing = false

    var body: some View {
        NavigationStack {
            Form {
                Section("PC Agent") {
                    TextField("Host or IP", text: $host)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                    TextField("Port", text: $port)
                        .keyboardType(.numberPad)
                    TextField("Token", text: $token)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.system(.footnote, design: .monospaced))
                }
                Section {
                    Button {
                        testing = true
                        testResult = nil
                        Task {
                            testResult = await service.testConnection()
                            testing = false
                        }
                    } label: {
                        HStack {
                            Text("Test Connection")
                            if testing {
                                Spacer()
                                ProgressView()
                            }
                        }
                    }
                    if let testResult {
                        Label(testResult, systemImage: testResult.hasPrefix("Connected")
                            ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                            .font(.footnote)
                            .foregroundStyle(testResult.hasPrefix("Connected") ? .green : .orange)
                    }
                } footer: {
                    Text("The agent runs on the PC from the DisplaySwitch folder. "
                        + "If \"\(AgentDefaults.host)\" doesn't resolve, use the PC's IP "
                        + "(usually \(AgentDefaults.fallbackIPHint)).")
                }
                Section {
                    ForEach(service.modes) { mode in
                        HStack {
                            Image(systemName: mode.symbol)
                                .foregroundStyle(mode.accent)
                                .frame(width: 28)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(mode.label)
                                Text(mode.ready ? mode.subtitle : "Empty")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Text("Slot \(mode.slot)")
                                .font(.caption)
                                .foregroundStyle(.tertiary)
                        }
                    }
                } header: {
                    Text("Display Modes")
                } footer: {
                    Text("Modes are managed on the PC: arrange your displays, then run "
                        + "Capture-Display-Mode.cmd in the DisplaySwitch folder to save the "
                        + "layout into a slot. Changes appear here automatically.")
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}

struct LogView: View {
    let service: SwitchService
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 5) {
                    if service.logLines.isEmpty {
                        Text("No log entries yet.")
                            .foregroundStyle(.secondary)
                            .padding(.top, 40)
                            .frame(maxWidth: .infinity)
                    }
                    ForEach(Array(service.logLines.enumerated()), id: \.offset) { _, line in
                        Text(line)
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(
                                line.contains("FAILED") || line.contains("ERR")
                                    || line.contains("WARNING") ? .orange : .secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding()
            }
            .navigationTitle("Switch Log")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        Task { await service.fetchLog() }
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .task { await service.fetchLog() }
        }
    }
}

#Preview {
    ContentView()
}
