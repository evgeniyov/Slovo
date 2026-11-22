import AppKit
import SlovoCore
import SwiftUI

struct SettingsSectionView: View {
    let section: SettingsSection

    var body: some View {
        switch section {
        case .general: GeneralSettings()
        case .recognition: RecognitionSettings()
        case .system: SystemSettings()
        case .meetings: MeetingSettings()
        case .integrations: IntegrationsSettings()
        case .privacy: PrivacySettings()
        case .about: AboutSettings()
        }
    }
}

/// A binding to an enum stored as its raw string in `@AppStorage`.
func enumBinding<T: RawRepresentable>(_ raw: Binding<String>, default value: T) -> Binding<T>
where T.RawValue == String {
    Binding(get: { T(rawValue: raw.wrappedValue) ?? value }, set: { raw.wrappedValue = $0.rawValue })
}

// MARK: - General

private struct GeneralSettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.dictationKey) private var keyRaw = DictationKey.fn.rawValue
    @AppStorage(SettingsKey.handsFreeEnabled) private var handsFree = true
    @AppStorage(SettingsKey.maxHandsFreeSeconds) private var maxHandsFreeSeconds = 360.0
    @AppStorage(SettingsKey.escapeCancels) private var escapeCancels = true
    @AppStorage(SettingsKey.showFlowBarWhenIdle) private var showFlowBar = true
    @AppStorage(SettingsKey.microphoneUID) private var microphoneUID = ""
    @AppStorage(SettingsKey.languageMode) private var languageModeRaw = LanguageMode.mixed.rawValue
    @State private var pasteShortcut = HotkeyShortcut.pasteLastDefault
    /// macOS or another app holds the chosen paste-last shortcut, so it does nothing.
    @State private var isPasteShortcutRefused = false

    var body: some View {
        let key = enumBinding($keyRaw, default: DictationKey.fn)
        VStack(alignment: .leading, spacing: 22) {
            SettingsGroup(loc.t("settings.group.dictation")) {
                SettingsRow(loc.t("settings.general.key"), detail: loc.t("settings.general.key.detail")) {
                    PopupChoice(options: DictationKey.allCases.map { ($0, loc.t($0.nameKey)) }, selection: key)
                }
                KeyTestIndicator(hotkeys: model.hotkeys, key: key.wrappedValue)
                    .padding(.bottom, 10)
                FnKeyWarnings(key: key.wrappedValue)
                RowDivider()
                SettingsRow(loc.t("settings.general.handsFree"), detail: loc.t("settings.general.handsFree.detail")) {
                    ThemedToggle(isOn: $handsFree)
                }
                RowDivider()
                SettingsRow(loc.t("settings.general.maxLength"), detail: loc.t("settings.general.maxLength.detail")) {
                    PopupChoice(options: [(120.0, loc.plural("plural.minutes", 2)), (360.0, loc.plural("plural.minutes", 6)),
                                          (600.0, loc.plural("plural.minutes", 10)), (900.0, loc.plural("plural.minutes", 15))],
                                selection: $maxHandsFreeSeconds)
                }
                .disabled(!handsFree)
                RowDivider()
                SettingsRow(loc.t("settings.general.escape"), detail: loc.t("settings.general.escape.detail")) {
                    ThemedToggle(isOn: $escapeCancels)
                }
                RowDivider()
                SettingsRow(loc.t("settings.general.pasteLast"), detail: loc.t("settings.general.pasteLast.detail")) {
                    PopupChoice(options: HotkeyShortcut.pasteLastChoices.map { ($0, $0.symbols) },
                                selection: Binding(get: { pasteShortcut }, set: { newValue in
                                    pasteShortcut = newValue
                                    if model.snapshot == nil {
                                        model.settings.pasteLastShortcut = newValue
                                        // Registers the new shortcut now, so the answer is known here.
                                        isPasteShortcutRefused = !model.wiring.applySettings()
                                    }
                                }))
                }
                if isPasteShortcutRefused {
                    InlineNotice(symbol: "exclamationmark.triangle", text: loc.t("settings.general.pasteLast.refused"))
                        .padding(.bottom, 12)
                }
            }

            SettingsGroup(loc.t("settings.group.input")) {
                SettingsRow(loc.t("settings.general.microphone"), detail: loc.t("settings.general.microphone.detail")) {
                    PopupChoice(options: microphoneOptions, selection: $microphoneUID)
                }
                RowDivider()
                SettingsRow(loc.t("settings.general.languageMode"), detail: loc.t("settings.general.languageMode.detail")) {
                    PopupChoice(options: [(LanguageMode.mixed, loc.t("languageMode.mixed")),
                                          (LanguageMode.russian, loc.t("languageMode.russian")),
                                          (LanguageMode.english, loc.t("languageMode.english"))],
                                selection: enumBinding($languageModeRaw, default: LanguageMode.mixed))
                }
            }

            SettingsGroup(loc.t("settings.group.interface")) {
                SettingsRow(loc.t("settings.system.flowBarIdle"), detail: loc.t("settings.system.flowBarIdle.detail")) {
                    ThemedToggle(isOn: $showFlowBar)
                }
                RowDivider()
                SettingsRow(loc.t("settings.general.language")) {
                    PopupChoice(options: [(AppLanguage.system, loc.t("language.system")),
                                          (AppLanguage.en, loc.t("language.en")),
                                          (AppLanguage.ru, loc.t("language.ru"))],
                                selection: $loc.language)
                }
            }
        }
        .onAppear {
            pasteShortcut = model.settings.pasteLastShortcut
            isPasteShortcutRefused = model.snapshot == nil && !model.wiring.isPasteLastRegistered
        }
    }

    private var microphoneOptions: [(value: String, title: String)] {
        var options: [(value: String, title: String)] = [("", loc.t("settings.general.microphone.default"))]
        options += model.microphones.map { ($0.uid, $0.name) }
        if !microphoneUID.isEmpty, !model.microphones.contains(where: { $0.uid == microphoneUID }) {
            options.append((microphoneUID, loc.t("settings.general.microphone.disconnected")))
        }
        return options
    }
}

