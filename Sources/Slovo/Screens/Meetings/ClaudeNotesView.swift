import SlovoCore
import SwiftUI

/// What Claude concluded about the meeting: a summary, the decisions and the tasks it
/// found, under its name and the day it wrote them.
///
/// It sits in its own tab and says what it is at the top, because this is a retelling and
/// not a record: the transcript next door is what was said, and these lines are what one
/// reader made of it. One button removes the whole thing.
struct ClaudeNotesView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isConfirmingDelete = false
    let meeting: MeetingRecord
    let notes: MeetingNotes

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            attribution
            RowDivider()
            PanelScroll {
                VStack(alignment: .leading, spacing: 18) {
                    if !notes.summary.isEmpty {
                        Text(notes.summary)
                            .font(theme.bodyFont)
                            .foregroundStyle(theme.textPrimary)
                            .lineSpacing(3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    if !notes.decisions.isEmpty {
                        list(title: loc.t("meetings.claudeNotes.decisions"), items: notes.decisions,
                             symbol: "checkmark.circle")
                    }
                    if !notes.tasks.isEmpty {
                        list(title: loc.t("meetings.claudeNotes.tasks"), items: notes.tasks, symbol: "square")
                    }
                }
                .padding(.horizontal, 18)
                .padding(.vertical, 16)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .confirmationDialog(loc.t("meetings.claudeNotes.delete.title"), isPresented: $isConfirmingDelete) {
            Button(loc.t("common.delete"), role: .destructive) {
                model.deleteClaudeNotes(in: meeting.id)
            }
            Button(loc.t("common.cancel"), role: .cancel) {}
        } message: {
            Text(loc.t("meetings.claudeNotes.delete.message", meeting.title))
        }
    }

    /// Who wrote this and when, then the warning that it is a retelling. Both stay above
    /// the text itself, so the text is never read without them.
    private var attribution: some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(loc.t("meetings.claudeNotes.written", notes.author, writtenText))
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(theme.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)
                Text(loc.t("meetings.claudeNotes.disclaimer"))
                    .font(theme.captionFont)
                    .foregroundStyle(theme.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            IconButton(symbol: "trash", help: loc.t("meetings.claudeNotes.delete")) {
                isConfirmingDelete = true
            }
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 12)
    }

    private var writtenText: String {
        "\(loc.longDate(notes.writtenAt)), \(loc.timeText(notes.writtenAt))"
    }

    private func list(title: String, items: [String], symbol: String) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            SectionLabel(text: title)
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                HStack(alignment: .firstTextBaseline, spacing: 9) {
                    Image(systemName: symbol)
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(theme.textSecondary)
                        .frame(width: 14)
                    Text(item)
                        .font(theme.bodyFont)
                        .foregroundStyle(theme.textPrimary)
                        .lineSpacing(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }
}
