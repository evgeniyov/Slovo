import AppKit
import SlovoCore
import SwiftUI

enum OnboardingStep: Int, CaseIterable, Identifiable {
    case welcome
    case permissions
    case key
    case models
    case practice

    var id: Int { rawValue }
}

/// First-launch walkthrough: language, permissions, key, models, practice.
struct OnboardingView: View {
    static let size = CGSize(width: 680, height: 660)

    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var step: OnboardingStep
    let onFinish: () -> Void
    /// Whether the practice step is on screen; the window decides whether the dictation
    /// key fills it, which it does only while the window is key.
    let onPracticeVisibilityChange: (Bool) -> Void

    init(initialStep: OnboardingStep = .welcome, onFinish: @escaping () -> Void = {},
         onPracticeVisibilityChange: @escaping (Bool) -> Void = { _ in }) {
        _step = State(initialValue: initialStep)
        self.onFinish = onFinish
        self.onPracticeVisibilityChange = onPracticeVisibilityChange
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                AppLogo(size: 22)
                Text("Slovo")
                    .font(theme.logoFont)
                    .foregroundStyle(theme.textPrimary)
                Spacer()
                Text(loc.t("onboarding.step", step.rawValue + 1, OnboardingStep.allCases.count))
                    .font(theme.captionMediumFont)
                    .foregroundStyle(theme.textSecondary)
            }
            .padding(.bottom, 14)
            HStack(spacing: 6) {
                ForEach(OnboardingStep.allCases) { item in
                    Capsule()
                        .fill(item.rawValue <= step.rawValue ? theme.accent : theme.calendarEmpty)
                        .frame(height: 4)
                }
            }
            .padding(.bottom, 30)

            Group {
                switch step {
                case .welcome: WelcomeStep()
                case .permissions: PermissionsStep()
                case .key: KeyStep()
                case .models: ModelsStep()
                case .practice: PracticeStep(dictation: model.dictation)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            HStack {
                if step != .welcome {
                    Button(loc.t("common.back")) {
                        move(to: OnboardingStep(rawValue: step.rawValue - 1) ?? .welcome)
                    }
                    .buttonStyle(SecondaryButtonStyle())
                }
                Spacer()
                if step == .practice {
                    Button(loc.t("common.finish"), action: onFinish)
                        .buttonStyle(PrimaryButtonStyle())
                } else {
                    Button(loc.t("common.continue")) {
                        move(to: OnboardingStep(rawValue: step.rawValue + 1) ?? .practice)
                    }
                    .buttonStyle(PrimaryButtonStyle())
                }
            }
            .padding(.top, 16)
        }
        .padding(.horizontal, 36)
        .padding(.top, 30)
        .padding(.bottom, 28)
        .frame(width: Self.size.width, height: Self.size.height)
        .background(theme.panelBackground)
        .onAppear { onPracticeVisibilityChange(step == .practice) }
        .onChange(of: step) { _, newStep in onPracticeVisibilityChange(newStep == .practice) }
        .onDisappear { onPracticeVisibilityChange(false) }
    }

    private func move(to newStep: OnboardingStep) {
        withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.2)) {
            step = newStep
        }
        if newStep == .permissions {
            model.permissions.refresh()
        }
    }
}

private struct StepHeader: View {
    @Environment(\.theme) private var theme
    let title: String
    let message: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.system(size: 30, weight: .regular, design: .serif))
                .foregroundStyle(theme.textPrimary)
            Text(message)
                .font(.system(size: 14))
                .foregroundStyle(theme.textSecondary)
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.bottom, 22)
    }
}