/// "Hold ⌥ to test": turns green while the dictation key is held.
struct KeyTestIndicator: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @ObservedObject var hotkeys: HotkeyMonitor
    let key: DictationKey

    var body: some View {
        let isDown = model.snapshot?.isKeyDown ?? hotkeys.isKeyDown
        let isRunning = model.snapshot?.isHotkeyRunning ?? hotkeys.isRunning
        HStack(spacing: 10) {
            if isRunning {
                Circle()
                    .fill(isDown ? theme.success : theme.panelBorder)
                    .frame(width: 9, height: 9)
                if isDown {
                    Text(loc.t("keytest.passed"))
                        .foregroundStyle(theme.success)
                } else {
                    HStack(spacing: 6) {
                        Text(loc.t("keytest.prompt.prefix"))
                        KeycapBadge(symbol: key.symbol, size: 12)
                        Text(loc.t("keytest.prompt.suffix"))
                    }
                    .foregroundStyle(theme.textPrimary)
                }
                Spacer(minLength: 0)
            } else {
                Image(systemName: "keyboard.badge.exclamationmark")
                    .foregroundStyle(theme.warning)
                Text(loc.t("keytest.needsPermission"))
                    .foregroundStyle(theme.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                Button(loc.t("onboarding.permissions.open")) {
                    guard model.snapshot == nil else { return }
                    // Asking lists Slovo under Input Monitoring; the pane opens as well.
                    Task {
                        _ = await model.permissions.request(.inputMonitoring)
                        model.dictation.refreshHotkeys()
                    }
                }
                .buttonStyle(SecondaryButtonStyle())
            }
        }
        .font(theme.captionMediumFont)
        .padding(.horizontal, 12)
        .frame(minHeight: 40)
        .background(isDown && isRunning ? theme.successSoft : theme.insetBackground,
                    in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
    }
}

/// The Fn conflicts from the spec, each explained with a way to fix it.
struct FnKeyWarnings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    let key: DictationKey

    var body: some View {
        if key.mayConflictWithSystem {
            VStack(spacing: 8) {
                if let text = usageWarning {
                    InlineNotice(symbol: "globe", text: text, actionTitle: loc.t("fn.openKeyboard")) {
                        model.fnConflicts.openKeyboardSettings()
                    }
                }
                if model.isWisprFlowRunning {
                    InlineNotice(symbol: "exclamationmark.triangle", text: loc.t("fn.wisprFlow"))
                }
            }
            .padding(.bottom, 10)
        }
    }

    private var usageWarning: String? {
        switch model.fnUsage {
        case .doNothing: return nil
        case .changeInputSource: return loc.t("fn.usage.inputSource")
        case .emoji: return loc.t("fn.usage.emoji")
        case .appleDictation: return loc.t("fn.usage.dictation")
        case .unknown: return loc.t("fn.usage.unknown")
        }
    }
}

