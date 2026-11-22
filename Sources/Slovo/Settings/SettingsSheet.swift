import SlovoCore
import SwiftUI

/// Settings as a modal sheet over the main window: sections on the left, rows on the right.
struct SettingsSheet: View {
    static let size = CGSize(width: 800, height: 600)

    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 2) {
                Text(loc.t("settings.title"))
                    .font(theme.sectionTitleFont)
                    .foregroundStyle(theme.textPrimary)
                    .padding(.horizontal, 10)
                    .padding(.top, 22)
                    .padding(.bottom, 14)
                ForEach(SettingsSection.allCases) { section in
                    SectionRow(section: section, isSelected: model.settingsSection == section) {
                        model.settingsSection = section
                    }
                }
                Spacer()
            }
            .padding(.horizontal, 10)
            .frame(width: 214)
            .background(theme.windowBackground)

            Rectangle().fill(theme.panelBorder).frame(width: 1)

            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .center) {
                    Text(loc.t(model.settingsSection.titleKey))
                        .font(theme.pageTitleFont)
                        .foregroundStyle(theme.textPrimary)
                    Spacer()
                    IconButton(symbol: "xmark", help: loc.t("common.close")) {
                        model.isSettingsPresented = false
                        dismiss()
                    }
                }
                .padding(.horizontal, 28)
                .padding(.top, 20)
                .padding(.bottom, 14)

                PanelScroll {
                    SettingsSectionView(section: model.settingsSection)
                        .padding(.horizontal, 28)
                        .padding(.top, 6)
                        .padding(.bottom, 28)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(theme.insetBackground)
        }
        .frame(width: Self.size.width, height: Self.size.height)
        // Errors of actions taken here: an alert on the window behind would wait until
        // the sheet closes.
        .alert(model.settingsAlert?.title ?? "",
               isPresented: Binding(get: { model.settingsAlert != nil },
                                    set: { if !$0 { model.settingsAlert = nil } }),
               presenting: model.settingsAlert) { _ in
            Button(loc.t("common.ok")) {}
        } message: { alert in
            Text(alert.message)
        }
    }
}

private struct SectionRow: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isHovering = false
    let section: SettingsSection
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: section.symbol)
                    .font(.system(size: 13))
                    .frame(width: 18)
                Text(loc.t(section.titleKey))
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .font(.system(size: 13, weight: isSelected ? .semibold : .regular))
            .foregroundStyle(theme.textPrimary)
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .frame(minHeight: 32)
            .background(isSelected ? theme.sidebarSelection : (isHovering ? theme.rowHover : Color.clear),
                        in: RoundedRectangle(cornerRadius: theme.rowCornerRadius))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}
