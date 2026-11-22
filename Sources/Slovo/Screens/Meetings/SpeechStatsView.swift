import SlovoCore
import SwiftUI

/// Who talked how much, at what pace, and how much of the recording was speech at all.
///
/// Everything is counted from the transcript on the spot. The numbers describe the whole
/// recording: the speaker filter above changes what the transcript shows, not what the
/// meeting was.
struct SpeechStatsView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let meeting: MeetingRecord

    var body: some View {
        let stats = SpeechStatsCalculator.compute(for: meeting)
        if stats.isEmpty {
            Text(loc.t("meetings.transcript.empty"))
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
                .padding(18)
        } else {
            VStack(alignment: .leading, spacing: 0) {
                reading(stats)
                RowDivider()
                PanelScroll {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(stats.speakers.enumerated()), id: \.element.id) { index, speaker in
                            if index > 0 {
                                RowDivider()
                                    .padding(.leading, 18)
                            }
                            SpeakerStatsRow(meeting: meeting, stats: speaker)
                        }
                        RowDivider()
                        MeetingSpeechSection(stats: stats)
                    }
                    .padding(.vertical, 4)
                }
            }
        }
    }

    /// The line worth reading before the numbers: who held the floor, with the owner's own
    /// share beside it, which is the number he came here for.
    private func reading(_ stats: SpeechStats) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(headline(stats))
                .font(theme.headlineFont)
                .foregroundStyle(theme.textPrimary)
                .fixedSize(horizontal: false, vertical: true)
            Text(caption(stats))
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 18)
        .padding(.vertical, 12)
    }

    private func headline(_ stats: SpeechStats) -> String {
        guard stats.speakers.count > 1 else { return loc.t("meetings.stats.single") }
        guard let top = stats.speakers.first, let speaker = meeting.speaker(id: top.speakerID) else {
            return loc.t("meetings.stats.single")
        }
        // Naming one voice as "the one who talks the most" would be a coin flip when the
        // runner-up spoke exactly as long; say so plainly instead.
        guard stats.speakers[1].speakingSeconds != top.speakingSeconds else {
            return loc.t("meetings.stats.tied")
        }
        return TranscriptMerger.isMe(speaker, in: meeting)
            ? loc.t("meetings.stats.topIsYou")
            : loc.t("meetings.stats.top", model.speakerLabel(speaker, in: meeting))
    }

    /// The owner's share, unless he is the only voice and it can only be all of it.
    private func caption(_ stats: SpeechStats) -> String {
        var parts: [String] = []
        if stats.speakers.count > 1, let mine = stats.speakers.first(where: isMe) {
            parts.append(loc.t("meetings.stats.yourShare", loc.percent(mine.share)))
        }
        parts.append(loc.t("meetings.stats.wholeMeeting"))
        return parts.joined(separator: " · ")
    }

    private func isMe(_ stats: SpeakerSpeechStats) -> Bool {
        guard let speaker = meeting.speaker(id: stats.speakerID) else { return false }
        return TranscriptMerger.isMe(speaker, in: meeting)
    }
}

/// One speaker: their share as a bar in their own colour, and the numbers behind it.
private struct SpeakerStatsRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let meeting: MeetingRecord
    let stats: SpeakerSpeechStats

    var body: some View {
        let speaker = meeting.speaker(id: stats.speakerID)
        let color = speaker.map { theme.speakerColor($0.colorIndex) } ?? theme.textSecondary
        VStack(alignment: .leading, spacing: 7) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(speaker.map { model.speakerLabel($0, in: meeting) } ?? stats.speakerID)
                    .font(theme.bodyMediumFont)
                    .foregroundStyle(color)
                    .lineLimit(1)
                Spacer(minLength: 8)
                Text(loc.percent(stats.share))
                    .font(.system(size: 13, weight: .semibold).monospacedDigit())
                    .foregroundStyle(theme.textPrimary)
            }
            ThemedProgressBar(fraction: stats.share, tint: color)
            Text(detail)
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 11)
    }

    /// Talk time, turns, the average turn and the pace, as one quiet line. A single turn
    /// is its own average, and too little speech has no honest pace.
    private var detail: String {
        var parts = [clockText(stats.speakingSeconds), loc.plural("plural.turns", stats.turns)]
        if stats.turns > 1 {
            parts.append(loc.t("meetings.stats.averageTurn", clockText(stats.averageTurnSeconds)))
        }
        if let pace = stats.wordsPerMinute {
            parts.append(loc.t("meetings.stats.pace", loc.number(Int(pace.rounded()))))
        }
        return parts.joined(separator: " · ")
    }
}

/// The recording as a whole: how much of it was speech, and the silences inside it.
private struct MeetingSpeechSection: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    let stats: SpeechStats

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionLabel(text: loc.t("meetings.stats.meeting"))
            ThemedProgressBar(fraction: stats.speechShare, height: 8)
            WrapLayout(spacing: 22, lineSpacing: 12) {
                figure(title: loc.t("meetings.stats.speech"), value: clockText(stats.speechSeconds),
                       caption: loc.percent(stats.speechShare))
                figure(title: loc.t("meetings.stats.silence"), value: clockText(stats.silenceSeconds))
                figure(title: loc.t("meetings.stats.longestPause"), value: clockText(stats.longestPauseSeconds))
                figure(title: loc.t("meetings.stats.pauses", thresholdText), value: loc.number(stats.pauseCount))
            }
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 14)
    }

    /// The silence a gap has to beat to be counted, «5 с» / "5 s".
    private var thresholdText: String {
        loc.t("meetings.stats.seconds", loc.number(Int(stats.pauseSeconds.rounded())))
    }

    private func figure(title: String, value: String, caption: String? = nil) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(theme.captionFont)
                .foregroundStyle(theme.textSecondary)
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                Text(value)
                    .font(.system(size: 15, weight: .medium).monospacedDigit())
                    .foregroundStyle(theme.textPrimary)
                if let caption {
                    Text(caption)
                        .font(theme.monoCaptionFont)
                        .foregroundStyle(theme.textSecondary)
                }
            }
        }
        .fixedSize()
    }
}
