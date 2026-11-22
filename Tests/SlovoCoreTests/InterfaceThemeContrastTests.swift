import Foundation
import XCTest

/// Speaker labels are 11 pt text in the speaker's colour. The app target has no tests, so
/// this reads both themes from `Theme.swift` and checks WCAG AA (4.5:1) on every
/// background a label sits on: the panel, a hovered row, and `accentSoft` over the panel,
/// the flash after jumping to a moment.
final class InterfaceThemeContrastTests: XCTestCase {
    private static let minimumRatio = 4.5

    func testSpeakerColoursKeepAAOnPanelHoverAndFocus() throws {
        let themeFile = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Sources/Slovo/Design/Theme.swift")
        let source = try String(contentsOf: themeFile, encoding: .utf8)

        for name in ["light", "dark"] {
            let theme = try XCTUnwrap(Self.block(named: name, in: source), "No \(name) theme in Theme.swift")
            let panel = try Self.color("panelBackground", in: theme)
            let backgrounds: [(String, [Double])] = [
                ("panelBackground", panel.rgb),
                ("rowHover", try Self.color("rowHover", in: theme).rgb),
                ("accentSoft over the panel", try Self.color("accentSoft", in: theme).composited(over: panel.rgb))
            ]
            // The order of `Theme.speakerPalette`.
            let speakers = [try Self.color("accentText", in: theme), try Self.color("secondaryAccent", in: theme)]
                + (try Self.extraSpeakerColors(in: theme))
            XCTAssertEqual(speakers.count, 8, "\(name): the speaker palette should have 8 colours")

            for (index, speaker) in speakers.enumerated() {
                for (backgroundName, background) in backgrounds {
                    let ratio = Self.contrast(speaker.rgb, background)
                    XCTAssertGreaterThanOrEqual(
                        ratio, Self.minimumRatio,
                        "\(name) speaker colour \(index) on \(backgroundName): \((ratio * 100).rounded() / 100):1")
                }
            }
        }
    }

    // MARK: - Reading Theme.swift

    private struct HexColor {
        let rgb: [Double]
        let opacity: Double

        func composited(over background: [Double]) -> [Double] {
            zip(rgb, background).map { opacity * $0 + (1 - opacity) * $1 }
        }
    }

    /// The arguments of `static let <name> = Theme(`, up to its closing parenthesis.
    private static func block(named name: String, in source: String) -> String? {
        guard let start = source.range(of: "static let \(name) = Theme("),
              let end = source.range(of: "\n    )", range: start.upperBound..<source.endIndex) else {
            return nil
        }
        return String(source[start.upperBound..<end.lowerBound])
    }

    private static func color(_ token: String, in theme: String) throws -> HexColor {
        let pattern = try NSRegularExpression(
            pattern: "\\b\(token): Color\\(hex: 0x([0-9A-Fa-f]{6})(?:, opacity: ([0-9.]+))?\\)")
        let range = NSRange(theme.startIndex..., in: theme)
        let match = try XCTUnwrap(pattern.firstMatch(in: theme, range: range), "No \(token) colour")
        let hex = try XCTUnwrap(Range(match.range(at: 1), in: theme).map { String(theme[$0]) })
        let opacity = Range(match.range(at: 2), in: theme).flatMap { Double(theme[$0]) } ?? 1
        return HexColor(rgb: try rgb(hex), opacity: opacity)
    }

    private static func extraSpeakerColors(in theme: String) throws -> [HexColor] {
        let list = try XCTUnwrap(theme.range(of: #"extraSpeakerColors: \[[^\]]*\]"#, options: .regularExpression),
                                 "No extraSpeakerColors")
        let text = String(theme[list])
        let pattern = try NSRegularExpression(pattern: "0x([0-9A-Fa-f]{6})")
        return try pattern.matches(in: text, range: NSRange(text.startIndex..., in: text)).map { match in
            let hex = try XCTUnwrap(Range(match.range(at: 1), in: text).map { String(text[$0]) })
            return HexColor(rgb: try rgb(hex), opacity: 1)
        }
    }

    private static func rgb(_ hex: String) throws -> [Double] {
        let value = try XCTUnwrap(UInt32(hex, radix: 16), "Bad hex \(hex)")
        return [Double((value >> 16) & 0xFF), Double((value >> 8) & 0xFF), Double(value & 0xFF)]
    }

    // MARK: - WCAG 2

    private static func contrast(_ first: [Double], _ second: [Double]) -> Double {
        let lighter = max(luminance(first), luminance(second))
        let darker = min(luminance(first), luminance(second))
        return (lighter + 0.05) / (darker + 0.05)
    }

    private static func luminance(_ rgb: [Double]) -> Double {
        let linear = rgb.map { channel -> Double in
            let value = channel / 255
            return value <= 0.03928 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2]
    }
}