// MARK: - Recognition

@MainActor
func engineTradeoffText(_ id: EngineID, loc: Localizer) -> String {
    switch id {
    case .gigaamRNNT: return loc.t("engine.tradeoff.gigaamRNNT")
    case .gigaamCTC: return loc.t("engine.tradeoff.gigaamCTC")
    case .parakeet: return loc.t("engine.tradeoff.parakeet")
    case .appleDictation: return loc.t("engine.tradeoff.apple")
    case .whisperTurbo: return loc.t("engine.tradeoff.whisper")
    }
}

/// Choices for "English words in Russian speech"; Auto names the model it would use now.
@MainActor
func englishHelperChoices(model: AppModel, loc: Localizer) -> [(EnglishHelperChoice, String)] {
    let auto = EnglishHelperChoice.auto.engine(status: model.modelStatus)
    return [(.auto, auto.map { loc.t("englishHelper.autoUsing", $0.displayName) } ?? loc.t("englishHelper.autoNone")),
            (.parakeet, engineChoiceTitle(.parakeet, model: model, loc: loc)),
            (.whisper, engineChoiceTitle(.whisperTurbo, model: model, loc: loc)),
            (.off, loc.t("englishHelper.off"))]
}

/// Engine names for pickers; engines that cannot run say so.
@MainActor
func engineChoiceTitle(_ id: EngineID, model: AppModel, loc: Localizer) -> String {
    switch model.modelStatus(id) {
    case .notDownloaded, .damaged:
        return loc.t("settings.recognition.choice.notReady", id.displayName)
    case .systemManaged(available: false):
        // Apple Dictation cannot be downloaded; macOS provides it or not.
        return loc.t("settings.recognition.choice.unavailable", id.displayName)
    default:
        return id.displayName
    }
}

