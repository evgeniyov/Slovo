import AppKit
import SlovoCore
import SwiftUI

/// True while `ScreenRenderer` draws a view with `ImageRenderer`, which cannot draw
/// AppKit-backed controls (scroll views, text fields, switches, menus). Controls
/// below swap in a static look in that mode.
private struct SnapshotModeKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var isSnapshot: Bool {
        get { self[SnapshotModeKey.self] }
        set { self[SnapshotModeKey.self] = newValue }
    }
}

/// The title of the settings row a control sits in, so switches and pop-ups that show
/// no text of their own still tell VoiceOver what they change.
private struct ControlLabelKey: EnvironmentKey {
    static let defaultValue: String? = nil
}

extension EnvironmentValues {
    var controlLabel: String? {
        get { self[ControlLabelKey.self] }
        set { self[ControlLabelKey.self] = newValue }
    }
}

/// Injects the model, localizer, locale, theme and settings store every root view needs.
struct AppRoot<Content: View>: View {
    let model: AppModel
    @ObservedObject private var localizer: Localizer
    private let content: Content

    init(model: AppModel, @ViewBuilder content: () -> Content) {
        self.model = model
        self.localizer = model.localizer
        self.content = content()
    }

    var body: some View {
        ThemedRoot {
            content
        }
        .environmentObject(model)
        .environmentObject(localizer)
        .environment(\.locale, localizer.locale)
        .defaultAppStorage(model.settings.defaults)
    }
}

/// A vertical scroll view in the window, a plain clipped stack in snapshots.
struct PanelScroll<Content: View>: View {
    @Environment(\.isSnapshot) private var isSnapshot
    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        if isSnapshot {
            // A fixed frame of the offered size keeps overflow at the top edge, as a
            // scroll view at rest would; a flexible frame would grow and centre it.
            GeometryReader { proxy in
                VStack(alignment: .leading, spacing: 0) {
                    content
                    Spacer(minLength: 0)
                }
                .frame(width: proxy.size.width, height: proxy.size.height, alignment: .topLeading)
            }
            .clipped()
        } else {
            ScrollView {
                content.frame(maxWidth: .infinity, alignment: .leading)
            }
            .scrollIndicators(.automatic)
        }
    }
}

/// A rounded search field.
struct SearchField: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    let placeholder: String
    @Binding var text: String

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12))
                .foregroundStyle(theme.textSecondary)
            if isSnapshot {
                Text(text.isEmpty ? placeholder : text)
                    .foregroundStyle(text.isEmpty ? theme.textSecondary : theme.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 0)
            } else {
                TextField(placeholder, text: $text)
                    .textFieldStyle(.plain)
                    .foregroundStyle(theme.textPrimary)
                if !text.isEmpty {
                    Button {
                        text = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(theme.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .help(loc.t("common.clearSearch"))
                    .accessibilityLabel(loc.t("common.clearSearch"))
                }
            }
        }
        .font(theme.bodyFont)
        .padding(.horizontal, 10)
        .frame(height: 30)
        .background(theme.fieldBackground, in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.buttonCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }
}

/// A single-line text input.
struct ThemedTextField: View {
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    let placeholder: String
    @Binding var text: String
    var onSubmit: () -> Void = {}

