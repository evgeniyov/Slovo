import AppKit
import Foundation

/// Tracks the frontmost app other than Slovo, so clicking the Flow bar or opening
/// the window does not make Slovo the dictation target.
@MainActor
public final class FrontmostApp: FrontmostAppProviding {
    private let ownBundleID: String?
    private var lastApp: AppIdentity?
    private var observer: NSObjectProtocol?

    public init(ownBundleID: String? = Bundle.main.bundleIdentifier) {
        self.ownBundleID = ownBundleID
    }

    /// Starts observing `NSWorkspace.didActivateApplicationNotification`.
    public func startObserving() {
        guard observer == nil else { return }
        if let app = NSWorkspace.shared.frontmostApplication {
            record(app)
        }
        observer = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self] notification in
            let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            MainActor.assumeIsolated {
                if let app {
                    self?.record(app)
                }
            }
        }
    }

    public func stopObserving() {
        if let observer {
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
        }
        observer = nil
    }

    /// The frontmost app when it is not Slovo, else the last other app that was.
    public func currentApp() -> AppIdentity? {
        if let app = NSWorkspace.shared.frontmostApplication {
            record(app)
        }
        return lastApp
    }

    private func record(_ app: NSRunningApplication) {
        record(bundleID: app.bundleIdentifier, name: app.localizedName)
    }

    /// Remembers an activated app unless it is Slovo or has no bundle id.
    func record(bundleID: String?, name: String?) {
        guard let identity = Self.identity(bundleID: bundleID, name: name, ownBundleID: ownBundleID) else { return }
        lastApp = identity
    }

    static func identity(bundleID: String?, name: String?, ownBundleID: String?) -> AppIdentity? {
        guard let bundleID, !bundleID.isEmpty, bundleID != ownBundleID else { return nil }
        let trimmedName = name?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return AppIdentity(bundleID: bundleID, name: trimmedName.isEmpty ? bundleID : trimmedName)
    }
}