private struct WelcomeStep: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        let selected: AppLanguage = loc.languageCode == "ru" ? .ru : .en
        VStack(alignment: .leading, spacing: 0) {
            StepHeader(title: loc.t("onboarding.welcome.title"), message: loc.t("onboarding.welcome.message"))
            SectionLabel(text: loc.t("settings.general.language"))
                .padding(.bottom, 10)
            HStack(spacing: 12) {
                LanguageCard(title: loc.t("language.en"), sample: loc.t("onboarding.welcome.sample.en"),
                             isSelected: selected == .en) { loc.language = .en }
                LanguageCard(title: loc.t("language.ru"), sample: loc.t("onboarding.welcome.sample.ru"),
                             isSelected: selected == .ru) { loc.language = .ru }
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.bottom, 26)
            VStack(alignment: .leading, spacing: 14) {
                feature(symbol: "waveform", title: loc.t("onboarding.welcome.feature1"),
                        detail: loc.t("onboarding.welcome.feature1.detail"))
                feature(symbol: "character.book.closed", title: loc.t("onboarding.welcome.feature2"),
                        detail: loc.t("onboarding.welcome.feature2.detail"))
                feature(symbol: "lock.shield", title: loc.t("onboarding.welcome.feature3"),
                        detail: loc.t("onboarding.welcome.feature3.detail"))
            }
        }
    }

    private func feature(symbol: String, title: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(theme.accentText)
                .frame(width: 30, height: 30)
                .background(theme.accentSoft, in: RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                Text(detail)
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

private struct LanguageCard: View {
    @Environment(\.theme) private var theme
    let title: String
    let sample: String
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(theme.sectionTitleFont)
                        .foregroundStyle(theme.textPrimary)
                    Text(sample)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 17))
                    .foregroundStyle(isSelected ? theme.accent : theme.panelBorder)
            }
            .padding(16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius)
                .strokeBorder(isSelected ? theme.accent : theme.panelBorder, lineWidth: isSelected ? 2 : 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

private struct PermissionsStep: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            StepHeader(title: loc.t("onboarding.permissions.title"), message: loc.t("onboarding.permissions.message"))
            VStack(spacing: 0) {
                PermissionRow(kind: .microphone, symbol: "mic", title: loc.t("permission.microphone"),
                              detail: loc.t("onboarding.permissions.microphone.detail"))
                RowDivider()
                PermissionRow(kind: .accessibility, symbol: "accessibility", title: loc.t("permission.accessibility"),
                              detail: loc.t("onboarding.permissions.accessibility.detail"))
                RowDivider()
                PermissionRow(kind: .inputMonitoring, symbol: "keyboard", title: loc.t("permission.inputMonitoring"),
                              detail: loc.t("onboarding.permissions.inputMonitoring.detail"))
            }
            .padding(.horizontal, 16)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "info.circle")
                Text(loc.t("onboarding.permissions.footnote"))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(theme.captionFont)
            .foregroundStyle(theme.textSecondary)
            .padding(.top, 12)
            .padding(.horizontal, 4)
        }
    }
}

/// One permission's status and a button to grant or open it, reused outside onboarding
/// by Settings → Privacy so a permission asked for lazily still has somewhere to check
/// and fix.
struct PermissionRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let kind: PermissionKind
    let symbol: String
    let title: String
    let detail: String

    var body: some View {
        let status = model.permissionStatus(kind)
        HStack(spacing: 14) {
            Image(systemName: symbol)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(theme.textPrimary)
                .frame(width: 34, height: 34)
                .background(theme.insetBackground, in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                Text(detail)
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            switch status {
            case .granted:
                StatusBadge(text: loc.t("permission.status.granted"), tone: .success)
            case .unknown:
                // Only System Audio Recording lands here: macOS gives no query API for
                // it, so the true status only ever shows in System Settings itself.
                StatusBadge(text: loc.t("permission.status.unknown"), tone: .neutral)
                actionButton(status)
            case .denied, .notDetermined:
                StatusBadge(text: loc.t("permission.status.missing"), tone: .warning)
                actionButton(status)
            }
        }
        .padding(.vertical, 14)
    }

    private func actionButton(_ status: PermissionStatus) -> some View {
        Button(kind == .microphone && status == .notDetermined ? loc.t("onboarding.permissions.allow")
                                                               : loc.t("onboarding.permissions.open")) {
            guard model.snapshot == nil else { return }
            // Asking first puts Slovo into the pane's list; `request` opens the pane itself
            // whenever macOS no longer asks.
            if Permissions.asksThroughRequest(kind) {
                Task {
                    _ = await model.permissions.request(kind)
                    model.permissions.refresh()
                }
            } else {
                model.permissions.openSystemSettings(for: kind)
            }
        }
        .buttonStyle(SecondaryButtonStyle())
    }
}

private struct KeyStep: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.dictationKey) private var keyRaw = DictationKey.fn.rawValue

    var body: some View {
        let key = DictationKey(rawValue: keyRaw) ?? .fn
        VStack(alignment: .leading, spacing: 12) {
            StepHeader(title: loc.t("onboarding.key.title"), message: loc.t("onboarding.key.message"))
                .padding(.bottom, -10)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)], spacing: 12) {
                ForEach(DictationKey.allCases) { option in
                    KeyCard(key: option, isSelected: option == key) {
                        keyRaw = option.rawValue
                    }
                }
            }
            KeyTestIndicator(hotkeys: model.hotkeys, key: key)
            FnKeyWarnings(key: key)
        }
    }
}

