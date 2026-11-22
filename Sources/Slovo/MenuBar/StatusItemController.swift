import AppKit
import Combine
import SlovoCore

/// The menu bar item. The menu is rebuilt each time it opens, so titles follow the
/// interface language and the dictation and meeting state. "Show the icon in the menu bar"
/// can hide it; a meeting recording still shows it, so nobody is recorded unnoticed.
@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    private let statusItem: NSStatusItem
    private let model: AppModel
    private var cancellables: Set<AnyCancellable> = []

    init(model: AppModel) {
        self.model = model
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()
        let menu = NSMenu()
        // Items are enabled by hand below; with auto-enabling, AppKit would switch every
        // item that has a target back on, so "Start hands-free" during a meeting or
        // "Paste last transcript" with nothing to paste would stay clickable.
        menu.autoenablesItems = false
        menu.delegate = self
        statusItem.menu = menu
        updateIcon()
        updateVisibility()

        // The setting applies at once, from Settings or from `defaults write`.
        NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification, object: model.settings.defaults)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.updateVisibility() }
            .store(in: &cancellables)

        model.dictation.$state
            .map(\.isRecording)
            .removeDuplicates()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.updateIcon() }
            .store(in: &cancellables)
        model.meetingRecorder.$state
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in
                self?.updateIcon()
                self?.updateVisibility()
            }
            .store(in: &cancellables)
        // A meeting is recorded with the window closed, so the icon is where a microphone
        // that went away can be seen at all.
        model.meetingRecorder.$microphoneStatus
            .removeDuplicates()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.updateIcon() }
            .store(in: &cancellables)
        // The same goes for a microphone switched off during a call.
        model.meetingRecorder.$isMicrophoneOn
            .removeDuplicates()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.updateIcon() }
            .store(in: &cancellables)
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let loc = model.localizer
        let dictation = model.dictation
        let isMeetingRecording = model.meetingRecorder.isMeetingRecording

        let status: String
        switch dictation.state {
        case .recording: status = loc.t("menu.status.recording")
        case .processing: status = loc.t("menu.status.processing")
        case .idle, .message:
            status = isMeetingRecording ? loc.t("flowbar.meetingInProgress")
                                        : loc.t("flowbar.hint", model.settings.dictationKey.symbol)
        }
        let statusItem = NSMenuItem(title: status, action: nil, keyEquivalent: "")
        statusItem.isEnabled = false
        menu.addItem(statusItem)
        menu.addItem(.separator())

        switch dictation.state {
        case .recording:
            menu.addItem(item(loc.t("menu.finishDictation")) { dictation.finish() })
            menu.addItem(item(loc.t("menu.cancelDictation")) { dictation.cancel() })
        case .processing:
            // A dictation still being recognized can be abandoned; a new one cannot start.
            menu.addItem(item(loc.t("menu.cancelDictation")) { dictation.cancel() })
        case .idle, .message:
            let start = item(loc.t("menu.startHandsFree")) { dictation.startHandsFree() }
            start.isEnabled = !isMeetingRecording
            menu.addItem(start)
        }
        let paste = item(loc.t("menu.pasteLast")) {
            Task { await dictation.pasteLastTranscript() }
        }
        paste.isEnabled = dictation.lastTranscript != nil
        let shortcut = model.settings.pasteLastShortcut
        // Show the shortcut only while macOS actually delivers it to Slovo.
        if shortcut.keyCode == 9, model.wiring.isPasteLastRegistered {
            paste.keyEquivalent = "v"
            paste.keyEquivalentModifierMask = modifierFlags(shortcut.modifiers)
        }
        menu.addItem(paste)
        menu.addItem(.separator())

        if isMeetingRecording {
            menu.addItem(item(loc.t("menu.stopMeeting")) { [model] in model.stopMeeting() })
            // During a call the window is behind the call app, so the switch is here too;
            // the check mark is its state.
            let microphone = item(loc.t("menu.recordMicrophone")) { [model] in model.toggleMeetingMicrophone() }
            microphone.state = model.meetingRecorder.isMicrophoneOn ? .on : .off
            let microphoneShortcut = model.settings.microphoneShortcut
            if microphoneShortcut.keyCode == 32 {
                microphone.keyEquivalent = "u"
                microphone.keyEquivalentModifierMask = modifierFlags(microphoneShortcut.modifiers)
            }
            menu.addItem(microphone)
            let mark = item(loc.t("menu.markMoment")) { [model] in model.markMoment() }
            // The global shortcut does the marking; the menu only shows it.
            let shortcut = model.settings.highlightShortcut
            if shortcut.keyCode == 46 {
                mark.keyEquivalent = "m"
                mark.keyEquivalentModifierMask = modifierFlags(shortcut.modifiers)
            }
            menu.addItem(mark)
        } else {
            menu.addItem(item(loc.t("menu.newMeeting")) { [model] in model.startMeeting() })
        }
        menu.addItem(.separator())

        menu.addItem(item(loc.t("menu.openSlovo")) { [model] in model.showMainWindow() })
        let settings = item(loc.t("menu.settings")) { [model] in model.openSettings() }
        settings.keyEquivalent = ","
        menu.addItem(settings)

        let help = NSMenuItem(title: loc.t("sidebar.help"), action: nil, keyEquivalent: "")
        let helpMenu = NSMenu()
        helpMenu.autoenablesItems = false
        helpMenu.addItem(item(loc.t("help.shortcuts")) { [model] in model.showShortcuts() })
        helpMenu.addItem(item(loc.t("help.troubleshooting")) { [model] in model.showTroubleshooting() })
        helpMenu.addItem(item(loc.t("help.openModels")) { [model] in model.openModelsFolder() })
        helpMenu.addItem(item(loc.t("help.openLogs")) { [model] in model.openLogs() })
        helpMenu.addItem(.separator())
        helpMenu.addItem(item(loc.t("help.showOnboarding")) { [model] in model.showOnboarding() })
        help.submenu = helpMenu
        menu.addItem(help)
        menu.addItem(.separator())

        let quit = item(loc.t("menu.quit")) { NSApp.terminate(nil) }
        quit.keyEquivalent = "q"
        menu.addItem(quit)
    }

    private func item(_ title: String, action: @escaping @MainActor () -> Void) -> NSMenuItem {
        ClosureMenuItem(title: title, handler: action)
    }

    private func modifierFlags(_ modifiers: HotkeyModifiers) -> NSEvent.ModifierFlags {
        var flags: NSEvent.ModifierFlags = []
        if modifiers.contains(.command) { flags.insert(.command) }
        if modifiers.contains(.control) { flags.insert(.control) }
        if modifiers.contains(.option) { flags.insert(.option) }
        if modifiers.contains(.shift) { flags.insert(.shift) }
        return flags
    }

    /// Shown when the setting allows it or while a meeting records.
    private func updateVisibility() {
        let visible = Self.isVisible(showSetting: model.settings.showMenuBarIcon,
                                     isMeetingRecording: model.meetingRecorder.isMeetingRecording)
        if statusItem.isVisible != visible {
            statusItem.isVisible = visible
        }
    }

    static func isVisible(showSetting: Bool, isMeetingRecording: Bool) -> Bool {
        showSetting || isMeetingRecording
    }

    private func updateIcon() {
        let symbol: String
        if model.meetingRecorder.isMeetingRecording {
            // The recording goes on without the microphone, so the icon warns rather than
            // stops: it returns to the plain dot as soon as a device comes back. A microphone
            // switched off on purpose shows too, so the state can be checked without the window.
            if model.meetingRecorder.microphoneStatus == .lost {
                symbol = "exclamationmark.circle"
            } else {
                symbol = model.meetingRecorder.isMicrophoneOn ? "record.circle" : "mic.slash.circle"
            }
        } else if model.dictation.state.isRecording {
            symbol = "waveform.circle.fill"
        } else {
            symbol = "waveform"
        }
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "Slovo")
        image?.isTemplate = true
        statusItem.button?.image = image
    }
}
