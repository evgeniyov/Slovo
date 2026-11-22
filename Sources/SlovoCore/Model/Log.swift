import Foundation
import os

/// Loggers for every part of Slovo share one subsystem so `log stream` can filter on it.
public enum SlovoLog {
    public static let subsystem = "local.slovo.Slovo"

    public static func logger(_ category: String) -> Logger {
        Logger(subsystem: subsystem, category: category)
    }
}

/// A monotonic time source in seconds. Timing never uses `Date()` differences, which
/// jump when the wall clock changes; tests inject a fake to drive timeouts.
public protocol UptimeProviding: Sendable {
    func uptime() -> TimeInterval
}

public struct SystemUptime: UptimeProviding {
    public init() {}

    public func uptime() -> TimeInterval {
        ProcessInfo.processInfo.systemUptime
    }
}
