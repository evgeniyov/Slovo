import AVKit
import SlovoCore
import SwiftUI

/// An inline player for a meeting's optional screen recording: a muted, low-frame-rate
/// reference video of what was on screen. Its file shares no timeline with
/// `MeetingPlayback`'s audio tracks, so it plays independently, with its own controls.
struct ScreenRecordingPlayer: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @StateObject private var holder: ScreenPlayerHolder

    init(url: URL) {
        _holder = StateObject(wrappedValue: ScreenPlayerHolder(url: url))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 5) {
                Image(systemName: "rectangle.on.rectangle")
                Text(loc.t("meetings.detail.screenRecording"))
            }
            .font(theme.captionFont)
            .foregroundStyle(theme.textSecondary)
            ScreenPlayerSurface(player: holder.player)
                .frame(maxWidth: .infinity, minHeight: 200, maxHeight: 260)
                .clipShape(RoundedRectangle(cornerRadius: theme.cardCornerRadius))
        }
        .onDisappear { holder.player.pause() }
    }
}

/// Owns the player so it is made once per card, not on every redraw of the header.
private final class ScreenPlayerHolder: ObservableObject {
    let player: AVPlayer

    init(url: URL) {
        player = AVPlayer(url: url)
    }
}

/// AppKit's `AVPlayerView` rather than SwiftUI's `VideoPlayer`: `VideoPlayer` lives in the
/// `_AVKit_SwiftUI` overlay, which a SwiftPM executable links without AVKit itself, so
/// creating one aborts the app ("failed to demangle superclass ... AVPlayerView").
/// Naming `AVPlayerView` here makes the linker pull AVKit in for real.
private struct ScreenPlayerSurface: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.player = player
        view.controlsStyle = .inline
        view.showsFullScreenToggleButton = true
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        if view.player !== player {
            view.player = player
        }
    }
}
