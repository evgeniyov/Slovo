import SlovoCore
import SwiftUI

/// Help → If something doesn't work: the usual causes, each with the one action that fixes it.
struct TroubleshootingSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss

    private struct Problem: Identifiable {
        let id: String
        let symbol: String
        let action: String
        let perform: @MainActor (AppModel) -> Void
        var status: PermissionKind?
    }

    private var problems: [Problem] {
        [
            Problem(id: "key", symbol: "keyboard", action: loc.t("troubleshooting.key.action"),
                    perform: { model in
                        Task { _ = await model.permissions.request(.inputMonitoring) }
                    }, status: .inputMonitoring),
            Problem(id: "nothingHeard", symbol: "mic.slash", action: loc.t("troubleshooting.nothingHeard.action"),
                    perform: { model in model.openSettings(.general) }, status: .microphone),
            Problem(id: "notPasted", symbol: "doc.on.clipboard", action: loc.t("troubleshooting.notPasted.action"),
                    perform: { model in
                        Task { _ = await model.permissions.request(.accessibility) }
                    }, status: .accessibility),
            Problem(id: "model", symbol: "arrow.down.circle", action: loc.t("troubleshooting.model.action"),
                    perform: { model in model.openSettings(.recognition) }),
            Problem(id: "meetingOthers", symbol: "person.2.wave.2", action: loc.t("troubleshooting.meetingOthers.action"),
                    perform: { model in model.permissions.openSystemSettings(for: .systemAudioRecording) }),
            Problem(id: "other", symbol: "doc.text.magnifyingglass", action: loc.t("help.openLogs"),
                    perform: { model in model.openLogs() })
        ]
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(loc.t("help.troubleshooting"))
                    .font(theme.sectionTitleFont)
                    .foregroundStyle(theme.textPrimary)
                Spacer()
                IconButton(symbol: "xmark", help: loc.t("common.close")) {
                    model.isTroubleshootingPresented = false
                    dismiss()
                }
            }
            ScrollView {
                VStack(spacing: 10) {
                    ForEach(problems) { problem in
                        Card(padding: 14) {
                            HStack(alignment: .top, spacing: 12) {
                                Image(systemName: problem.symbol)
                                    .font(.system(size: 14))
                                    .foregroundStyle(theme.accentText)
                                    .frame(width: 22)
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(loc.t("troubleshooting.\(problem.id).title"))
                                        .font(theme.headlineFont)
                                        .foregroundStyle(theme.textPrimary)
                                    Text(loc.t("troubleshooting.\(problem.id).detail"))
                                        .font(theme.captionFont)
                                        .foregroundStyle(theme.textSecondary)
                                        .fixedSize(horizontal: false, vertical: true)
                                    if let kind = problem.status {
                                        let granted = model.permissionStatus(kind) == .granted
                                        Text("\(loc.t("permission.\(kind.rawValue)")): \(loc.t(granted ? "permission.status.granted" : "permission.status.missing"))")
                                            .font(theme.captionMediumFont)
                                            .foregroundStyle(granted ? theme.success : theme.warning)
                                    }
                                }
                                Spacer(minLength: 8)
                                Button(problem.action) { problem.perform(model) }
                                    .buttonStyle(SecondaryButtonStyle())
                            }
                        }
                    }
                }
            }
        }
        .padding(24)
        .frame(width: 580, height: 560)
        .background(theme.windowBackground)
    }
}
