import SlovoCore
import SwiftUI

/// Help → Keyboard shortcuts: a cheat sheet that follows the chosen keys.
struct ShortcutsSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @AppStorage(SettingsKey.dictationKey) private var keyRaw = DictationKey.fn.rawValue

    var body: some View {
        let key = (DictationKey(rawValue: keyRaw) ?? .fn).symbol
        let rows: [(title: String, detail: String, keys: [String])] = [
            (loc.t("shortcuts.hold"), loc.t("shortcuts.hold.detail"), [key]),
            (loc.t("shortcuts.handsFree"), loc.t("shortcuts.handsFree.detail"), [key, key]),
            (loc.t("shortcuts.finish"), loc.t("shortcuts.finish.detail"), [key]),
            (loc.t("shortcuts.cancel"), loc.t("shortcuts.cancel.detail"), ["esc"]),
            (loc.t("shortcuts.pasteLast"), loc.t("shortcuts.pasteLast.detail"), [model.settings.pasteLastShortcut.symbols]),
            (loc.t("shortcuts.markMoment"), loc.t("shortcuts.markMoment.detail"), [model.settings.highlightShortcut.symbols]),
            (loc.t("shortcuts.meetingMicrophone"), loc.t("shortcuts.meetingMicrophone.detail"),
             [model.settings.microphoneShortcut.symbols]),
            (loc.t("shortcuts.settings"), loc.t("shortcuts.settings.detail"), ["⌘,"])
        ]
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text(loc.t("help.shortcuts"))
                    .font(theme.sectionTitleFont)
                    .foregroundStyle(theme.textPrimary)
                Spacer()
                IconButton(symbol: "xmark", help: loc.t("common.close")) {
                    model.isShortcutsPresented = false
                    dismiss()
                }
            }
            VStack(spacing: 0) {
                ForEach(Array(rows.enumerated()), id: \.offset) { index, row in
                    if index > 0 {
                        RowDivider()
                    }
                    SettingsRow(row.title, detail: row.detail) {
                        ShortcutKeys(keys: row.keys)
                    }
                }
            }
            .padding(.horizontal, 16)
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius).strokeBorder(theme.panelBorder, lineWidth: 1))
        }
        .padding(24)
        .frame(width: 500)
        .background(theme.windowBackground)
    }
}