private struct KeyCard: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let key: DictationKey
    let isSelected: Bool
    let action: () -> Void

    private var detail: String {
        switch key {
        case .fn: return loc.t("key.fn.detail")
        case .rightOption: return loc.t("key.rightOption.detail")
        case .rightCommand: return loc.t("key.rightCommand.detail")
        case .leftControlOption: return loc.t("key.leftControlOption.detail")
        }
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                KeycapBadge(symbol: key.symbol, size: 16)
                    .frame(width: 58)
                VStack(alignment: .leading, spacing: 2) {
                    Text(loc.t(key.nameKey))
                        .font(theme.bodyMediumFont)
                        .foregroundStyle(theme.textPrimary)
                    Text(detail)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
                Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 16))
                    .foregroundStyle(isSelected ? theme.accent : theme.panelBorder)
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius)
                .strokeBorder(isSelected ? theme.accent : theme.panelBorder, lineWidth: isSelected ? 2 : 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

/// What the Models step and Finish conclude from the engines' statuses.
enum OnboardingModels {
    /// A model that is not on this Mac: only a download makes it usable. A download in
    /// progress or a status still being checked does not count.
    static func isMissing(_ status: ModelStatus) -> Bool {
        switch status {
        case .notDownloaded, .damaged: return true
        default: return false
        }
    }

    enum Notice: Equatable {
        /// No engine can run, and nothing is downloading: the dictation key would do nothing.
        case nothingUsable
        /// The dictation engine is missing; Finish switches to Apple Dictation.
        case appleFallback
    }

    static func notice(statuses: [EngineID: ModelStatus], dictationEngine: EngineID) -> Notice? {
        // Whisper only helps another engine, so it alone cannot make dictation work.
        let all = EngineID.transcriptionEngines.map { statuses[$0] ?? .unknown }
        let isSettled = !all.contains { status in
            switch status {
            case .unknown, .downloading: return true
            default: return false
            }
        }
        if isSettled, !all.contains(where: \.isUsable) {
            return .nothingUsable
        }
        if isMissing(statuses[dictationEngine] ?? .unknown), (statuses[.appleDictation] ?? .unknown).isUsable {
            return .appleFallback
        }
        return nil
    }
}

private struct ModelsStep: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    private var notice: OnboardingModels.Notice? {
        let statuses = Dictionary(uniqueKeysWithValues: EngineID.allCases.map { ($0, model.modelStatus($0)) })
        return OnboardingModels.notice(statuses: statuses,
                                       dictationEngine: model.settings.engine(for: .dictation(model.settings.languageMode)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            StepHeader(title: loc.t("onboarding.models.title"), message: loc.t("onboarding.models.message"))
            switch notice {
            case .nothingUsable:
                InlineNotice(symbol: "exclamationmark.triangle", text: loc.t("onboarding.models.nothingUsable"))
                    .padding(.bottom, 12)
            case .appleFallback:
                InlineNotice(symbol: "info.circle",
                             text: loc.t("onboarding.models.appleFallback", EngineID.appleDictation.displayName),
                             tone: .neutral)
                    .padding(.bottom, 12)
            case nil:
                EmptyView()
            }
            PanelScroll {
                VStack(spacing: 0) {
                    // The English helper is an extra for later, in Settings → Recognition.
                    ForEach(Array(EngineID.transcriptionEngines.enumerated()), id: \.element) { index, id in
                        if index > 0 {
                            RowDivider()
                        }
                        EngineModelRow(id: id)
                    }
                }
                .padding(.horizontal, 16)
                .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
                .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
            }
        }
    }
}

private struct PracticeStep: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.dictationKey) private var keyRaw = DictationKey.fn.rawValue
    @ObservedObject var dictation: DictationController

    var body: some View {
        let key = DictationKey(rawValue: keyRaw) ?? .fn
        let result = model.snapshot?.practiceResult ?? dictation.practiceResult
        let isPracticing: Bool = {
            if case let .recording(info) = dictation.state { return info.target == .practice }
            return false
        }()
        VStack(alignment: .leading, spacing: 14) {
            StepHeader(title: loc.t("onboarding.practice.title"), message: loc.t("onboarding.practice.message"))
                .padding(.bottom, -8)
            HStack(spacing: 8) {
                Text(loc.t("onboarding.practice.hold.prefix"))
                KeycapBadge(symbol: key.symbol, size: 14)
                Text(loc.t("onboarding.practice.hold.suffix"))
            }
            .font(theme.bodyMediumFont)
            .foregroundStyle(theme.textPrimary)

            // Practice never pastes anywhere: what Slovo heard shows up in this card.
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 6) {
                    Image(systemName: result == nil ? "waveform" : "checkmark.circle.fill")
                        .foregroundStyle(result == nil ? theme.textSecondary : theme.success)
                    Text(loc.t("onboarding.practice.result"))
                        .font(theme.captionMediumFont)
                        .foregroundStyle(theme.textSecondary)
                }
                Text(result ?? loc.t("onboarding.practice.placeholder"))
                    .font(theme.styleSampleFont)
                    .foregroundStyle(result == nil ? theme.textSecondary : theme.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(14)
            .frame(maxWidth: .infinity, minHeight: 120, alignment: .topLeading)
            .background(result == nil ? theme.insetBackground : theme.successSoft,
                        in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))

            HStack(spacing: 10) {
                Button {
                    if isPracticing {
                        dictation.finish()
                    } else {
                        dictation.startHandsFree(target: .practice)
                    }
                } label: {
                    Label(isPracticing ? loc.t("onboarding.practice.stop") : loc.t("onboarding.practice.handsFree"),
                          systemImage: isPracticing ? "stop.fill" : "mic")
                }
                .buttonStyle(SecondaryButtonStyle())
                Text(loc.t("onboarding.practice.handsFree.detail"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
            }
        }
    }
}