private struct RecognitionSettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.russianEngine) private var russianRaw = EngineID.gigaamRNNT.rawValue
    @AppStorage(SettingsKey.englishEngine) private var englishRaw = EngineID.parakeet.rawValue
    @AppStorage(SettingsKey.meetingEngine) private var meetingRaw = EngineID.gigaamCTC.rawValue
    @AppStorage(SettingsKey.englishHelper) private var englishHelperRaw = EnglishHelperChoice.auto.rawValue
    @AppStorage(SettingsKey.accurateMixedDictation) private var accurateMixed = false
    @AppStorage(SettingsKey.idleUnloadMinutes) private var idleMinutes = EngineIdleTimeout.minutes15.rawValue

    /// The chosen helper model when it cannot run, so English words would stay as GigaAM heard them.
    private var missingHelper: EngineID? {
        if accurateMixed { return nil }
        switch EnglishHelperChoice(rawValue: englishHelperRaw) ?? .auto {
        case .parakeet: return model.modelStatus(.parakeet).isUsable ? nil : .parakeet
        case .whisper: return model.modelStatus(.whisperTurbo).isUsable ? nil : .whisperTurbo
        case .auto, .off: return nil
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            SettingsGroup(loc.t("settings.group.engines")) {
                SettingsRow(loc.t("settings.recognition.accurateMixed"),
                            detail: loc.t("settings.recognition.accurateMixed.detail")) {
                    ThemedToggle(isOn: $accurateMixed)
                        .onChange(of: accurateMixed) { _, enabled in
                            guard enabled, model.snapshot == nil else { return }
                            model.settings.languageMode = .mixed
                            if !model.modelStatus(.whisperTurbo).isUsable {
                                model.models.startDownload(.whisperTurbo)
                            }
                        }
                }
                RowDivider()
                SettingsRow(loc.t(accurateMixed ? "settings.recognition.russianOnly" : "settings.recognition.russian"),
                            detail: loc.t("settings.recognition.russian.detail")) {
                    PopupChoice(options: EngineID.russianChoices.map { ($0, engineChoiceTitle($0, model: model, loc: loc)) },
                                selection: enumBinding($russianRaw, default: EngineID.gigaamRNNT))
                }
                RowDivider()
                SettingsRow(loc.t("settings.recognition.englishHelper"),
                            detail: loc.t("settings.recognition.englishHelper.detail")) {
                    PopupChoice(options: englishHelperChoices(model: model, loc: loc),
                                selection: enumBinding($englishHelperRaw, default: EnglishHelperChoice.auto))
                }
                .disabled(accurateMixed)
                if let missingHelper, model.snapshot == nil {
                    InlineNotice(symbol: "info.circle", text: loc.t("englishHelper.notReady", missingHelper.displayName),
                                 tone: .neutral)
                        .padding(.bottom, 10)
                }
                RowDivider()
                SettingsRow(loc.t("settings.recognition.english"), detail: loc.t("settings.recognition.english.detail")) {
                    PopupChoice(options: EngineID.englishChoices.map { ($0, engineChoiceTitle($0, model: model, loc: loc)) },
                                selection: enumBinding($englishRaw, default: EngineID.parakeet))
                }
                RowDivider()
                SettingsRow(loc.t("settings.recognition.meetingsEngine"),
                            detail: loc.t("settings.recognition.meetingsEngine.detail")) {
                    PopupChoice(options: EngineID.meetingChoices.map { ($0, engineChoiceTitle($0, model: model, loc: loc)) },
                                selection: enumBinding($meetingRaw, default: EngineID.gigaamCTC))
                }
                RowDivider()
                SettingsRow(loc.t("settings.recognition.idle"), detail: loc.t("settings.recognition.idle.detail")) {
                    PopupChoice(options: [(EngineIdleTimeout.minutes5.rawValue, loc.t("idle.minutes5")),
                                          (EngineIdleTimeout.minutes15.rawValue, loc.t("idle.minutes15")),
                                          (EngineIdleTimeout.minutes60.rawValue, loc.t("idle.minutes60")),
                                          (EngineIdleTimeout.never.rawValue, loc.t("idle.never"))],
                                selection: $idleMinutes)
                }
            }

            VStack(alignment: .leading, spacing: 8) {
                SettingsGroup(loc.t("settings.recognition.models")) {
                    ForEach(Array(EngineID.allCases.enumerated()), id: \.element) { index, id in
                        if index > 0 {
                            RowDivider()
                        }
                        EngineModelRow(id: id)
                    }
                }
                if let error = model.models.lastError, model.snapshot == nil {
                    InlineNotice(symbol: "exclamationmark.triangle.fill", text: error, tone: .destructive)
                }
                Text(loc.t("settings.recognition.models.footer"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .padding(.horizontal, 4)
            }
        }
    }
}

/// One engine: status, size, trade-off and Download/Delete. Shared with onboarding.
struct EngineModelRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    let id: EngineID

    private var isRecommended: Bool {
        id == .gigaamRNNT || id == .parakeet
    }

    var body: some View {
        ModelRow(title: id.displayName, tradeoff: engineTradeoffText(id, loc: loc), status: model.modelStatus(id),
                 downloadBytes: ModelManager.downloadBytes(for: id), isLoading: model.engineLoadState(id) == .loading,
                 deleteTitle: loc.t("engine.delete.title", id.displayName), deleteMessage: loc.t("engine.delete.message"),
                 onDownload: { model.models.startDownload(id) },
                 onCancel: { model.models.cancelDownload(id) },
                 onDelete: { model.deleteModel(id) }) {
            if isRecommended {
                StatusBadge(text: id == .gigaamRNNT ? loc.t("engine.recommended.russian") : loc.t("engine.recommended.english"),
                            tone: .accent, showsDot: false)
            } else if id == .whisperTurbo {
                StatusBadge(text: loc.t("engine.badge.accurate"), tone: .neutral, showsDot: false)
            }
        }
    }
}

// MARK: - System

