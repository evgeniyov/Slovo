import SlovoCore
import SwiftUI

/// Play/pause, a draggable scrubber, current/duration time and a speed choice for a
/// meeting's recorded audio. Hidden entirely once the audio is gone.
struct PlaybackBar: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @ObservedObject var playback: MeetingPlayback
    let meeting: MeetingRecord

    private static let rates: [Float] = [0.75, 1, 1.25, 1.5, 1.75, 2]

    var body: some View {
        let isCurrent = playback.meetingID == meeting.id
        let duration = isCurrent && playback.duration > 0 ? playback.duration : meeting.duration
        if isCurrent && playback.isPlaying {
            // Only the playhead needs a redraw clock; play/pause and a seek already
            // publish through `playback` itself.
            TimelineView(.periodic(from: .now, by: 0.1)) { _ in
                bar(currentTime: playback.currentTime(), duration: duration, isPlaying: true)
            }
        } else {
            bar(currentTime: isCurrent ? playback.currentTime() : 0, duration: duration, isPlaying: false)
        }
    }

    private func bar(currentTime: TimeInterval, duration: TimeInterval, isPlaying: Bool) -> some View {
        HStack(spacing: 10) {
            IconButton(symbol: isPlaying ? "pause.fill" : "play.fill",
                       help: isPlaying ? loc.t("meetings.playback.pause") : loc.t("meetings.playback.play")) {
                model.toggleMeetingPlayback(meeting)
            }
            Text(clockText(currentTime))
                .font(theme.monoCaptionFont)
                .foregroundStyle(theme.textSecondary)
                .frame(width: 42, alignment: .trailing)
            PlaybackScrubber(fraction: duration > 0 ? currentTime / duration : 0) { fraction in
                model.seekMeetingPlayback(meeting, to: fraction * duration)
            }
            Text(clockText(duration))
                .font(theme.monoCaptionFont)
                .foregroundStyle(theme.textSecondary)
                .frame(width: 42, alignment: .leading)
            PopupChoice(options: Self.rates.map { ($0, Self.speedLabel($0)) },
                        selection: Binding(get: { playback.rate }, set: { model.setMeetingPlaybackRate($0) }))
                .help(loc.t("meetings.playback.speed"))
        }
    }

    private static func speedLabel(_ rate: Float) -> String {
        "\(String(format: "%g", rate))×"
    }
}

/// A draggable, click-to-seek progress bar, drawn the same way `ThemedProgressBar` is.
private struct PlaybackScrubber: View {
    @Environment(\.theme) private var theme
    let fraction: Double
    let onSeek: (Double) -> Void
    @State private var dragFraction: Double?

    var body: some View {
        let shown = min(max(dragFraction ?? fraction, 0), 1)
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(theme.calendarEmpty)
                Capsule()
                    .fill(theme.accent)
                    .frame(width: max(6, proxy.size.width * shown))
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        dragFraction = clamped(value.location.x, in: proxy.size.width)
                    }
                    .onEnded { value in
                        let result = clamped(value.location.x, in: proxy.size.width)
                        dragFraction = nil
                        onSeek(result)
                    }
            )
        }
        .frame(height: 6)
    }

    private func clamped(_ x: CGFloat, in width: CGFloat) -> Double {
        guard width > 0 else { return 0 }
        return min(max(x / width, 0), 1)
    }
}
