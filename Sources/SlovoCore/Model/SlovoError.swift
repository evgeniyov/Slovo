import Foundation

/// An error whose message is already localized through `CoreStrings`, for services
/// that have no more specific error type.
public struct SlovoError: LocalizedError, Equatable, Sendable {
    public let message: String

    public init(_ message: String) {
        self.message = message
    }

    public var errorDescription: String? { message }
}