private struct SystemSettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @AppStorage(SettingsKey.soundsEnabled) private var sounds = true
    @AppStorage(SettingsKey.showInDock) private var showInDock = true
    @AppStorage(SettingsKey.showMenuBarIcon) private var showMenuBarIcon = true

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            SettingsGroup(loc.t("settings.group.startup")) {
                SettingsRow(loc.t("settings.system.launchAtLogin"), detail: loc.t("settings.system.launchAtLogin.detail")) {
                    ThemedToggle(isOn: Binding(get: { model.isLaunchAtLoginEnabled },
                                               set: { model.setLaunchAtLogin($0) }))
                }
                if model.loginItem.needsApproval, model.snapshot == nil {
                    InlineNotice(symbol: "exclamationmark.circle", text: loc.t("settings.system.launchAtLogin.approval"),
                                 actionTitle: loc.t("onboarding.permissions.open")) {
                        model.loginItem.openSystemSettings()
                    }
                    .padding(.bottom, 10)
                }
            }
            SettingsGroup(loc.t("settings.group.appearance")) {
                SettingsRow(loc.t("settings.system.sounds"), detail: loc.t("settings.system.sounds.detail")) {
                    ThemedToggle(isOn: $sounds)
                }
                RowDivider()
                SettingsRow(loc.t("settings.system.dock"), detail: loc.t("settings.system.dock.detail")) {
                    // The app delegate follows the setting while a window is open.
                    ThemedToggle(isOn: $showInDock)
                }
                RowDivider()
                SettingsRow(loc.t("settings.system.menuBarIcon"), detail: loc.t("settings.system.menuBarIcon.detail")) {
                    // The menu bar item follows the setting at once.
                    ThemedToggle(isOn: $showMenuBarIcon)
                }
            }
        }
    }
}

// MARK: - Meetings

@MainActor
func retentionTitle(_ period: RetentionPeriod, loc: Localizer) -> String {
    switch period {
    case .forever: return loc.t("retention.forever")
    case .days30: return loc.t("retention.days30")
    case .days7: return loc.t("retention.days7")
    case .hours24: return loc.t("retention.hours24")
    case .never: return loc.t("retention.never")
    case .afterTranscription: return loc.t("retention.afterTranscription")
    }
}

/// "Automatic" or the number of people.
@MainActor
func expectedSpeakersTitle(_ count: Int, loc: Localizer) -> String {
    count == 0 ? loc.t("settings.meetings.people.automatic") : loc.number(count)
}