/// Hosts onboarding in a centred window and records completion.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    private let window: NSWindow
    private let model: AppModel
    /// Called whenever the window opens or closes, so the Dock icon can follow.
    private let onOpenChange: () -> Void
    private var isPracticeVisible = false

    /// True from `show` until the window closes.
    private(set) var isOpen = false

    init(model: AppModel, onOpenChange: @escaping () -> Void = {}) {
        self.model = model
        self.onOpenChange = onOpenChange
        window = NSWindow(contentRect: NSRect(origin: .zero, size: OnboardingView.size),
                          styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: true)
        super.init()
        window.title = "Slovo"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.center()
        window.delegate = self
    }

    /// Shows the walkthrough at `step`. The view keeps its step while it lives, so a
    /// closed window gets a fresh view each time; an open one stays where the user is.
    func show(from step: OnboardingStep) {
        if !window.isVisible || window.contentView == nil {
            window.contentView = NSHostingView(rootView: AppRoot(model: model) {
                OnboardingView(initialStep: step,
                               onFinish: { [weak self] in self?.finish() },
                               onPracticeVisibilityChange: { [weak self] isVisible in
                                   self?.isPracticeVisible = isVisible
                                   self?.updatePracticeTarget()
                               })
            })
        }
        isOpen = true
        // The policy change settles before activation, as for the main window.
        onOpenChange()
        DispatchQueue.main.async { [window] in
            NSApp.activate()
            window.makeKeyAndOrderFront(nil)
        }
    }

    private func finish() {
        model.settings.onboardingCompleted = true
        useAppleDictationIfModelsAreMissing()
        // The main window opens first, so the Dock icon stays while one window replaces
        // the other.
        model.showMainWindow()
        window.close()
    }

    /// The Models step promises that Slovo works with Apple Dictation when nothing is
    /// downloaded: a dictation engine whose model is missing gives way to it. An engine
    /// still downloading or not checked yet keeps its place.
    private func useAppleDictationIfModelsAreMissing() {
        guard model.snapshot == nil, model.modelStatus(.appleDictation).isUsable else { return }
        let settings = model.settings
        if OnboardingModels.isMissing(model.modelStatus(settings.russianEngine)) {
            settings.russianEngine = .appleDictation
        }
        if OnboardingModels.isMissing(model.modelStatus(settings.englishEngine)) {
            settings.englishEngine = .appleDictation
        }
    }

    /// The dictation key fills the practice card only while the walkthrough is the key
    /// window; in any other app it pastes as usual, even with the practice step open.
    private func updatePracticeTarget() {
        model.setPracticeActive(isPracticeVisible && window.isKeyWindow)
    }

    func windowDidBecomeKey(_ notification: Notification) {
        updatePracticeTarget()
    }

    func windowDidResignKey(_ notification: Notification) {
        updatePracticeTarget()
    }

    func windowWillClose(_ notification: Notification) {
        // Closing the window does not always take its views off screen first.
        isPracticeVisible = false
        model.setPracticeActive(false)
        isOpen = false
        onOpenChange()
    }
}
