import AppKit
import SlovoCore
import SwiftUI

/// Black (white in dark mode) filled button.
struct PrimaryButtonStyle: ButtonStyle {
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(theme.bodyMediumFont)
            .labelStyle(ButtonLabelStyle())
            .foregroundStyle(theme.primaryButtonText)
            .padding(.horizontal, 14)
            .frame(height: 30)
            .background(theme.primaryButtonBackground, in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
            .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.4)
            .contentShape(Rectangle())
    }
}

struct SecondaryButtonStyle: ButtonStyle {
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var isEnabled
    var isDestructive = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(theme.bodyMediumFont)
            .labelStyle(ButtonLabelStyle())
            .foregroundStyle(isDestructive ? theme.destructive : theme.secondaryButtonText)
            .padding(.horizontal, 14)
            .frame(height: 30)
            .background(isDestructive ? theme.destructiveSoft : theme.secondaryButtonBackground,
                        in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
            .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.45)
            .contentShape(Rectangle())
    }
}

/// Icon and title close together, the icon a little smaller than the text.
private struct ButtonLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.font(.system(size: 12, weight: .medium))
            configuration.title
        }
    }
}

/// A small square icon button with a hover background, for row actions.
struct IconButton: View {
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let symbol: String
    let help: String
    var tint: Color?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(tint ?? theme.textSecondary)
                .frame(width: 26, height: 26)
                .background(isHovering ? theme.secondaryButtonBackground : Color.clear,
                            in: RoundedRectangle(cornerRadius: 6))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
        .help(help)
        .accessibilityLabel(help)
    }
}

/// The floating capsule of row actions that appears on hover.
struct HoverActionBar<Content: View>: View {
    @Environment(\.theme) private var theme
    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        HStack(spacing: 0) {
            content
        }
        .padding(2)
        .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(theme.panelBorder, lineWidth: 1))
        .shadow(color: theme.shadow, radius: 6, y: 2)
    }
}

/// The orange keycap that shows the dictation key.
struct KeycapBadge: View {
    @Environment(\.theme) private var theme
    let symbol: String
    var size: CGFloat = 15

    var body: some View {
        Text(symbol)
            .font(.system(size: size, weight: .semibold, design: .rounded))
            .foregroundStyle(theme.keycapText)
            .padding(.horizontal, size * 0.6)
            .padding(.vertical, size * 0.2)
            .frame(minWidth: size * 1.9)
            .background(theme.keycapFill, in: RoundedRectangle(cornerRadius: size * 0.4))
            .overlay(RoundedRectangle(cornerRadius: size * 0.4).strokeBorder(theme.keycapBorder, lineWidth: 1))
            .fixedSize()
    }
}

/// A quiet keycap for shortcut lists.
struct ShortcutKeys: View {
    @Environment(\.theme) private var theme
    let keys: [String]

    var body: some View {
        HStack(spacing: 4) {
            ForEach(Array(keys.enumerated()), id: \.offset) { _, key in
                Text(key)
                    .font(.system(size: 12, weight: .medium, design: .rounded))
                    .foregroundStyle(theme.textPrimary)
                    .padding(.horizontal, 7)
                    .frame(minWidth: 24, minHeight: 22)
                    .background(theme.fieldBackground, in: RoundedRectangle(cornerRadius: 5))
                    .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(theme.panelBorder, lineWidth: 1))
            }
        }
        .fixedSize()
    }
}

/// Page title at the top of a screen, with an optional subtitle and trailing controls.
struct PageHeader<Trailing: View>: View {
    @Environment(\.theme) private var theme
    let title: String
    let subtitle: String?
    let trailing: Trailing

    init(_ title: String, subtitle: String? = nil, @ViewBuilder trailing: () -> Trailing) {
        self.title = title
        self.subtitle = subtitle
        self.trailing = trailing()
    }

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(theme.pageTitleFont)
                    .foregroundStyle(theme.textPrimary)
                if let subtitle {
                    Text(subtitle)
                        .font(theme.pageSubtitleFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            trailing
        }
    }
}

extension PageHeader where Trailing == EmptyView {
    init(_ title: String, subtitle: String? = nil) {
        self.init(title, subtitle: subtitle) { EmptyView() }
    }
}

/// Dark warm gradient card with a serif headline, as on Home, Dictionary and Snippets.
struct BannerCard<Accessory: View>: View {
    @Environment(\.theme) private var theme
    @EnvironmentObject private var loc: Localizer
    let headline: String
    let message: String
    let buttonTitle: String?
    var action: () -> Void = {}
    var onDismiss: (() -> Void)?
    let accessory: Accessory