    var body: some View {
        Group {
            if isSnapshot {
                Text(text.isEmpty ? placeholder : text)
                    .foregroundStyle(text.isEmpty ? theme.textSecondary : theme.textPrimary)
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                TextField(placeholder, text: $text)
                    .textFieldStyle(.plain)
                    .foregroundStyle(theme.textPrimary)
                    .onSubmit(onSubmit)
            }
        }
        .font(theme.bodyFont)
        .padding(.horizontal, 10)
        .frame(height: 32)
        .background(theme.fieldBackground, in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
        .overlay(RoundedRectangle(cornerRadius: theme.buttonCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }
}

/// A multi-line text input without its own border, for editors placed inside cards.
struct ThemedTextEditor: View {
    @Environment(\.theme) private var theme
    @Environment(\.isSnapshot) private var isSnapshot
    let placeholder: String
    @Binding var text: String
    var font: Font = .system(size: 14)

    var body: some View {
        ZStack(alignment: .topLeading) {
            if isSnapshot {
                Text(text.isEmpty ? placeholder : text)
                    .foregroundStyle(text.isEmpty ? theme.textSecondary : theme.textPrimary)
                    .lineSpacing(3)
                    .padding(.leading, 5)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                TextEditor(text: $text)
                    .scrollContentBackground(.hidden)
                    .foregroundStyle(theme.textPrimary)
                    .lineSpacing(3)
                    .accessibilityLabel(placeholder)
                if text.isEmpty {
                    Text(placeholder)
                        .foregroundStyle(theme.textSecondary)
                        .padding(.leading, 5)
                        .allowsHitTesting(false)
                }
            }
        }
        .font(font)
    }
}

/// A bordered multi-line field for sheets.
struct ThemedTextArea: View {
    @Environment(\.theme) private var theme
    let placeholder: String
    @Binding var text: String

    var body: some View {
        ThemedTextEditor(placeholder: placeholder, text: $text, font: theme.bodyFont)
            .padding(.horizontal, 6)
            .padding(.vertical, 8)
            .background(theme.fieldBackground, in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.buttonCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
    }
}

/// An accent-coloured switch drawn with shapes, so it looks the same everywhere.
struct ThemedToggle: View {
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.controlLabel) private var rowLabel
    @Binding var isOn: Bool
    /// What the switch turns on, for VoiceOver; inside a settings row the row title.
    private let label: String?

    init(isOn: Binding<Bool>, label: String? = nil) {
        _isOn = isOn
        self.label = label
    }

    var body: some View {
        Button {
            withAnimation(reduceMotion ? nil : .spring(response: 0.22, dampingFraction: 0.85)) {
                isOn.toggle()
            }
        } label: {
            Capsule()
                .fill(isOn ? theme.accent : theme.secondaryButtonBackground)
                .frame(width: 34, height: 20)
                .overlay(alignment: isOn ? .trailing : .leading) {
                    Circle()
                        .fill(theme.toggleKnob)
                        .shadow(color: theme.shadow, radius: 1, y: 0.5)
                        .padding(2)
                }
                .opacity(isEnabled ? 1 : 0.5)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text(label ?? rowLabel ?? ""))
        .accessibilityValue(isOn ? Text("1") : Text("0"))
        .accessibilityAddTraits(.isToggle)
    }
}

/// One item of a pop-up menu shown with `MenuPresenter`.
struct MenuEntry {
    enum Kind {
        case action(title: String, isChecked: Bool, isEnabled: Bool, handler: @MainActor () -> Void)
        case header(String)
        case separator
    }

    let kind: Kind

    static func action(_ title: String, isChecked: Bool = false, isEnabled: Bool = true,
                       handler: @escaping @MainActor () -> Void) -> MenuEntry {
        MenuEntry(kind: .action(title: title, isChecked: isChecked, isEnabled: isEnabled, handler: handler))
    }

    static func header(_ title: String) -> MenuEntry {
        MenuEntry(kind: .header(title))
    }

    static let separator = MenuEntry(kind: .separator)
}

/// Shows a native `NSMenu` at the pointer. Used instead of SwiftUI `Menu` and `Picker`,
/// whose labels ignore custom styling and which `ImageRenderer` cannot draw.
@MainActor
enum MenuPresenter {
    static func show(_ entries: [MenuEntry]) {
        let menu = NSMenu()
        menu.autoenablesItems = false
        var checkedItem: NSMenuItem?
        for entry in entries {
            switch entry.kind {
            case let .action(title, isChecked, isEnabled, handler):
                let item = ClosureMenuItem(title: title, handler: handler)
                item.state = isChecked ? .on : .off
                item.isEnabled = isEnabled
                menu.addItem(item)
                if isChecked {
                    checkedItem = item
                }
            case let .header(title):
                let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                item.isEnabled = false
                menu.addItem(item)
            case .separator:
                menu.addItem(.separator())
            }
        }
        // Like a native pop-up button, the checked item opens under the pointer.
        menu.popUp(positioning: checkedItem, at: NSEvent.mouseLocation, in: nil)
    }
}

final class ClosureMenuItem: NSMenuItem {
    private let handler: @MainActor () -> Void

    init(title: String, handler: @escaping @MainActor () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(invoke), keyEquivalent: "")
        target = self
    }

    required init(coder: NSCoder) {
        handler = {}
        super.init(coder: coder)
    }

    @objc private func invoke() {
        MainActor.assumeIsolated {
            handler()
        }
    }
}

/// A button that opens a menu; the label is any view, drawn the same in snapshots.
struct MenuButton<Label: View>: View {
    let entries: () -> [MenuEntry]
    private let label: Label

    init(entries: @escaping () -> [MenuEntry], @ViewBuilder label: () -> Label) {
        self.entries = entries
        self.label = label()
    }

    var body: some View {
        Button {
            MenuPresenter.show(entries())
        } label: {
            label.contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// A pop-up choice drawn as a quiet button with the current value; the options open
/// in a native menu.
struct PopupChoice<Value: Hashable>: View {
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.controlLabel) private var rowLabel
    let options: [(value: Value, title: String)]
    @Binding var selection: Value

    private var currentTitle: String {
        options.first { $0.value == selection }?.title ?? ""
    }

    var body: some View {
        MenuButton(entries: {
            options.map { option in
                MenuEntry.action(option.title, isChecked: option.value == selection) {
                    selection = option.value
                }
            }
        }) {
            HStack(spacing: 6) {
                Text(currentTitle)
                    .lineLimit(1)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(theme.textSecondary)
            }
            .font(theme.bodyFont)
            .foregroundStyle(theme.secondaryButtonText)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(theme.secondaryButtonBackground, in: RoundedRectangle(cornerRadius: theme.buttonCornerRadius))
            .opacity(isEnabled ? 1 : 0.5)
        }
        .fixedSize()
        // The button shows only the value; VoiceOver hears the row it belongs to as well.
        .accessibilityLabel(Text(rowLabel ?? currentTitle))
        .accessibilityValue(Text(rowLabel == nil ? "" : currentTitle))
    }
}

/// A thin determinate progress bar.
struct ThemedProgressBar: View {
    @Environment(\.theme) private var theme
    let fraction: Double
    var height: CGFloat = 6
    /// The accent, unless the bar belongs to something that carries a colour of its own,
    /// such as a speaker.
    var tint: Color?

    var body: some View {
        GeometryReader { proxy in
            let clamped = min(max(fraction, 0), 1)
            ZStack(alignment: .leading) {
                Capsule().fill(theme.calendarEmpty)
                if clamped > 0 {
                    Capsule()
                        .fill(tint ?? theme.accent)
                        .frame(width: max(height, proxy.size.width * clamped))
                }
            }
        }
        .frame(height: height)
    }
}

/// An empty-state card with an icon, a title, a hint and an optional action.
struct EmptyStateCard: View {
    @Environment(\.theme) private var theme
    let symbol: String
    let title: String
    let message: String
    var actionTitle: String?
    var action: () -> Void = {}

    var body: some View {
        Card {
            VStack(spacing: 8) {
                Image(systemName: symbol)
                    .font(.system(size: 26, weight: .light))
                    .foregroundStyle(theme.textSecondary)
                    .padding(.bottom, 2)
                Text(title)
                    .font(theme.sectionTitleFont)
                    .foregroundStyle(theme.textPrimary)
                Text(message)
                    .font(theme.bodyFont)
                    .foregroundStyle(theme.textSecondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: 420)
                if let actionTitle {
                    Button(actionTitle, action: action)
                        .buttonStyle(SecondaryButtonStyle())
                        .padding(.top, 6)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 26)
        }
    }
}

extension DictationKey {
    /// Localization key of the key's full name.
    var nameKey: String {
        switch self {
        case .fn: return "key.fn"
        case .rightOption: return "key.rightOption"
        case .rightCommand: return "key.rightCommand"
        case .leftControlOption: return "key.leftControlOption"
        }
    }
}

extension AppCategory {
    var titleKey: String { "category.\(rawValue)" }

    var symbol: String {
        switch self {
        case .personal: return "bubble.left.and.bubble.right"
        case .work: return "briefcase"
        case .email: return "envelope"
        case .other: return "square.grid.2x2"
        }
    }
}

extension StylePreset {
    var titleKey: String { "style.preset.\(rawValue)" }
    var detailKey: String { "style.preset.\(rawValue).detail" }
}

extension HotkeyShortcut {
    /// Symbols such as ⌃⌘V, in the order macOS menus use.
    var symbols: String {
        var text = ""
        if modifiers.contains(.control) { text += "⌃" }
        if modifiers.contains(.option) { text += "⌥" }
        if modifiers.contains(.shift) { text += "⇧" }
        if modifiers.contains(.command) { text += "⌘" }
        return text + Self.keyName(for: keyCode)
    }

    /// Choices offered for "Paste last transcript"; all use keys that keep their
    /// position across keyboard layouts.
    static let pasteLastChoices: [HotkeyShortcut] = [
        .pasteLastDefault,
        HotkeyShortcut(keyCode: 9, modifiers: [.option, .command]),
        HotkeyShortcut(keyCode: 9, modifiers: [.shift, .command]),
        HotkeyShortcut(keyCode: 9, modifiers: [.control, .option])
    ]

    private static func keyName(for keyCode: UInt32) -> String {
        // ANSI key codes of the letters the choices use; others show their number.
        let names: [UInt32: String] = [9: "V", 11: "B", 35: "P", 45: "N", 46: "M", 16: "Y", 32: "U"]
        return names[keyCode] ?? "#\(keyCode)"
    }
}
