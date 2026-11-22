import AppKit
import Combine

/// The entry point. Slovo keeps its windows and menus in AppKit (`AppDelegate`): the
/// SwiftUI app lifecycle opens an empty Settings window at launch for a menu bar app
/// whose only scene is `Settings`.
@main
enum SlovoMain {
    /// `--mcp` is checked before anything else: Claude starts this executable as a headless
    /// server, which must not create the app, its stores or a second dictation key listener.
    @MainActor
    static func main() {
        MCPServerMode.runIfRequested()
        MicrophoneDiagnostics.runIfRequested()
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        withExtendedLifetime(delegate) {
            application.run()
        }
    }
}

/// The menu bar menus while a Slovo window is in front: the app menu with Settings…,
/// Edit for text fields, Window and Slovo's own Help items. Rebuilt when the interface
/// language changes.
@MainActor
final class MainMenuController {
    private let localizer: Localizer
    private weak var delegate: AppDelegate?
    private var cancellable: AnyCancellable?

    init(localizer: Localizer, delegate: AppDelegate) {
        self.localizer = localizer
        self.delegate = delegate
        rebuild()
        // `@Published` fires before the new language is stored.
        cancellable = localizer.$language
            .dropFirst()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.rebuild() }
    }

    private func rebuild() {
        let loc = localizer
        let menu = NSMenu()

        let appMenu = NSMenu(title: "Slovo")
        appMenu.addItem(withTitle: loc.t("menu.about"),
                        action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(action(loc.t("menu.settings"), key: ",") { $0.openSettings() })
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: loc.t("menu.hide"), action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = appMenu.addItem(withTitle: loc.t("menu.hideOthers"),
                                         action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(withTitle: loc.t("menu.showAll"),
                        action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: loc.t("menu.quit"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        add(appMenu, to: menu)

        let editMenu = NSMenu(title: loc.t("menu.edit"))
        editMenu.addItem(withTitle: loc.t("menu.undo"), action: Selector(("undo:")), keyEquivalent: "z")
        let redo = editMenu.addItem(withTitle: loc.t("menu.redo"), action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: loc.t("menu.cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: loc.t("menu.copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: loc.t("menu.paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: loc.t("menu.selectAll"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        add(editMenu, to: menu)

        let windowMenu = NSMenu(title: loc.t("menu.window"))
        windowMenu.addItem(withTitle: loc.t("menu.minimize"), action: #selector(NSWindow.performMiniaturize(_:)),
                           keyEquivalent: "m")
        windowMenu.addItem(withTitle: loc.t("menu.zoom"), action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        windowMenu.addItem(.separator())
        windowMenu.addItem(withTitle: loc.t("menu.close"), action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        add(windowMenu, to: menu)

        let helpMenu = NSMenu(title: loc.t("sidebar.help"))
        helpMenu.addItem(action(loc.t("help.shortcuts")) { $0.model?.showShortcuts() })
        helpMenu.addItem(action(loc.t("help.openModels")) { $0.model?.openModelsFolder() })
        helpMenu.addItem(action(loc.t("help.openLogs")) { $0.model?.openLogs() })
        helpMenu.addItem(.separator())
        helpMenu.addItem(action(loc.t("help.showOnboarding")) { $0.showOnboarding() })
        add(helpMenu, to: menu)

        NSApp.mainMenu = menu
        NSApp.windowsMenu = windowMenu
    }

    private func action(_ title: String, key: String = "", _ perform: @escaping @MainActor (AppDelegate) -> Void) -> NSMenuItem {
        let item = ClosureMenuItem(title: title) { [weak self] in
            guard let delegate = self?.delegate else { return }
            perform(delegate)
        }
        item.keyEquivalent = key
        return item
    }

    private func add(_ submenu: NSMenu, to menu: NSMenu) {
        let item = NSMenuItem(title: submenu.title, action: nil, keyEquivalent: "")
        item.submenu = submenu
        menu.addItem(item)
    }
}