    init(headline: String, message: String, buttonTitle: String?, action: @escaping () -> Void = {},
         onDismiss: (() -> Void)? = nil, @ViewBuilder accessory: () -> Accessory) {
        self.headline = headline
        self.message = message
        self.buttonTitle = buttonTitle
        self.action = action
        self.onDismiss = onDismiss
        self.accessory = accessory()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top) {
                Text(headline)
                    .font(theme.bannerHeadlineFont)
                    .foregroundStyle(theme.bannerText)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 16)
                if let onDismiss {
                    Button(action: onDismiss) {
                        Image(systemName: "xmark")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(theme.bannerSecondaryText)
                            .frame(width: 22, height: 22)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(loc.t("banner.dismiss"))
                    .accessibilityLabel(loc.t("banner.dismiss"))
                }
            }
            Text(message)
                .font(theme.bodyFont)
                .foregroundStyle(theme.bannerSecondaryText)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 560, alignment: .leading)
            accessory
            if let buttonTitle {
                Button(buttonTitle, action: action)
                    .buttonStyle(BannerButtonStyle())
                    .padding(.top, 4)
            }
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 22)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            LinearGradient(colors: [theme.bannerGradientStart, theme.bannerGradientEnd],
                           startPoint: .leading, endPoint: .trailing),
            in: RoundedRectangle(cornerRadius: theme.cardCornerRadius)
        )
        // On the dark panel the gradient's dark end would dissolve into the ground.
        .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }
}

extension BannerCard where Accessory == EmptyView {
    init(headline: String, message: String, buttonTitle: String?, action: @escaping () -> Void = {},
         onDismiss: (() -> Void)? = nil) {
        self.init(headline: headline, message: message, buttonTitle: buttonTitle, action: action,
                  onDismiss: onDismiss) { EmptyView() }
    }
}

/// A translucent example chip on a banner: «from» → to.
struct BannerExample: View {
    @Environment(\.theme) private var theme
    let from: String
    let to: String

    var body: some View {
        HStack(spacing: 6) {
            Text(from)
                .foregroundStyle(theme.bannerSecondaryText)
            Image(systemName: "arrow.right")
                .font(.system(size: 9, weight: .semibold))
                .foregroundStyle(theme.bannerSecondaryText)
            Text(to)
                .foregroundStyle(theme.bannerText)
                .lineLimit(1)
        }
        .font(theme.captionMediumFont)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(theme.bannerChipBackground, in: Capsule())
        .fixedSize()
    }
}

private struct BannerButtonStyle: ButtonStyle {
    @Environment(\.theme) private var theme

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(theme.bodyMediumFont)
            .foregroundStyle(theme.bannerButtonText)
            .padding(.horizontal, 14)
            .frame(height: 30)
            .background(theme.bannerButtonBackground, in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
            .opacity(configuration.isPressed ? 0.85 : 1)
    }
}

/// A bordered card on the content panel.
struct Card<Content: View>: View {
    @Environment(\.theme) private var theme
    var padding: CGFloat = 18
    let content: Content

    init(padding: CGFloat = 18, @ViewBuilder content: () -> Content) {
        self.padding = padding
        self.content = content()
    }

    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }
}

/// Small uppercase label above a group, such as TODAY in the history list.
struct SectionLabel: View {
    @Environment(\.theme) private var theme
    let text: String

    var body: some View {
        Text(text.uppercased(with: nil))
            .font(theme.sectionLabelFont)
            .tracking(0.6)
            .foregroundStyle(theme.textSecondary)
    }
}

/// A horizontal 1 pt hairline.
struct RowDivider: View {
    @Environment(\.theme) private var theme

    var body: some View {
        Rectangle().fill(theme.divider).frame(height: 1)
    }
}

/// A row of selectable pills; an optional count follows each title.
struct ChoiceChips<Value: Hashable>: View {
    @Environment(\.theme) private var theme
    let options: [(value: Value, title: String)]
    @Binding var selection: Value
    var counts: [Value: Int] = [:]