private struct MeetingSettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.recordMicrophone) private var recordMicrophone = true
    @AppStorage(SettingsKey.recordSystemAudio) private var recordSystemAudio = true
    @AppStorage(SettingsKey.recordScreen) private var recordScreen = false
    @AppStorage(SettingsKey.inboxEnabled) private var inboxEnabled = false
    @AppStorage(SettingsKey.meetingEngine) private var meetingRaw = EngineID.gigaamCTC.rawValue
    @AppStorage(SettingsKey.keepMeetingAudio) private var keepRaw = RetentionPeriod.forever.rawValue
    @AppStorage(SettingsKey.speakerSeparation) private var separateSpeakers = true
    @AppStorage(SettingsKey.expectedSpeakers) private var expectedSpeakers = 0
    @State private var markShortcut = HotkeyShortcut.markMomentDefault
    @State private var microphoneShortcut = HotkeyShortcut.microphoneToggleDefault

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            SettingsGroup(loc.t("settings.group.recording")) {
                SettingsRow(loc.t("settings.meetings.microphone"), detail: loc.t("settings.meetings.microphone.detail")) {
                    ThemedToggle(isOn: $recordMicrophone)
                }
                RowDivider()
                SettingsRow(loc.t("settings.meetings.microphoneShortcut"),
                            detail: loc.t("settings.meetings.microphoneShortcut.detail")) {
                    PopupChoice(options: HotkeyShortcut.microphoneToggleChoices.map { ($0, $0.symbols) },
                                selection: Binding(get: { microphoneShortcut }, set: { newValue in
                                    microphoneShortcut = newValue
                                    if model.snapshot == nil {
                                        model.settings.microphoneShortcut = newValue
                                    }
                                }))
                }
                if let refused = model.snapshot?.refusedMicrophoneShortcut ?? model.refusedMicrophoneShortcut,
                   refused == microphoneShortcut {
                    InlineNotice(symbol: "exclamationmark.triangle", text: loc.t("settings.meetings.markShortcut.refused"))
                        .padding(.bottom, 12)
                }
                RowDivider()
                SettingsRow(loc.t("settings.meetings.systemAudio"), detail: loc.t("settings.meetings.systemAudio.detail")) {
                    ThemedToggle(isOn: $recordSystemAudio)
                }
                RowDivider()
                SettingsRow(loc.t("settings.meetings.screen"), detail: loc.t("settings.meetings.screen.detail")) {
                    ThemedToggle(isOn: $recordScreen)
                        .onChange(of: recordScreen) { _, isOn in
                            // Asked now, so the system prompt follows this choice rather
                            // than a meeting that already started without the screen.
                            if isOn, model.snapshot == nil {
                                model.requestScreenRecordingAccess()
                            }
                        }
                }
                RowDivider()
                SettingsRow(loc.t("settings.meetings.engine"), detail: loc.t("settings.meetings.engine.detail")) {
                    PopupChoice(options: EngineID.meetingChoices.map { ($0, engineChoiceTitle($0, model: model, loc: loc)) },
                                selection: enumBinding($meetingRaw, default: EngineID.gigaamCTC))
                }
                RowDivider()
                SettingsRow(loc.t("settings.meetings.inbox"), detail: loc.t("settings.meetings.inbox.detail")) {
                    ThemedToggle(isOn: $inboxEnabled)
                }
            }

            VStack(alignment: .leading, spacing: 8) {
                SettingsGroup(loc.t("settings.group.speakers")) {
                    SettingsRow(loc.t("settings.meetings.separate"), detail: loc.t("settings.meetings.separate.detail")) {
                        ThemedToggle(isOn: $separateSpeakers)
                    }
                    RowDivider()
                    SettingsRow(loc.t("settings.meetings.people"), detail: loc.t("settings.meetings.people.detail")) {
                        PopupChoice(options: AppSettings.expectedSpeakerChoices.map { ($0, expectedSpeakersTitle($0, loc: loc)) },
                                    selection: Binding(get: { AppSettings.normalizedExpectedSpeakers(expectedSpeakers) },
                                                       set: { expectedSpeakers = $0 }))
                    }
                    .disabled(!separateSpeakers)
                    RowDivider()
                    SpeakerModelRow(diarization: model.diarization)
                }
                if let error = model.diarization.lastError, model.snapshot == nil {
                    InlineNotice(symbol: "exclamationmark.triangle.fill", text: error, tone: .destructive)
                }
            }

            SettingsGroup(loc.t("settings.group.moments")) {
                SettingsRow(loc.t("settings.meetings.markShortcut"), detail: loc.t("settings.meetings.markShortcut.detail")) {
                    PopupChoice(options: HotkeyShortcut.markMomentChoices.map { ($0, $0.symbols) },
                                selection: Binding(get: { markShortcut }, set: { newValue in
                                    markShortcut = newValue
                                    if model.snapshot == nil {
                                        model.settings.highlightShortcut = newValue
                                    }
                                }))
                }
                if let refused = model.snapshot?.refusedHighlightShortcut ?? model.refusedHighlightShortcut,
                   refused == markShortcut {
                    InlineNotice(symbol: "exclamationmark.triangle", text: loc.t("settings.meetings.markShortcut.refused"))
                        .padding(.bottom, 12)
                }
            }

            SettingsGroup(loc.t("settings.group.storage")) {
                SettingsRow(loc.t("settings.meetings.keepAudio"), detail: loc.t("settings.meetings.keepAudio.detail")) {
                    PopupChoice(options: RetentionPeriod.meetingAudioChoices.map { ($0, retentionTitle($0, loc: loc)) },
                                selection: enumBinding($keepRaw, default: RetentionPeriod.forever))
                }
            }
            InlineNotice(symbol: "person.wave.2", text: loc.t("meetings.consent.detail"), tone: .neutral)
        }
        .onAppear {
            markShortcut = model.snapshot?.refusedHighlightShortcut ?? model.settings.highlightShortcut
            microphoneShortcut = model.snapshot?.refusedMicrophoneShortcut ?? model.settings.microphoneShortcut
        }
    }
}

/// The speaker model with Download, Cancel and Delete; below macOS 15 it only says why
/// it cannot run.
private struct SpeakerModelRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @ObservedObject var diarization: DiarizationService

    var body: some View {
        ModelRow(title: loc.t("settings.meetings.speakerModel"), tradeoff: loc.t("settings.meetings.speakerModel.tradeoff"),
                 status: model.speakerModelStatus, downloadBytes: DiarizationService.downloadBytes,
                 isLoading: model.speakerModelLoadState == .loading,
                 unavailableReason: model.isSpeakerSeparationSupported ? nil : loc.t("settings.meetings.speakerModel.unsupported"),
                 deleteTitle: loc.t("settings.meetings.speakerModel.delete.title"),
                 deleteMessage: loc.t("settings.meetings.speakerModel.delete.message"),
                 onDownload: { diarization.startDownload() },
                 onCancel: { diarization.cancelDownload() },
                 onDelete: { model.deleteSpeakerModel() })
    }
}

