import XCTest
@testable import SlovoCore

/// Which permission buttons ask macOS first. Asking is what lists Slovo in the privacy
/// panes; a pane opened before Slovo ever asked does not show it at all.
@MainActor
final class FixAppPermissionsTests: XCTestCase {
    func testPermissionsWithARequestAPIAskBeforeOpeningSettings() {
        XCTAssertTrue(Permissions.asksThroughRequest(.microphone))
        XCTAssertTrue(Permissions.asksThroughRequest(.accessibility))
        XCTAssertTrue(Permissions.asksThroughRequest(.inputMonitoring))
        XCTAssertTrue(Permissions.asksThroughRequest(.screenRecording))
    }

    func testSystemAudioRecordingOnlyOpensSettings() {
        // macOS has no request call for it; the first meeting's tap makes the system ask.
        XCTAssertFalse(Permissions.asksThroughRequest(.systemAudioRecording))
    }

    func testEveryPermissionHasASettingsPane() {
        for kind in PermissionKind.allCases {
            XCTAssertNotNil(Permissions.settingsURL(for: kind), "\(kind)")
        }
    }
}
