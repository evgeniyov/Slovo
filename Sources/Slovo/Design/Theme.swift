import SwiftUI

/// Every colour, radius and type style the interface uses. Views read the theme from
/// the environment and never write literal colours.
struct Theme: Equatable {
    // Surfaces
    let windowBackground: Color
    let sidebarBackground: Color
    let panelBackground: Color
    let panelBorder: Color
    /// Hairlines between rows inside a card; lighter than the card border.
    let divider: Color
    /// Search fields and text inputs on the panel.
    let fieldBackground: Color
    let sidebarSelection: Color
    let rowHover: Color
    /// Quiet inset areas inside cards: style samples, transcript timestamps.
    let insetBackground: Color
    let shadow: Color

    // Text
    let textPrimary: Color
    let textSecondary: Color

    // Buttons
    let primaryButtonBackground: Color
    let primaryButtonText: Color
    let secondaryButtonBackground: Color
    let secondaryButtonText: Color

    // Accent: toggles, progress, the recording dot
    let accent: Color
    /// The accent used as text on the panel, contrast-checked.
    let accentText: Color
    /// Tinted backgrounds of selected chips and badges.
    let accentSoft: Color

    // Keycap badge
    let keycapFill: Color
    let keycapBorder: Color
    let keycapText: Color

    // Banner cards
    let bannerGradientStart: Color
    let bannerGradientEnd: Color
    let bannerText: Color
    let bannerSecondaryText: Color
    let bannerButtonBackground: Color
    let bannerButtonText: Color
    let bannerChipBackground: Color

    // Flow bar
    let flowBarBackground: Color
    let flowBarIdle: Color
    let flowBarForeground: Color
    let flowBarSecondary: Color
    let flowBarButtonBackground: Color
    let flowBarBorder: Color
    let flowBarAccent: Color
    let flowBarWarning: Color

    // Status
    let success: Color
    let successSoft: Color
    let warning: Color
    let warningSoft: Color
    let destructive: Color
    let destructiveSoft: Color

    // Charts
    let calendarEmpty: Color
    /// Four intensities of the activity calendar, lightest first.
    let calendarLevels: [Color]
    /// The system audio meter and the "Others" speaker, set apart from the accent.
    let secondaryAccent: Color

    // Switches
    let toggleKnob: Color

    // Speakers
    /// One text colour per speaker, indexed by `MeetingSpeaker.colorIndex`. Index 0 is
    /// `accentText` and index 1 `secondaryAccent`, so "Me" and "Others" look as they did
    /// before speakers were separated; every entry keeps at least 4.5:1 on the panel.
    var speakerPalette: [Color] {
        [accentText, secondaryAccent] + extraSpeakerColors
    }
    /// Speakers 3 to 8 of the palette.
    let extraSpeakerColors: [Color]

    /// The colour of a speaker; any index wraps into the palette.
    func speakerColor(_ index: Int) -> Color {
        let palette = speakerPalette
        return palette[((index % palette.count) + palette.count) % palette.count]
    }

    /// A star on a transcript line and in the list of important moments.
    var highlightStar: Color { warning }

    // Metrics
    let panelCornerRadius: CGFloat = 16
    let rowCornerRadius: CGFloat = 8
    let buttonCornerRadius: CGFloat = 8
    let cardCornerRadius: CGFloat = 12

    // Type
    let pageTitleFont = Font.system(size: 26, weight: .semibold)
    let pageSubtitleFont = Font.system(size: 13)
    let sectionTitleFont = Font.system(size: 15, weight: .semibold)
    let headlineFont = Font.system(size: 13, weight: .semibold)
    let bodyFont = Font.system(size: 13)
    let bodyMediumFont = Font.system(size: 13, weight: .medium)
    let captionFont = Font.system(size: 11)
    let captionMediumFont = Font.system(size: 11, weight: .medium)
    let sectionLabelFont = Font.system(size: 11, weight: .semibold)
    let monoCaptionFont = Font.system(size: 11).monospacedDigit()
    /// Commands and configuration snippets meant to be copied.
    let codeFont = Font.system(size: 11.5, design: .monospaced)
    let bannerHeadlineFont = Font.system(size: 26, weight: .regular, design: .serif)
    let statNumberFont = Font.system(size: 34, weight: .regular, design: .serif)
    let largeNumberFont = Font.system(size: 44, weight: .regular, design: .serif)
    let styleSampleFont = Font.system(size: 15, weight: .regular, design: .serif)
    let logoFont = Font.system(size: 20, weight: .semibold, design: .serif)