// MARK: - Data and privacy

private struct PrivacySettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.keepHistory) private var historyRaw = RetentionPeriod.forever.rawValue
    @AppStorage(SettingsKey.keepDictationAudio) private var audioRaw = RetentionPeriod.days7.rawValue
    @AppStorage(SettingsKey.mcpEnabled) private var claudeAccess = false
    @State private var isConfirmingDelete = false
    @State private var retentionPreview = RetentionPreview()

    private var retentionPreviewText: String {
        guard !retentionPreview.isEmpty else { return loc.t("settings.privacy.preview.none") }
        let size = ByteCountFormatter.string(fromByteCount: retentionPreview.audioBytes, countStyle: .file)
        return loc.t("settings.privacy.preview.some", retentionPreview.entries, retentionPreview.dictationAudio,
                     retentionPreview.meetingAudio, size)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 12) {
                localCard
                if claudeAccess {
                    InlineNotice(symbol: "exclamationmark.bubble", text: loc.t("settings.privacy.claudeOn"),
                                 actionTitle: loc.t("settings.privacy.claudeOn.link")) {
                        model.settingsSection = .integrations
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))

            SettingsGroup(loc.t("settings.group.permissions")) {
                PermissionRow(kind: .microphone, symbol: "mic", title: loc.t("permission.microphone"),
                              detail: loc.t("onboarding.permissions.microphone.detail"))
                RowDivider()
                PermissionRow(kind: .accessibility, symbol: "accessibility", title: loc.t("permission.accessibility"),
                              detail: loc.t("onboarding.permissions.accessibility.detail"))
                RowDivider()
                PermissionRow(kind: .inputMonitoring, symbol: "keyboard", title: loc.t("permission.inputMonitoring"),
                              detail: loc.t("onboarding.permissions.inputMonitoring.detail"))
                RowDivider()
                PermissionRow(kind: .systemAudioRecording, symbol: "waveform", title: loc.t("permission.systemAudioRecording"),
                              detail: loc.t("settings.permissions.systemAudioRecording.detail"))
                RowDivider()
                PermissionRow(kind: .screenRecording, symbol: "rectangle.inset.filled", title: loc.t("permission.screenRecording"),
                              detail: loc.t("settings.permissions.screenRecording.detail"))
            }

            SettingsGroup(loc.t("settings.group.retention")) {
                SettingsRow(loc.t("settings.privacy.keepHistory"), detail: loc.t("settings.privacy.keepHistory.detail")) {
                    PopupChoice(options: RetentionPeriod.historyChoices.map { ($0, retentionTitle($0, loc: loc)) },
                                selection: enumBinding($historyRaw, default: RetentionPeriod.forever))
                }
                RowDivider()
                SettingsRow(loc.t("settings.privacy.keepAudio"), detail: loc.t("settings.privacy.keepAudio.detail")) {
                    PopupChoice(options: RetentionPeriod.dictationAudioChoices.map { ($0, retentionTitle($0, loc: loc)) },
                                selection: enumBinding($audioRaw, default: RetentionPeriod.days7))
                }
                RowDivider()
                SettingsRow(loc.t("settings.privacy.preview"), detail: retentionPreviewText) { EmptyView() }
            }
            .onAppear { retentionPreview = model.retention.preview() }
            .onChange(of: historyRaw) { _ in retentionPreview = model.retention.preview() }
            .onChange(of: audioRaw) { _ in retentionPreview = model.retention.preview() }

            SettingsGroup(loc.t("settings.group.data")) {
                SettingsRow(loc.t("settings.privacy.openFolder"), detail: displayPath(model.directories.base)) {
                    Button(loc.t("common.open")) {
                        model.openDataFolder()
                    }
                    .buttonStyle(SecondaryButtonStyle())
                }
                RowDivider()
                SettingsRow(loc.t("settings.privacy.deleteAll"), detail: loc.t("settings.privacy.deleteAll.detail")) {
                    Button(loc.t("settings.privacy.deleteAll.button")) {
                        isConfirmingDelete = true
                    }
                    .buttonStyle(SecondaryButtonStyle(isDestructive: true))
                    .disabled(model.historyEntries.isEmpty && !model.history.hasHistoryToDelete)
                }
            }
        }
        .alert(loc.t("settings.privacy.deleteAll.confirmTitle"), isPresented: $isConfirmingDelete) {
            Button(loc.t("settings.privacy.deleteAll.confirm"), role: .destructive) {
                model.deleteAllHistory()
            }
            Button(loc.t("common.cancel"), role: .cancel) {}
        } message: {
            Text(loc.t("settings.privacy.deleteAll.confirmMessage", loc.plural("plural.dictations", model.historyEntries.count)))
        }
    }

    private var localCard: some View {
        HStack(alignment: .center, spacing: 14) {
            Image(systemName: "lock.shield")
                .font(.system(size: 24, weight: .regular))
                .foregroundStyle(theme.bannerText)
                .frame(width: 44, height: 44)
                .background(LinearGradient(colors: [theme.bannerGradientStart, theme.bannerGradientEnd],
                                           startPoint: .topLeading, endPoint: .bottomTrailing),
                            in: RoundedRectangle(cornerRadius: 11))
            VStack(alignment: .leading, spacing: 3) {
                Text(loc.t("settings.privacy.local"))
                    .font(theme.sectionTitleFont)
                    .foregroundStyle(theme.textPrimary)
                Text(loc.t("settings.privacy.local.detail"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// A path with the home folder shortened to ~.
func displayPath(_ url: URL) -> String {
    let home = FileManager.default.homeDirectoryForCurrentUser.path
    let path = url.path
    return path.hasPrefix(home) ? "~" + path.dropFirst(home.count) : path
}

// MARK: - About

private struct AboutSettings: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    /// "Version 1.0 (42)", or a plain note when running outside the app bundle.
    private var versionLine: String {
        guard let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String else {
            return loc.t("settings.about.devBuild")
        }
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
        return loc.t("settings.about.version", version, build)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            HStack(spacing: 16) {
                AppLogo(size: 64)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Slovo")
                        .font(.system(size: 26, weight: .regular, design: .serif))
                        .foregroundStyle(theme.textPrimary)
                    Text(loc.t("settings.about.tagline"))
                        .font(theme.bodyFont)
                        .foregroundStyle(theme.textSecondary)
                    Text(versionLine)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                }
            }

            SettingsGroup(loc.t("settings.about.licenses")) {
                LicenseRow(name: "GigaAM v3", license: "MIT", detail: loc.t("settings.about.gigaam"))
                RowDivider()
                LicenseRow(name: "NVIDIA Parakeet TDT 0.6B v3", license: "CC-BY-4.0", detail: loc.t("settings.about.parakeet"))
                RowDivider()
                LicenseRow(name: "sherpa-onnx", license: "Apache-2.0", detail: loc.t("settings.about.sherpa"))
                RowDivider()
                LicenseRow(name: "FluidAudio", license: "Apache-2.0", detail: loc.t("settings.about.fluidaudio"))
                RowDivider()
                LicenseRow(name: "OpenAI Whisper large-v3 turbo", license: "MIT", detail: loc.t("settings.about.whisper"))
                RowDivider()
                LicenseRow(name: "whisper.cpp", license: "MIT", detail: loc.t("settings.about.whispercpp"))
                RowDivider()
                LicenseRow(name: "pyannote community-1", license: "CC-BY-4.0", detail: loc.t("settings.about.pyannote"))
            }

            SettingsGroup {
                SettingsRow(loc.t("help.openModels"), detail: displayPath(model.models.modelsRoot)) {
                    Button(loc.t("common.open")) {
                        model.openModelsFolder()
                    }
                    .buttonStyle(SecondaryButtonStyle())
                }
            }
        }
    }
}

private struct LicenseRow: View {
    @Environment(\.theme) private var theme
    let name: String
    let license: String
    let detail: String

    var body: some View {
        SettingsRow(name, detail: detail) {
            Text(license)
                .font(theme.monoCaptionFont)
                .foregroundStyle(theme.textSecondary)
                .padding(.horizontal, 8)
                .frame(height: 22)
                .background(theme.insetBackground, in: RoundedRectangle(cornerRadius: 6))
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(theme.panelBorder, lineWidth: 1))
        }
    }
}
