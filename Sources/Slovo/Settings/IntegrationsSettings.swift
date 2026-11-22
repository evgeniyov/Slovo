import AppKit
import SlovoCore
import SwiftUI

/// Settings → Integrations: Claude's access to meetings, reading and writing switched
/// separately, the registration snippets for Claude Code and the Claude app, and a check
/// that the server answers.
struct IntegrationsSettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.mcpEnabled) private var accessEnabled = false
    @AppStorage(SettingsKey.mcpMaxAgeDays) private var maxAgeDays = 0
    @AppStorage(SettingsKey.mcpWriteEnabled) private var writeEnabled = false

    var body: some View {
        let path = model.executablePath
        let registration = model.claudeRegistrationStatus
        VStack(alignment: .leading, spacing: 22) {
            introCard

            SettingsGroup(loc.t("settings.group.claudeAccess")) {
                SettingsRow(loc.t("settings.integrations.enable"), detail: loc.t("settings.integrations.enable.detail")) {
                    ThemedToggle(isOn: $accessEnabled)
                }
                RowDivider()
                SettingsRow(loc.t("settings.integrations.maxAge"), detail: loc.t("settings.integrations.maxAge.detail")) {
                    PopupChoice(options: AppSettings.mcpMaxAgeDayChoices.map { ($0, ageTitle($0)) },
                                selection: Binding(get: { AppSettings.normalizedMCPMaxAgeDays(maxAgeDays) },
                                                   set: { maxAgeDays = $0 }))
                }
                RowDivider()
                // Writing is its own decision: reading meetings and changing them are not
                // the same thing to grant.
                SettingsRow(loc.t("settings.integrations.write"), detail: loc.t("settings.integrations.write.detail")) {
                    ThemedToggle(isOn: $writeEnabled)
                }
            }

            VStack(alignment: .leading, spacing: 8) {
                SettingsGroup(loc.t("settings.group.connect")) {
                    if !ClaudeIntegration.isInApplications(path) {
                        InlineNotice(symbol: "exclamationmark.triangle", text: loc.t("settings.integrations.notInApplications"))
                            .padding(.top, 12)
                    }
                    ClientRow(title: "Claude Code", detail: loc.t("settings.integrations.claudeCode.detail"),
                              isRegistered: registration?.claudeCode,
                              registeredText: loc.t("settings.integrations.status.claudeCode.added"),
                              missingText: loc.t("settings.integrations.status.claudeCode.missing"),
                              snippet: ClaudeIntegration.claudeCodeCommand(executablePath: path),
                              displayedSnippet: ClaudeIntegration.claudeCodeCommandForDisplay(executablePath: path)) {
                        EmptyView()
                    }
                    RowDivider()
                    ClientRow(title: loc.t("settings.integrations.claudeApp"), detail: loc.t("settings.integrations.claudeApp.detail"),
                              isRegistered: registration?.claudeApp,
                              registeredText: loc.t("settings.integrations.status.claudeApp.added"),
                              missingText: loc.t("settings.integrations.status.claudeApp.missing"),
                              snippet: ClaudeIntegration.claudeAppSnippet(executablePath: path)) {
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([ClaudeIntegration.claudeAppConfigURL])
                        } label: {
                            Label(loc.t("settings.integrations.showConfig"), systemImage: "folder")
                        }
                        .buttonStyle(SecondaryButtonStyle())
                        .disabled(model.snapshot == nil && !ClaudeIntegration.claudeAppConfigExists)
                    }
                    RowDivider()
                    checkRow
                }
                Text(loc.t("settings.integrations.tip"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 4)
            }
        }
        .onAppear { model.refreshClaudeRegistration() }
    }

    private var introCard: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "puzzlepiece.extension")
                .font(.system(size: 22, weight: .regular))
                .foregroundStyle(theme.bannerText)
                .frame(width: 44, height: 44)
                .background(LinearGradient(colors: [theme.bannerGradientStart, theme.bannerGradientEnd],
                                           startPoint: .topLeading, endPoint: .bottomTrailing),
                            in: RoundedRectangle(cornerRadius: 11))
            VStack(alignment: .leading, spacing: 6) {
                Text(loc.t("settings.integrations.title"))
                    .font(theme.sectionTitleFont)
                    .foregroundStyle(theme.textPrimary)
                Text(loc.t("settings.integrations.intro"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Image(systemName: "exclamationmark.bubble")
                        .foregroundStyle(theme.warning)
                    Text(loc.t("settings.integrations.disclosure"))
                        .foregroundStyle(theme.textPrimary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .font(theme.captionMediumFont)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }

    @ViewBuilder
    private var checkRow: some View {
        let state = model.mcpCheck
        SettingsRow(loc.t("settings.integrations.check.title"), detail: loc.t("settings.integrations.check.detail")) {
            Button {
                model.runMCPCheck()
            } label: {
                Label(state == .running ? loc.t("settings.integrations.check.running") : loc.t("settings.integrations.check"),
                      systemImage: "stethoscope")
            }
            .buttonStyle(SecondaryButtonStyle())
            .disabled(state == .running)
        }
        if case let .finished(result) = state {
            checkResult(result)
                .padding(.bottom, 12)
        }
    }

    private func checkResult(_ result: MCPCheckResult) -> InlineNotice {
        switch result {
        case let .works(count):
            return InlineNotice(symbol: "checkmark.circle.fill",
                                text: loc.t("settings.integrations.check.works", loc.plural("plural.meetings", count)),
                                tone: .success)
        case .accessOff:
            return InlineNotice(symbol: "exclamationmark.circle", text: loc.t("settings.integrations.check.accessOff"))
        case let .failed(message):
            return InlineNotice(symbol: "xmark.octagon", text: loc.t("settings.integrations.check.failed", message),
                                tone: .destructive)
        }
    }

    private func ageTitle(_ days: Int) -> String {
        days == 0 ? loc.t("settings.integrations.maxAge.all") : loc.t("settings.integrations.maxAge.days", days)
    }
}

/// One Claude client: whether Slovo is registered, what to do, and the text to copy.
private struct ClientRow<Actions: View>: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var justCopied = false
    let title: String
    let detail: String
    /// Nil while the configuration files have not been read yet.
    let isRegistered: Bool?
    let registeredText: String
    let missingText: String
    /// What Copy puts on the pasteboard.
    let snippet: String
    /// What the row shows, when it differs from the copied text.
    var displayedSnippet: String?
    @ViewBuilder let actions: () -> Actions

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .center, spacing: 10) {
                    Text(title)
                        .font(theme.bodyMediumFont)
                        .foregroundStyle(theme.textPrimary)
                    Spacer(minLength: 8)
                    switch isRegistered {
                    case .some(true):
                        StatusBadge(text: registeredText, tone: .success)
                    case .some(false):
                        StatusBadge(text: missingText, tone: .neutral)
                    case .none:
                        StatusBadge(text: loc.t("engine.status.checking"), tone: .neutral)
                    }
                }
                Text(detail)
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text(displayedSnippet ?? snippet)
                .font(theme.codeFont)
                .foregroundStyle(theme.textPrimary)
                .lineSpacing(2)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
                .background(theme.insetBackground, in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
                .overlay(RoundedRectangle(cornerRadius: theme.rowCornerRadius).strokeBorder(theme.divider, lineWidth: 1))
            HStack(spacing: 8) {
                Spacer(minLength: 0)
                actions()
                Button {
                    model.copyToPasteboard(snippet)
                    justCopied = true
                    Task {
                        try? await Task.sleep(for: .seconds(1.2))
                        justCopied = false
                    }
                } label: {
                    Label(justCopied ? loc.t("common.copied") : loc.t("common.copy"),
                          systemImage: justCopied ? "checkmark" : "doc.on.doc")
                }
                .buttonStyle(SecondaryButtonStyle())
            }
        }
        .padding(.vertical, 12)
    }
}
