import SlovoCore
import SwiftUI

struct StyleScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme

    var body: some View {
        PanelScroll {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(loc.t("sidebar.style"), subtitle: loc.t("style.subtitle"))

                ChoiceChips(options: AppCategory.allCases.map { (StyleTab.category($0), loc.t($0.titleKey)) }
                                + [(StyleTab.cleanup, loc.t("style.tab.cleanup"))],
                            selection: $model.styleTab)

                switch model.styleTab {
                case let .category(category):
                    StyleCategoryView(category: category)
                        .id(category)
                case .cleanup:
                    AutoCleanupView()
                }
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 28)
        }
    }
}

private struct StyleCategoryView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage private var styleRaw: String
    let category: AppCategory

    init(category: AppCategory) {
        self.category = category
        _styleRaw = AppStorage(wrappedValue: category.defaultStyle.rawValue, SettingsKey.style(for: category))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top, spacing: 12) {
                ForEach(StylePreset.allCases) { preset in
                    StyleCard(category: category, preset: preset, isSelected: styleRaw == preset.rawValue) {
                        styleRaw = preset.rawValue
                    }
                }
            }
            .fixedSize(horizontal: false, vertical: true)

            AppsInCategoryCard(category: category)
        }
    }
}

private struct StyleCard: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let category: AppCategory
    let preset: StylePreset
    let isSelected: Bool
    let action: () -> Void

    /// Literal keys per sample so the localization check sees every one.
    private var sample: String {
        switch (category, preset) {
        case (.personal, .formal): return loc.t("style.sample.personal.formal")
        case (.personal, .casual): return loc.t("style.sample.personal.casual")
        case (.personal, .veryCasual): return loc.t("style.sample.personal.veryCasual")
        case (.work, .formal): return loc.t("style.sample.work.formal")
        case (.work, .casual): return loc.t("style.sample.work.casual")
        case (.work, .veryCasual): return loc.t("style.sample.work.veryCasual")
        case (.email, .formal): return loc.t("style.sample.email.formal")
        case (.email, .casual): return loc.t("style.sample.email.casual")
        case (.email, .veryCasual): return loc.t("style.sample.email.veryCasual")
        case (.other, .formal): return loc.t("style.sample.other.formal")
        case (.other, .casual): return loc.t("style.sample.other.casual")
        case (.other, .veryCasual): return loc.t("style.sample.other.veryCasual")
        }
    }

    private var title: String {
        switch preset {
        case .formal: return loc.t("style.preset.formal")
        case .casual: return loc.t("style.preset.casual")
        case .veryCasual: return loc.t("style.preset.veryCasual")
        }
    }

    private var detail: String {
        switch preset {
        case .formal: return loc.t("style.preset.formal.detail")
        case .casual: return loc.t("style.preset.casual.detail")
        case .veryCasual: return loc.t("style.preset.veryCasual.detail")
        }
    }

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(title)
                            .font(theme.sectionTitleFont)
                            .foregroundStyle(theme.textPrimary)
                        Text(detail)
                            .font(theme.captionFont)
                            .foregroundStyle(theme.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 6)
                    Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 17))
                        .foregroundStyle(isSelected ? theme.accent : theme.panelBorder)
                }
                Text(sample)
                    .font(theme.styleSampleFont)
                    .foregroundStyle(theme.textPrimary)
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(12)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                    .background(theme.insetBackground, in: RoundedRectangle(cornerRadius: 10))
            }
            .padding(16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(isSelected ? theme.accentSoft.opacity(0.35) : Color.clear,
                        in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .background(theme.panelBackground, in: RoundedRectangle(cornerRadius: theme.cardCornerRadius))
            .overlay(RoundedRectangle(cornerRadius: theme.cardCornerRadius)
                .strokeBorder(isSelected ? theme.accent : theme.panelBorder, lineWidth: isSelected ? 2 : 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

private struct AppsInCategoryCard: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let category: AppCategory

    var body: some View {
        let apps = model.apps(in: category)
        Card {
            VStack(alignment: .leading, spacing: 4) {
                Text(loc.t("style.apps.title"))
                    .font(theme.headlineFont)
                    .foregroundStyle(theme.textPrimary)
                Text(loc.t("style.apps.detail"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .padding(.bottom, 8)
                if apps.isEmpty {
                    Text(loc.t("style.apps.empty"))
                        .font(theme.bodyFont)
                        .foregroundStyle(theme.textSecondary)
                        .padding(.vertical, 8)
                } else {
                    ForEach(Array(apps.enumerated()), id: \.element.bundleID) { index, app in
                        if index > 0 {
                            RowDivider()
                        }
                        HStack(spacing: 10) {
                            AppIconView(bundleID: app.bundleID, name: app.name, size: 24)
                            Text(app.name)
                                .font(theme.bodyFont)
                                .foregroundStyle(theme.textPrimary)
                            Spacer()
                            MenuButton(entries: {
                                [MenuEntry.header(loc.t("style.apps.moveTo"))] + AppCategory.allCases.map { target in
                                    MenuEntry.action(loc.t(target.titleKey), isChecked: target == category) {
                                        if target != category {
                                            model.categories.setCategory(target, for: app)
                                        }
                                    }
                                }
                            }) {
                                HStack(spacing: 6) {
                                    Text(loc.t("style.apps.move"))
                                    Image(systemName: "chevron.up.chevron.down")
                                        .font(.system(size: 9, weight: .semibold))
                                        .foregroundStyle(theme.textSecondary)
                                }
                                .font(theme.captionMediumFont)
                                .foregroundStyle(theme.secondaryButtonText)
                                .padding(.horizontal, 10)
                                .frame(height: 26)
                                .background(theme.secondaryButtonBackground, in: RoundedRectangle(cornerRadius: 7))
                            }
                            .fixedSize()
                        }
                        .padding(.vertical, 8)
                    }
                }
            }
        }
    }
}

private struct AutoCleanupView: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @AppStorage(SettingsKey.removeFillers) private var removeFillers = true
    @AppStorage(SettingsKey.removeRepetitions) private var removeRepetitions = true

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(loc.t("style.cleanup.message"))
                .font(theme.bodyFont)
                .foregroundStyle(theme.textSecondary)
            Card {
                VStack(alignment: .leading, spacing: 12) {
                    SettingsRow(loc.t("style.cleanup.fillers"), detail: loc.t("style.cleanup.fillers.detail")) {
                        ThemedToggle(isOn: $removeFillers)
                    }
                    .padding(.vertical, -8)
                    BeforeAfter(before: loc.t("style.cleanup.fillers.before"), after: loc.t("style.cleanup.fillers.after"),
                                isOn: removeFillers)
                }
            }
            Card {
                VStack(alignment: .leading, spacing: 12) {
                    SettingsRow(loc.t("style.cleanup.repetitions"), detail: loc.t("style.cleanup.repetitions.detail")) {
                        ThemedToggle(isOn: $removeRepetitions)
                    }
                    .padding(.vertical, -8)
                    BeforeAfter(before: loc.t("style.cleanup.repetitions.before"),
                                after: loc.t("style.cleanup.repetitions.after"), isOn: removeRepetitions)
                }
            }
        }
    }
}

private struct BeforeAfter: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let before: String
    let after: String
    let isOn: Bool

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            example(label: loc.t("style.cleanup.before"), text: before)
            Image(systemName: "arrow.right")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(theme.textSecondary)
            example(label: loc.t("style.cleanup.after"), text: isOn ? after : before)
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func example(label: String, text: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(theme.captionMediumFont)
                .foregroundStyle(theme.textSecondary)
            Text(text)
                .font(theme.styleSampleFont)
                .foregroundStyle(theme.textPrimary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(theme.insetBackground, in: RoundedRectangle(cornerRadius: 10))
    }
}
