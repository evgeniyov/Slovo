import AppKit
import SlovoCore
import SwiftUI

/// The main window: sidebar on the warm background and the rounded content panel.
struct MainWindowView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: 0) {
            Sidebar()
                .frame(width: 214)
            ScreenContent(screen: model.selectedScreen)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .background(theme.panelBackground)
                .clipShape(RoundedRectangle(cornerRadius: theme.panelCornerRadius))
                .overlay(RoundedRectangle(cornerRadius: theme.panelCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
                .padding(.vertical, 10)
                .padding(.trailing, 10)
        }
        .frame(minWidth: 900, minHeight: 600)
        .background(theme.windowBackground)
        .sheet(isPresented: $model.isSettingsPresented) {
            AppRoot(model: model) {
                SettingsSheet()
            }
        }
        .sheet(isPresented: $model.isShortcutsPresented) {
            AppRoot(model: model) {
                ShortcutsSheet()
            }
        }
        .alert(model.alert?.title ?? "",
               isPresented: Binding(get: { model.alert != nil }, set: { if !$0 { model.alert = nil } }),
               presenting: model.alert) { _ in
            Button(loc.t("common.ok")) {}
        } message: { alert in
            Text(alert.message)
        }
    }
}

private struct Sidebar: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 8) {
                AppLogo(size: 22)
                Text("Slovo")
                    .font(theme.logoFont)
                    .foregroundStyle(theme.textPrimary)
            }
            .padding(.horizontal, 10)
            .padding(.top, 40)
            .padding(.bottom, 16)

            ForEach(Screen.allCases) { screen in
                SidebarRow(title: loc.t(screen.titleKey), symbol: screen.symbol,
                           isSelected: model.selectedScreen == screen) {
                    model.selectedScreen = screen
                }
            }
            Spacer(minLength: 12)
            ReadinessCard()
                .padding(.bottom, 8)
            SidebarRow(title: loc.t("sidebar.settings"), symbol: "gearshape", isSelected: false) {
                model.openSettings()
            }
            MenuButton(entries: {
                [
                    .action(loc.t("help.shortcuts")) { model.showShortcuts() },
                    .action(loc.t("help.troubleshooting")) { model.showTroubleshooting() },
                    .separator,
                    .action(loc.t("help.openModels")) { model.openModelsFolder() },
                    .action(loc.t("help.openLogs")) { model.openLogs() },
                    .separator,
                    .action(loc.t("help.showOnboarding")) { model.showOnboarding() }
                ]
            }) {
                SidebarRowLabel(title: loc.t("sidebar.help"), symbol: "questionmark.circle", isSelected: false)
            }
        }
        .padding(.horizontal, 10)
        .padding(.bottom, 14)
        .sheet(isPresented: $model.isTroubleshootingPresented) {
            AppRoot(model: model) {
                TroubleshootingSheet()
            }
        }
    }
}

private struct SidebarRow: View {
    let title: String
    let symbol: String
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            SidebarRowLabel(title: title, symbol: symbol, isSelected: isSelected)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

private struct SidebarRowLabel: View {
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let title: String
    let symbol: String
    let isSelected: Bool

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 13))
                .frame(width: 18)
            Text(title)
                .lineLimit(1)
            Spacer(minLength: 0)
        }
        .font(.system(size: 13, weight: isSelected ? .semibold : .regular))
        .foregroundStyle(theme.textPrimary)
        .padding(.horizontal, 10)
        .frame(height: 32)
        .background(isSelected ? theme.sidebarSelection : (isHovering ? theme.rowHover : Color.clear),
                    in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
        .contentShape(Rectangle())
        .onHover { isHovering = $0 }
    }
}

/// A small card that says whether dictation can work, and leads to the fix when not.
private struct ReadinessCard: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        switch model.readiness {
        case .checking:
            EmptyView()
        case let .ready(engine):
            card(color: theme.success, title: loc.t("readiness.ready"), detail: engine.displayName, action: nil)
        case .permissionsNeeded:
            card(color: theme.warning, title: loc.t("readiness.permissions"), detail: loc.t("readiness.permissions.detail"),
                 action: { model.showOnboarding(.permissions) })
        case let .modelMissing(engine):
            card(color: theme.warning, title: loc.t("readiness.modelMissing"), detail: engine.displayName,
                 action: { model.openSettings(.recognition) })
        case let .downloading(engine, progress):
            card(color: theme.accent, title: loc.t("readiness.downloading", Int((progress * 100).rounded())),
                 detail: engine.displayName, action: { model.openSettings(.recognition) })
        }
    }

    private func card(color: Color, title: String, detail: String, action: (() -> Void)?) -> some View {
        Button {
            action?()
        } label: {
            HStack(alignment: .top, spacing: 8) {
                Circle()
                    .fill(color)
                    .frame(width: 7, height: 7)
                    .padding(.top, 4)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(theme.captionMediumFont)
                        .foregroundStyle(theme.textPrimary)
                    Text(detail)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
                if action != nil {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(theme.textSecondary)
                        .padding(.top, 3)
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.rowCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(action == nil)
    }
}

private struct ScreenContent: View {
    let screen: Screen

    var body: some View {
        switch screen {
        case .home: HomeScreen()
        case .meetings: MeetingsScreen()
        case .insights: InsightsScreen()
        case .dictionary: DictionaryScreen()
        case .snippets: SnippetsScreen()
        case .style: StyleScreen()
        case .scratchpad: ScratchpadScreen()
        }
    }
}

/// Owns the main window and reports when it opens or closes, so the Dock icon follows.
@MainActor
final class MainWindowController: NSObject, NSWindowDelegate {
    private let window: NSWindow
    private let model: AppModel
    private let onOpenChange: () -> Void

    /// True from `show()` until the window closes; a minimized window is still open.
    private(set) var isOpen = false

    init(model: AppModel, onOpenChange: @escaping () -> Void = {}) {
        self.model = model
        self.onOpenChange = onOpenChange
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 720),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                          backing: .buffered, defer: true)
        super.init()
        window.title = "Slovo"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.minSize = NSSize(width: 900, height: 600)
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: AppRoot(model: model) { MainWindowView() })
        window.center()
        window.setFrameAutosaveName("SlovoMainWindow")
        window.delegate = self
    }

    var isVisible: Bool {
        window.isVisible
    }

    func show() {
        isOpen = true
        onOpenChange()
        // The policy change settles before activation; activating in the same turn can
        // leave the previous app's menu bar in place.
        DispatchQueue.main.async { [window] in
            NSApp.activate()
            window.makeKeyAndOrderFront(nil)
        }
    }

    func windowWillClose(_ notification: Notification) {
        model.playback.stop()
        model.meetingPlayback.stop()
        isOpen = false
        onOpenChange()
    }
}