    static let light = Theme(
        windowBackground: Color(hex: 0xF2F0EB),
        sidebarBackground: Color(hex: 0xF2F0EB),
        panelBackground: Color(hex: 0xFBFAF7),
        panelBorder: Color(hex: 0xE7E3DA),
        divider: Color(hex: 0xEEEAE3),
        fieldBackground: Color(hex: 0xFFFFFF),
        sidebarSelection: Color(hex: 0xE7E3DA),
        rowHover: Color(hex: 0xF5F2EC),
        insetBackground: Color(hex: 0xF4F1EB),
        shadow: Color(hex: 0x1C1B19, opacity: 0.10),
        textPrimary: Color(hex: 0x1C1B19),
        textSecondary: Color(hex: 0x6B675F),
        primaryButtonBackground: Color(hex: 0x161514),
        primaryButtonText: Color(hex: 0xFFFFFF),
        secondaryButtonBackground: Color(hex: 0xECE8E0),
        secondaryButtonText: Color(hex: 0x1C1B19),
        accent: Color(hex: 0x7C5CFA),
        accentText: Color(hex: 0x6A48F0),
        accentSoft: Color(hex: 0x7C5CFA, opacity: 0.12),
        keycapFill: Color(hex: 0xF4A259),
        keycapBorder: Color(hex: 0x1C1B19),
        keycapText: Color(hex: 0x1C1B19),
        bannerGradientStart: Color(hex: 0x1E1A17),
        bannerGradientEnd: Color(hex: 0x4A3A30),
        bannerText: Color(hex: 0xFBFAF7),
        bannerSecondaryText: Color(hex: 0xD8CFC4),
        bannerButtonBackground: Color(hex: 0xFBFAF7),
        bannerButtonText: Color(hex: 0x1C1B19),
        bannerChipBackground: Color(hex: 0xFFFFFF, opacity: 0.10),
        flowBarBackground: Color(hex: 0x161514),
        flowBarIdle: Color(hex: 0x8F8A81),
        flowBarForeground: Color(hex: 0xFFFFFF),
        flowBarSecondary: Color(hex: 0xFFFFFF, opacity: 0.68),
        flowBarButtonBackground: Color(hex: 0xFFFFFF, opacity: 0.14),
        flowBarBorder: Color(hex: 0xFFFFFF, opacity: 0.14),
        flowBarAccent: Color(hex: 0xA48BFF),
        flowBarWarning: Color(hex: 0xF4A259),
        success: Color(hex: 0x2E7D4F),
        successSoft: Color(hex: 0x2E7D4F, opacity: 0.12),
        warning: Color(hex: 0x9A5806),
        warningSoft: Color(hex: 0xF4A259, opacity: 0.20),
        destructive: Color(hex: 0xB42318),
        destructiveSoft: Color(hex: 0xB42318, opacity: 0.09),
        calendarEmpty: Color(hex: 0xECE8E0),
        calendarLevels: [Color(hex: 0xD9D0FD), Color(hex: 0xB5A4FC), Color(hex: 0x977DFB), Color(hex: 0x7050F0)],
        // Also speaker 2; dark enough for 4.5:1 behind a hovered or focused transcript row.
        secondaryAccent: Color(hex: 0x1B7081),
        toggleKnob: Color(hex: 0xFFFFFF),
        // Speaker labels sit on the panel, on `rowHover` and on `accentSoft` (the flash after
        // jumping to a moment), which is the darkest of the three: accent text 4.6:1, teal
        // 4.7:1, rust 4.5:1, green 4.8:1, magenta 4.9:1, blue 4.9:1, ochre 4.7:1, olive 4.6:1.
        extraSpeakerColors: [Color(hex: 0xB4461C), Color(hex: 0x2B7338), Color(hex: 0xB02F74),
                             Color(hex: 0x2F62B8), Color(hex: 0x8A5F00), Color(hex: 0x5F6F12)]
    )