    var body: some View {
        HStack(spacing: 6) {
            ForEach(options, id: \.value) { option in
                let isSelected = option.value == selection
                Button {
                    selection = option.value
                } label: {
                    HStack(spacing: 5) {
                        Text(option.title)
                            .font(.system(size: 12, weight: isSelected ? .semibold : .medium))
                        if let count = counts[option.value] {
                            Text("\(count)")
                                .font(.system(size: 11, weight: .medium).monospacedDigit())
                                .opacity(0.65)
                        }
                    }
                    .foregroundStyle(isSelected ? theme.primaryButtonText : theme.secondaryButtonText)
                    .padding(.horizontal, 12)
                    .frame(height: 28)
                    .background(isSelected ? theme.primaryButtonBackground : theme.secondaryButtonBackground,
                                in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .fixedSize()
                .accessibilityAddTraits(isSelected ? .isSelected : [])
            }
        }
    }
}

/// Status pill with a dot: Ready, Downloading, Allowed and so on.
struct StatusBadge: View {
    enum Tone {
        case success, warning, destructive, accent, neutral
    }

    @Environment(\.theme) private var theme
    let text: String
    let tone: Tone
    var showsDot = true

    private var colors: (foreground: Color, background: Color) {
        switch tone {
        case .success: return (theme.success, theme.successSoft)
        case .warning: return (theme.warning, theme.warningSoft)
        case .destructive: return (theme.destructive, theme.destructiveSoft)
        case .accent: return (theme.accentText, theme.accentSoft)
        case .neutral: return (theme.textSecondary, theme.secondaryButtonBackground)
        }
    }

    var body: some View {
        HStack(spacing: 5) {
            if showsDot {
                Circle().fill(colors.foreground).frame(width: 6, height: 6)
            }
            Text(text)
                .lineLimit(1)
        }
        .font(theme.captionMediumFont)
        .foregroundStyle(colors.foreground)
        .padding(.horizontal, 8)
        .frame(height: 20)
        .background(colors.background, in: Capsule())
        .fixedSize()
    }
}

/// An inline warning or note with an optional action button.
struct InlineNotice: View {
    @Environment(\.theme) private var theme
    let symbol: String
    let text: String
    var tone: StatusBadge.Tone = .warning
    var actionTitle: String?
    var action: () -> Void = {}

    private var colors: (foreground: Color, background: Color) {
        switch tone {
        case .success: return (theme.success, theme.successSoft)
        case .warning: return (theme.warning, theme.warningSoft)
        case .destructive: return (theme.destructive, theme.destructiveSoft)
        case .accent: return (theme.accentText, theme.accentSoft)
        case .neutral: return (theme.textSecondary, theme.insetBackground)
        }
    }

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(colors.foreground)
            Text(text)
                .font(theme.captionFont)
                .foregroundStyle(theme.textPrimary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            if let actionTitle {
                Button(actionTitle, action: action)
                    .buttonStyle(SecondaryButtonStyle())
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(colors.background, in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
    }
}

/// A labelled settings row: title and explanation on the left, control on the right.
struct SettingsRow<Control: View>: View {
    @Environment(\.theme) private var theme
    let title: String
    let detail: String?
    let control: Control

    init(_ title: String, detail: String? = nil, @ViewBuilder control: () -> Control) {
        self.title = title
        self.detail = detail
        self.control = control()
    }

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)
                if let detail {
                    Text(detail)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            control
                .environment(\.controlLabel, title)
        }
        .padding(.vertical, 11)
    }
}

/// A titled card of settings rows.
struct SettingsGroup<Content: View>: View {
    @Environment(\.theme) private var theme
    let title: String?
    let content: Content

    init(_ title: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let title {
                SectionLabel(text: title)
                    .padding(.leading, 4)
            }
            VStack(alignment: .leading, spacing: 0) {
                content
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 2)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
        }
    }
}

/// The app's icon when installed, else a letter tile. Screenshots always use the
/// tile: workspace icons wash out the whole `ImageRenderer` image, and tiles keep
/// renders the same on every Mac.
struct AppIconView: View {
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    let bundleID: String?
    let name: String
    var size: CGFloat = 24

    private var icon: NSImage? {
        guard !isSnapshot, let bundleID,
              let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else {
            return nil
        }
        return NSWorkspace.shared.icon(forFile: url.path)
    }

    var body: some View {
        if let icon {
            Image(nsImage: icon)
                .resizable()
                .interpolation(.high)
                .frame(width: size, height: size)
        } else {
            Text(name.first.map { String($0).uppercased() } ?? "?")
                .font(.system(size: size * 0.5, weight: .semibold, design: .rounded))
                .foregroundStyle(theme.textSecondary)
                .frame(width: size, height: size)
                .background(theme.secondaryButtonBackground, in: RoundedRectangle(cornerRadius: size * 0.24))
        }
    }
}

/// Slovo's mark: a waveform on the banner gradient.
struct AppLogo: View {
    @Environment(\.theme) private var theme
    var size: CGFloat = 56

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.26)
            .fill(LinearGradient(colors: [theme.bannerGradientStart, theme.bannerGradientEnd],
                                 startPoint: .topLeading, endPoint: .bottomTrailing))
            // The dark banner gradient nearly matches the dark window; the hairline keeps the shape.
            .overlay(RoundedRectangle(cornerRadius: size * 0.26).strokeBorder(theme.panelBorder, lineWidth: 1))
            .frame(width: size, height: size)
            .overlay {
                Image(systemName: "waveform")
                    .font(.system(size: size * 0.46, weight: .medium))
                    .foregroundStyle(theme.keycapFill)
            }
    }
}

/// A segmented level meter for meeting audio.
struct LevelMeter: View {
    @Environment(\.theme) private var theme
    let level: Float
    let tint: Color
    var segments = 24

    var body: some View {
        let lit = Int((Double(min(max(level, 0), 1)) * Double(segments)).rounded())
        HStack(spacing: 3) {
            ForEach(0..<segments, id: \.self) { index in
                RoundedRectangle(cornerRadius: 1.5)
                    .fill(index < lit ? tint : theme.calendarEmpty)
                    .frame(maxWidth: .infinity)
            }
        }
        .frame(height: 10)
    }
}
