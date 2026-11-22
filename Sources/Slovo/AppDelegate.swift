import AppKit
import SlovoCore
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    /// Created before the scene body asks for it; follows `app.language`.
    let localizer: Localizer

    private(set) var model: AppModel?
    private var mainWindow: MainWindowController?
    private var onboardingWindow: OnboardingWindowController?
    private var statusItem: StatusItemController?
    private var flowBar: FlowBarController?
    private var mainMenu: MainMenuController?
    private var defaultsObserver: NSObjectProtocol?
    /// macOS started Slovo at login: it stays in the menu bar instead of opening its window.
    private var launchedAsLoginItem = false

    override init() {
        AppSettings(defaults: .standard).registerDefaults()
        localizer = Localizer(defaults: .standard)
        super.init()
    }

    func applicationWillFinishLaunching(_ notification: Notification) {
        launchedAsLoginItem = Self.isLoginItemLaunch()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        #if DEBUG
        if let directory = ScreenRenderer.requestedDirectory(in: CommandLine.arguments) {
            NSApp.setActivationPolicy(.prohibited)
            let succeeded = ScreenRenderer.renderAll(to: directory)
            exit(succeeded ? 0 : 1)
        }
        #endif

        NSApp.setActivationPolicy(.accessory)
        let model = AppModel.live(localizer: localizer)
        self.model = model
        model.showMainWindow = { [weak self] in self?.showMainWindow() }
        model.showOnboardingWindow = { [weak self] step in self?.showOnboarding(from: step) }

        mainMenu = MainMenuController(localizer: localizer, delegate: self)
        statusItem = StatusItemController(model: model)
        let flowBar = FlowBarController(model: model)
        flowBar.show()
        self.flowBar = flowBar
        mainWindow = MainWindowController(model: model) { [weak self] in self?.updateDockPolicy() }
        // "Show Slovo in the Dock" applies at once while a window is open.
        defaultsObserver = NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: model.settings.defaults, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.updateDockPolicy()
            }
        }

        model.start()
        if !model.settings.onboardingCompleted {
            showOnboarding()
        } else if !(launchedAsLoginItem || Self.isLoginItemLaunch()) {
            showMainWindow()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        // A menu bar app keeps running with no windows open.
        false
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool {
        true
    }

    /// Opening Slovo again (Finder, Spotlight, Launchpad) brings its window forward: with the
    /// menu bar icon hidden it is the way back in. The system's `hasVisibleWindows` counts the
    /// Flow bar panel, which shows over a focused text field such as Spotlight's, so Slovo's own
    /// windows decide instead.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if onboardingWindow?.isOpen ?? false {
            // An open walkthrough keeps its step.
            showOnboarding()
        } else {
            showMainWindow()
        }
        return true
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        // Permission rows and the Fn warnings update after the user returns from System Settings.
        model?.applicationDidBecomeActive()
    }

    func applicationWillTerminate(_ notification: Notification) {
        model?.prepareForTermination()
    }

    func showMainWindow() {
        mainWindow?.show()
    }

    func openSettings() {
        model?.openSettings()
    }

    /// Opens onboarding; Help starts from the welcome step, the sidebar's setup card from permissions.
    func showOnboarding(from step: OnboardingStep = .welcome) {
        guard let model else { return }
        if onboardingWindow == nil {
            onboardingWindow = OnboardingWindowController(model: model) { [weak self] in self?.updateDockPolicy() }
        }
        onboardingWindow?.show(from: step)
    }

    /// The Dock icon (and with it the menu bar and ⌘-Tab) shows while any of Slovo's
    /// windows is open, the walkthrough included, and the setting allows it.
    private func updateDockPolicy() {
        guard let model else { return }
        let isWindowOpen = (mainWindow?.isOpen ?? false) || (onboardingWindow?.isOpen ?? false)
        let policy: NSApplication.ActivationPolicy = model.settings.showInDock && isWindowOpen ? .regular : .accessory
        if NSApp.activationPolicy() != policy {
            NSApp.setActivationPolicy(policy)
        }
    }

    /// Whether the launch event says macOS opened Slovo as a login item.
    private static func isLoginItemLaunch() -> Bool {
        guard let event = NSAppleEventManager.shared().currentAppleEvent,
              event.eventID == AEEventID(kAEOpenApplication),
              let property = event.paramDescriptor(forKeyword: AEKeyword(keyAEPropData)) else {
            return false
        }
        return property.enumCodeValue == OSType(keyAELaunchedAsLogInItem)
    }
}