    // Dark values keep WCAG AA for text on the panel: primary 13.9:1, secondary 6.1:1,
    // accent text 6.0:1, secondary accent 6.3:1, warning 7.9:1.
    static let dark = Theme(
        windowBackground: Color(hex: 0x1B1A18),
        sidebarBackground: Color(hex: 0x1B1A18),
        panelBackground: Color(hex: 0x232220),
        panelBorder: Color(hex: 0x34322E),
        divider: Color(hex: 0x2E2C29),
        fieldBackground: Color(hex: 0x1B1A18),
        sidebarSelection: Color(hex: 0x2F2D2A),
        rowHover: Color(hex: 0x2A2926),
        insetBackground: Color(hex: 0x1D1C1A),
        shadow: Color(hex: 0x000000, opacity: 0.40),
        textPrimary: Color(hex: 0xEEEAE3),
        textSecondary: Color(hex: 0xA39E94),
        primaryButtonBackground: Color(hex: 0xFFFFFF),
        primaryButtonText: Color(hex: 0x161514),
        secondaryButtonBackground: Color(hex: 0x34322E),
        secondaryButtonText: Color(hex: 0xEEEAE3),
        accent: Color(hex: 0x8F74FB),
        accentText: Color(hex: 0xAE9BFC),
        accentSoft: Color(hex: 0x8F74FB, opacity: 0.20),
        keycapFill: Color(hex: 0xF4A259),
        keycapBorder: Color(hex: 0x0F0E0D),
        keycapText: Color(hex: 0x1C1B19),
        bannerGradientStart: Color(hex: 0x1E1A17),
        bannerGradientEnd: Color(hex: 0x4A3A30),
        bannerText: Color(hex: 0xFBFAF7),
        bannerSecondaryText: Color(hex: 0xD8CFC4),
        bannerButtonBackground: Color(hex: 0xFBFAF7),
        bannerButtonText: Color(hex: 0x1C1B19),
        bannerChipBackground: Color(hex: 0xFFFFFF, opacity: 0.10),
        flowBarBackground: Color(hex: 0x0F0E0D),
        flowBarIdle: Color(hex: 0x77726A),
        flowBarForeground: Color(hex: 0xFFFFFF),
        flowBarSecondary: Color(hex: 0xFFFFFF, opacity: 0.68),
        flowBarButtonBackground: Color(hex: 0xFFFFFF, opacity: 0.16),
        flowBarBorder: Color(hex: 0xFFFFFF, opacity: 0.18),
        flowBarAccent: Color(hex: 0xA48BFF),
        flowBarWarning: Color(hex: 0xF4A259),
        success: Color(hex: 0x5CC48A),
        successSoft: Color(hex: 0x5CC48A, opacity: 0.16),
        warning: Color(hex: 0xE9A94B),
        warningSoft: Color(hex: 0xE9A94B, opacity: 0.14),
        destructive: Color(hex: 0xF97066),
        destructiveSoft: Color(hex: 0xF97066, opacity: 0.14),
        calendarEmpty: Color(hex: 0x2F2D2A),
        calendarLevels: [Color(hex: 0x3F3766), Color(hex: 0x5B4BA3), Color(hex: 0x7A62D8), Color(hex: 0xA48BFF)],
        secondaryAccent: Color(hex: 0x5EC4D6),
        toggleKnob: Color(hex: 0xF4F1EC),
        // On the dark panel: salmon 7.0:1, green 8.1:1, pink 6.9:1, blue 7.0:1,
        // ochre 8.3:1, olive 8.7:1.
        extraSpeakerColors: [Color(hex: 0xF2946B), Color(hex: 0x7CCB84), Color(hex: 0xF08BC0),
                             Color(hex: 0x86ACF7), Color(hex: 0xE2B455), Color(hex: 0xB9C86A)]
    )

    static func resolve(_ scheme: ColorScheme) -> Theme {
        scheme == .dark ? .dark : .light
    }
}

private struct ThemeKey: EnvironmentKey {
    static let defaultValue = Theme.light
}

extension EnvironmentValues {
    var theme: Theme {
        get { self[ThemeKey.self] }
        set { self[ThemeKey.self] = newValue }
    }
}

/// Injects the theme matching the current colour scheme; wrap every root view in it.
struct ThemedRoot<Content: View>: View {
    @Environment(\.colorScheme) private var colorScheme
    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        content.environment(\.theme, Theme.resolve(colorScheme))
    }
}

extension Color {
    /// Theme tokens are the only place that builds colours from hex values.
    fileprivate init(hex: UInt32, opacity: Double = 1) {
        self.init(.sRGB,
                  red: Double((hex >> 16) & 0xFF) / 255,
                  green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255,
                  opacity: opacity)
    }
}
