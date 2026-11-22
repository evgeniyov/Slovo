import AppKit
import Combine
import QuartzCore
import SlovoCore
import SwiftUI

/// Borderless, non-activating overlay that never becomes key and never steals focus.
final class FlowBarPanel: NSPanel {
    init(size: NSSize) {
        super.init(contentRect: NSRect(origin: .zero, size: size),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        isFloatingPanel = true
        level = .statusBar
        // Space eligibility follows the same input/visibility rule as the pill;
        // see `FlowBarController.update()`.
        collectionBehavior = FlowBarController.idleSpaces
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        isReleasedWhenClosed = false
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Whether the pointer is over the Flow bar. The hover hint is wider than the idle
/// pill, so the controller sizes the panel from this too.
@MainActor
final class FlowBarHover: ObservableObject {
    /// The bar sits where the pointer travels to a video's controls or the Dock; passing
    /// through should not open it, only resting on it for a moment. The pill answers at once
    /// with a small nudge (`isPointerInside`), so the wait never feels like lag.
    static let dwell: TimeInterval = 0.22

    @Published private(set) var isHovering = false
    /// True as soon as the pointer is over the bar, before the dwell has passed.
    @Published private(set) var isPointerInside = false
    private var pendingEnter: DispatchWorkItem?

    /// Leaving takes effect at once; arriving only after the pointer has stayed.
    func pointerMoved(inside: Bool) {
        pendingEnter?.cancel()
        pendingEnter = nil
        if isPointerInside != inside {
            isPointerInside = inside
        }
        guard inside else {
            isHovering = false
            return
        }
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.isPointerInside else { return }
                self.isHovering = true
            }
        }
        pendingEnter = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.dwell, execute: work)
    }

    /// Clears hover at once, for when the panel leaves the screen: once it is
    /// off screen it can receive no more hover events, so nothing would otherwise
    /// clear a hover left over from before it hid.
    func reset() {
        pendingEnter?.cancel()
        pendingEnter = nil
        isPointerInside = false
        isHovering = false
    }
}

/// Whether the pill is on screen at all. The view animates it in and out; the controller
/// keeps the panel up until the animation out has finished.
@MainActor
final class FlowBarPresence: ObservableObject {
    @Published var isShown = false
}

/// The moment marked last, or the microphone switched last, while a meeting records,
/// shown for a moment in the Flow bar. At most one of the two is set.
@MainActor
final class FlowBarMoment: ObservableObject {
    /// Seconds into the recording; nil when no confirmation is showing.
    @Published var markedTime: TimeInterval?
    /// Whether the microphone was switched on or off; nil when that is not showing.
    @Published var microphoneOn: Bool?

    var isShowing: Bool {
        markedTime != nil || microphoneOn != nil
    }
}

/// Keeps the Flow bar panel exactly as large as its pill, so the transparent rest of
/// the bar never swallows clicks meant for the windows below, and places it 12 pt
/// above the visible frame of the screen with keyboard focus.
///
/// Whether the bar shows follows the dictation state, and the pill is measured in a
/// separate hosting view: the panel's own view does not lay out while the panel is
/// off screen, so it can never report the size that would bring the panel on screen.
@MainActor
final class FlowBarController {
    /// The size screenshots use for every state.
    static let panelSize = NSSize(width: 360, height: 72)
    /// Room around the pill for its shadow.
    static let margin: CGFloat = 12
    /// Distance from the Dock, or from the bottom of the screen when the Dock hides. Small,
    /// because the bar floats over other apps and the lower it sits, the less it covers.
    static let dockGap: CGFloat = 3
    /// Every desktop while the idle bar is unavailable and animating out.
    nonisolated static let idleSpaces: NSWindow.CollectionBehavior = [.canJoinAllSpaces]
    /// Full-screen spaces too, so an available dictation button can be clicked there.
    nonisolated static let activeSpaces: NSWindow.CollectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

    private let panel = FlowBarPanel(size: FlowBarController.panelSize)
    private let model: AppModel
    private let hover = FlowBarHover()
    private let moment = FlowBarMoment()
    private let presence = FlowBarPresence()
    /// How long the panel waits before shrinking, so the pill's spring can settle inside it.
    static let shrinkDelay: TimeInterval = 0.45
    private var momentHide: DispatchWorkItem?
    private var markSound: NSSound?
    /// The confirmation stays this long after each mark.
    static let momentDuration: TimeInterval = 1.5
    private let measuringView = NSHostingView(rootView: AnyView(EmptyView()))
    private var pillSize: CGSize = .zero
    private var pendingShrink: DispatchWorkItem?
    private var observers: [NSObjectProtocol] = []
    private var cancellables: Set<AnyCancellable> = []
    private var isEnabled = false

    init(model: AppModel) {
        self.model = model
        let hover = self.hover
        let moment = self.moment
        let presence = self.presence
        let hosting = NSHostingView(rootView: AppRoot(model: model) {
            FlowBarView(dictation: model.dictation, hover: hover, moment: moment, presence: presence)
        })
        // The controller sizes the panel; the hosting view must not impose its own limits.
        hosting.sizingOptions = []
        panel.contentView = hosting

        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshPlacement() }
        })
        // App activation alone does not cover switching Spaces, waking, or unlocking.
        // Re-read visibility too: the cached size may still describe a hidden window.
        for name in [NSWorkspace.didActivateApplicationNotification,
                     NSWorkspace.activeSpaceDidChangeNotification,
                     NSWorkspace.didWakeNotification,
                     NSWorkspace.sessionDidBecomeActiveNotification] {
            observers.append(NSWorkspace.shared.notificationCenter.addObserver(
                forName: name, object: nil, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.refreshPlacement() }
            })
        }
        observers.append(NotificationCenter.default.addObserver(
            forName: NSApplication.didUnhideNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshPlacement() }
        })
        // "Show Flow bar when idle" and the key named in the hint.
        observers.append(NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: model.settings.defaults, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.updateFocusWatcher()
                self?.update()
            }
        })
        // Everything else that changes the pill's shape. `@Published` fires before the
        // new value is stored, so the update runs a turn later.
        let dictation = model.dictation
        Publishers.MergeMany(
            dictation.$state.map { _ in () }.eraseToAnyPublisher(),
            dictation.$handsFreeRemaining.map { _ in () }.eraseToAnyPublisher(),
            hover.$isHovering.map { _ in () }.eraseToAnyPublisher(),
            model.focusedInput.$focus.map { _ in () }.eraseToAnyPublisher(),
            moment.$markedTime.map { _ in () }.eraseToAnyPublisher(),
            moment.$microphoneOn.map { _ in () }.eraseToAnyPublisher(),
            model.localizer.$language.map { _ in () }.eraseToAnyPublisher()
        )
        .receive(on: DispatchQueue.main)
        .sink { [weak self] in self?.update() }
        .store(in: &cancellables)

        // WindowServer can order a panel out without changing any published state.
        // Recover that case even when focus stays in the same field for hours. This
        // check does no measuring/layout while the panel and its content are visible.
        Timer.publish(every: 2, on: .main, in: .common)
            .autoconnect()
            .sink { [weak self] _ in
                guard let self, self.isEnabled, !self.isPillHidden,
                      !self.panel.isVisible || !self.presence.isShown else { return }
                self.update()
            }
            .store(in: &cancellables)

        model.meetingRecorder.$lastMarkedMoment
            .dropFirst()
            .compactMap { $0 }
            .receive(on: DispatchQueue.main)
            .sink { [weak self] highlight in self?.showMarkedMoment(highlight) }
            .store(in: &cancellables)
        // The recorder plays the switch's own sound; the bar only shows it.
        model.meetingRecorder.$lastMicrophoneSwitch
            .dropFirst()
            .compactMap { $0 }
            .receive(on: DispatchQueue.main)
            .sink { [weak self] change in self?.showMicrophoneSwitch(change) }
            .store(in: &cancellables)
    }

    /// «Marked 12:40» for a moment, with a soft sound when sounds are on.
    private func showMarkedMoment(_ highlight: MeetingHighlight) {
        moment.microphoneOn = nil
        moment.markedTime = highlight.time
        if model.settings.soundsEnabled {
            playMarkSound()
        }
        hideMomentLater()
    }

    /// «Microphone on» or «Microphone off», the same way.
    private func showMicrophoneSwitch(_ change: MicrophoneSwitch) {
        moment.markedTime = nil
        moment.microphoneOn = change.isOn
        hideMomentLater()
    }

    private func hideMomentLater() {
        momentHide?.cancel()
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                self?.moment.markedTime = nil
                self?.moment.microphoneOn = nil
            }
        }
        momentHide = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.momentDuration, execute: work)
    }

    private func playMarkSound() {
        if markSound == nil {
            // A copy, so the lower volume does not change the shared named sound.
            markSound = NSSound(named: NSSound.Name("Purr"))?.copy() as? NSSound
            markSound?.volume = 0.35
        }
        guard let markSound else { return }
        if markSound.isPlaying {
            markSound.stop()
        }
        markSound.play()
    }

    func show() {
        isEnabled = true
        updateFocusWatcher()
        update()
        reposition()
    }

    func hide() {
        isEnabled = false
        updateFocusWatcher()
        pendingShrink?.cancel()
        pendingShrink = nil
        momentHide?.cancel()
        presence.isShown = false
        hover.reset()
        panel.orderOut(nil)
    }

    private func updateFocusWatcher() {
        if isEnabled && model.settings.showFlowBarWhenIdle {
            model.focusedInput.start()
        } else {
            model.focusedInput.stop()
        }
    }

    private func refreshPlacement() {
        guard isEnabled else { return }
        update()
        // Keep the larger animation canvas until a pending shrink has settled.
        if pendingShrink == nil {
            reposition()
        }
    }

    /// Measures the pill for the current state, then grows the panel at once or
    /// shrinks it after the pill's own animation.
    private func update() {
        guard isEnabled else { return }
        if !panel.isVisible, hover.isPointerInside || hover.isHovering {
            // An externally hidden panel may never receive the pointer-exit event.
            hover.reset()
        }
        // A field in a full-screen app needs the start button before dictation starts.
        // Unknown AX focus must use the same policy, since some composers expose none.
        let hidden = isPillHidden
        let spaces = hidden ? Self.idleSpaces : Self.activeSpaces
        if panel.collectionBehavior != spaces {
            panel.collectionBehavior = spaces
            // A visible window picks up new space rules only when ordered again.
            if panel.isVisible {
                panel.orderFrontRegardless()
            }
        }
        if presence.isShown == hidden {
            presence.isShown = !hidden
        }
        let size = hidden ? .zero : measurePill()
        guard size != pillSize else {
            // Equal desired sizes do not imply equal window visibility. In particular,
            // show() after hide() and a panel ordered out by macOS need ordering in.
            if !hidden, !panel.isVisible {
                pendingShrink?.cancel()
                pendingShrink = nil
                reposition()
            }
            return
        }
        let grows = size.width > pillSize.width || size.height > pillSize.height
        pillSize = size
        pendingShrink?.cancel()
        pendingShrink = nil
        if grows {
            reposition()
        } else {
            let work = DispatchWorkItem { [weak self] in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.pendingShrink = nil
                    self.reposition()
                }
            }
            pendingShrink = work
            DispatchQueue.main.asyncAfter(deadline: .now() + Self.shrinkDelay, execute: work)
        }
    }

    /// Only an idle bar hides: with "Show Flow bar when idle" off, or while focus is
    /// known to be somewhere nothing can be typed. Recordings, processing and messages
    /// always show. Focus that cannot be read keeps the bar, since hiding it from a field
    /// that was simply not recognised is the worse mistake.
    private var isPillHidden: Bool {
        let idleHidden = !model.settings.showFlowBarWhenIdle || model.focusedInput.focus == .other
        return model.dictation.state == .idle && idleHidden && !moment.isShowing
    }

    private func measurePill() -> CGSize {
        let model = self.model
        let dictation = model.dictation
        let isHovering = hover.isHovering
        let markedTime = moment.markedTime
        let microphoneOn = moment.microphoneOn
        measuringView.rootView = AnyView(AppRoot(model: model) {
            FlowBarContent(state: dictation.state, level: 0, remaining: dictation.handsFreeRemaining,
                           keySymbol: model.settings.dictationKey.symbol, showWhenIdle: true,
                           isHovering: isHovering, isMeasuring: true, markedMoment: markedTime,
                           microphoneSwitch: microphoneOn)
        })
        let size = measuringView.fittingSize
        return CGSize(width: max(1, size.width), height: max(1, size.height))
    }

    private func reposition() {
        guard isEnabled else { return }
        guard pillSize.width > 0, pillSize.height > 0 else {
            // Off screen, the panel can receive no more hover events; without this,
            // a hover left over from before it hid would stick until the panel
            // happens to show again with the pointer already resting on it.
            hover.reset()
            panel.orderOut(nil)
            return
        }
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visible = screen.visibleFrame
        let width = ceil(pillSize.width) + Self.margin * 2
        let height = ceil(pillSize.height) + Self.margin * 2
        let frame = NSRect(x: round(visible.midX - width / 2), y: visible.minY + Self.dockGap - Self.margin,
                           width: width, height: height)
        if panel.frame != frame {
            // The hosting view is layer-backed, so an ordinary setFrame lets Core
            // Animation implicitly animate its layer to the new bounds — stretching
            // the still-old pill toward the panel's corner for a frame before
            // SwiftUI redraws it in place. Disabling actions makes the resize itself
            // instant; the pill's own look still animates via `.animation(value:)`.
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            panel.setFrame(frame, display: true)
            CATransaction.commit()
        }
        if !panel.isVisible || !panel.isOnActiveSpace {
            panel.orderFrontRegardless()
        }
    }
}

/// Connects the live dictation state to `FlowBarContent`.
struct FlowBarView: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var dictation: DictationController
    @ObservedObject var hover: FlowBarHover
    @ObservedObject var moment: FlowBarMoment
    @ObservedObject var presence: FlowBarPresence
    @AppStorage(SettingsKey.showFlowBarWhenIdle) private var showWhenIdle = true
    @AppStorage(SettingsKey.dictationKey) private var keyRaw = DictationKey.fn.rawValue

    var body: some View {
        FlowBarContent(state: dictation.state, level: dictation.level, remaining: dictation.handsFreeRemaining,
                       keySymbol: (DictationKey(rawValue: keyRaw) ?? .fn).symbol,
                       showWhenIdle: showWhenIdle, isHovering: hover.isHovering,
                       isPointerInside: hover.isPointerInside, isShown: presence.isShown,
                       onStart: { dictation.startHandsFree() },
                       onStop: { dictation.finish() },
                       onCancel: { dictation.cancel() },
                       onMessageTap: handleMessageTap,
                       onHover: { hover.pointerMoved(inside: $0) },
                       nothingHeardDevice: dictation.lastInputDeviceName,
                       markedMoment: moment.markedTime,
                       microphoneSwitch: moment.microphoneOn)
    }

    private func handleMessageTap(_ message: FlowMessage) {
        if case .nothingHeard = message {
            // The usual cause is the wrong input, such as a headset jack with no microphone.
            model.openSettings(.general)
        } else if case .copied(.accessibilityMissing) = message {
            model.permissions.openSystemSettings(for: .accessibility)
        } else if case let .failure(failure) = message {
            if let permission = failure.permission {
                model.permissions.openSystemSettings(for: permission)
            } else if case .modelMissing = failure {
                model.openSettings(.recognition)
            }
        }
        dictation.dismissMessage()
    }
}

/// The Flow bar for a given state; pure, so it can be rendered for every state.
///
/// Every state is the same capsule: the small idle pill grows into the dark bar and
/// flows from the hint to a recording, to recognition, to a message and back, changing
/// its size and colour on one spring while its content fades in once the shape opens.
/// Nothing is swapped for a different shape, so the bar reads as one object that
/// comes alive rather than as pictures replacing each other.
struct FlowBarContent: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.isSnapshot) private var isSnapshot
    let state: DictationState
    let level: Float
    let remaining: TimeInterval?
    let keySymbol: String
    let showWhenIdle: Bool
    let isHovering: Bool
    /// The pointer is over the idle pill but has not rested long enough to open it: the
    /// pill nudges larger and warmer at once.
    var isPointerInside = false
    /// False while the pill animates away before the panel hides.
    var isShown = true
    /// Only the pill at its own size, for measuring the panel.
    var isMeasuring = false
    var onStart: () -> Void = {}
    var onStop: () -> Void = {}
    var onCancel: () -> Void = {}
    var onMessageTap: (FlowMessage) -> Void = { _ in }
    var onHover: (Bool) -> Void = { _ in }
    /// The microphone named in "Nothing heard", when known.
    var nothingHeardDevice: String?
    /// The recording position of a moment just marked; its confirmation replaces the
    /// idle pill and messages while it shows.
    var markedMoment: TimeInterval?
    /// The microphone state a meeting was just switched to, confirmed the same way.
    var microphoneSwitch: Bool?

    static let barHeight: CGFloat = 34
    static let idleSize = CGSize(width: 40, height: 8)
    static let awakeSize = CGSize(width: 58, height: 10)
    /// The idle pill is small; the area that reacts to the pointer is not.
    static let idleHitArea = CGSize(width: 72, height: 22)

    /// Dictation is blocked while a meeting records, so the confirmation never hides a
    /// recording or text being recognized.
    private var showsMeetingNotice: Bool {
        guard markedMoment != nil || microphoneSwitch != nil else { return false }
        switch state {
        case .idle, .message: return true
        case .recording, .processing: return false
        }
    }

    /// Whether the capsule is the dark bar with content, rather than the small idle pill.
    private var isExpanded: Bool {
        if showsMeetingNotice { return true }
        if case .idle = state { return isHovering }
        return true
    }

    /// Identifies what the dark bar shows; a change fades one content out and the next in
    /// while the capsule reshapes.
    private var contentKey: String {
        if showsMeetingNotice, let microphoneSwitch {
            return "microphone-\(microphoneSwitch)"
        }
        if showsMeetingNotice, let markedMoment {
            return "moment-\(markedMoment)"
        }
        switch state {
        case .idle: return isHovering ? "hint" : "idle"
        case let .recording(info): return "recording-\(info.mode.rawValue)-\(remaining == nil)"
        case let .processing(isLoadingModel): return "processing-\(isLoadingModel)"
        case let .message(message): return "message-\(String(describing: message))"
        }
    }

    var body: some View {
        if isMeasuring {
            capsule
                .fixedSize()
        } else {
            ZStack {
                if isShown, isExpanded || showWhenIdle {
                    capsule
                        .transition(appearTransition)
                }
            }
            .fixedSize()
            // Hover is tracked on the container that outlives every reshaping, so the
            // capsule growing under the pointer cannot bounce the hover state.
            .onHover(perform: onHover)
            .animation(morph, value: contentKey)
            .animation(morph, value: isShown)
            .animation(reduceMotion ? nil : .spring(duration: 0.24, bounce: 0.4), value: isPointerInside)
            .padding(.bottom, FlowBarController.margin)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
        }
    }

    /// One spring for every change of shape: quick, with a little life at the end.
    private var morph: Animation? {
        reduceMotion ? .easeInOut(duration: 0.15) : .spring(duration: 0.42, bounce: 0.26)
    }

    private var capsule: some View {
        let expanded = isExpanded
        let awake = !expanded && isPointerInside
        let idle = awake ? Self.awakeSize : Self.idleSize
        return ZStack {
            if expanded {
                content
                    .fixedSize()
                    .id(contentKey)
                    .transition(contentTransition)
            }
        }
        .frame(width: expanded ? nil : idle.width, height: expanded ? Self.barHeight : idle.height)
        .frame(minWidth: expanded ? 120 : nil)
        .background(Capsule().fill(expanded ? theme.flowBarBackground : (awake ? theme.flowBarAccent : theme.flowBarIdle)))
        .overlay(Capsule().strokeBorder(theme.flowBarBorder, lineWidth: 1))
        .clipShape(Capsule())
        // The shadow grows with the bar instead of appearing with it.
        .shadow(color: theme.shadow.opacity(expanded ? 1 : (awake ? 0.6 : 0)),
                radius: expanded ? 7 : 3, y: expanded ? 2 : 1)
        .frame(width: expanded ? nil : Self.idleHitArea.width, height: expanded ? nil : Self.idleHitArea.height)
        .contentShape(expanded ? AnyShape(Capsule()) : AnyShape(Rectangle()))
        .onTapGesture { tapAction?() }
        .help(helpText)
    }

    /// Content appears once the capsule has started to open, and leaves at once.
    private var contentTransition: AnyTransition {
        if reduceMotion { return .opacity }
        return .asymmetric(
            insertion: .opacity.combined(with: .scale(scale: 0.88)).animation(.easeOut(duration: 0.22).delay(0.07)),
            removal: .opacity.animation(.easeIn(duration: 0.08)))
    }

    /// The whole pill coming to or leaving a text field.
    private var appearTransition: AnyTransition {
        reduceMotion ? .opacity : .scale(scale: 0.5, anchor: .bottom).combined(with: .opacity)
    }

    private var tapAction: (() -> Void)? {
        if showsMeetingNotice { return nil }
        switch state {
        case .idle: return onStart
        case let .message(message): return { onMessageTap(message) }
        case .recording, .processing: return nil
        }
    }

    private var helpText: String {
        if case .idle = state, !showsMeetingNotice { return loc.t("flowbar.clickToStart") }
        return ""
    }

    @ViewBuilder
    private var content: some View {
        if showsMeetingNotice, let microphoneSwitch {
            HStack(spacing: 7) {
                Image(systemName: microphoneSwitch ? "mic.fill" : "mic.slash.fill")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(microphoneSwitch ? theme.flowBarAccent : theme.flowBarWarning)
                Text(microphoneSwitch ? loc.t("flowbar.microphoneOn") : loc.t("flowbar.microphoneOff"))
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(theme.flowBarForeground)
            }
            .padding(.horizontal, 16)
        } else if showsMeetingNotice, let markedMoment {
            HStack(spacing: 7) {
                Image(systemName: "star.fill")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.flowBarWarning)
                    .symbolEffect(.bounce, value: markedMoment)
                Text(loc.t("flowbar.momentMarked", clockText(markedMoment)))
                    .font(.system(size: 12, weight: .medium).monospacedDigit())
                    .foregroundStyle(theme.flowBarForeground)
            }
            .padding(.horizontal, 16)
        } else {
            stateContent
        }
    }

    @ViewBuilder
    private var stateContent: some View {
        switch state {
        case .idle:
            HStack(spacing: 7) {
                Image(systemName: "waveform")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(theme.flowBarAccent)
                    // The waveform breathes while the hint waits for a click.
                    .symbolEffect(.variableColor.iterative.dimInactiveLayers, options: .repeating,
                                  isActive: !reduceMotion && !isSnapshot && !isMeasuring)
                Text(loc.t("flowbar.hint", keySymbol))
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(theme.flowBarForeground)
            }
            .padding(.horizontal, 16)
        case let .recording(info):
            HStack(spacing: 10) {
                if info.mode == .handsFree {
                    circleButton("xmark", help: loc.t("flowbar.cancel"), filled: false, action: onCancel)
                }
                LevelBars(level: level)
                if let remaining {
                    Text(clockText(remaining.rounded(.up)))
                        .font(.system(size: 12, weight: .semibold).monospacedDigit())
                        .foregroundStyle(remaining <= 10 ? theme.flowBarWarning : theme.flowBarSecondary)
                        .contentTransition(.numericText(countsDown: true))
                }
                if info.mode == .handsFree {
                    circleButton("stop.fill", help: loc.t("flowbar.stop"), filled: true, action: onStop)
                }
            }
            .padding(.horizontal, info.mode == .handsFree ? 6 : 16)
        case let .processing(isLoadingModel):
            HStack(spacing: 9) {
                // A model stuck loading or an engine that hangs must not hold the bar.
                circleButton("xmark", help: loc.t("flowbar.cancel"), filled: false, action: onCancel)
                ProcessingDots()
                if isLoadingModel {
                    Text(loc.t("flowbar.loadingModel"))
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(theme.flowBarForeground)
                }
            }
            .padding(.leading, 6)
            .padding(.trailing, 16)
        case let .message(message):
            HStack(spacing: 7) {
                Image(systemName: symbol(for: message))
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(isFailure(message) ? theme.flowBarWarning : theme.flowBarAccent)
                Text(text(for: message))
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(theme.flowBarForeground)
                    .lineLimit(1)
                    .frame(maxWidth: 300)
                if isActionable(message) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(theme.flowBarSecondary)
                }
            }
            .padding(.horizontal, 16)
        }
    }

    private func circleButton(_ symbol: String, help: String, filled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(filled ? theme.flowBarBackground : theme.flowBarForeground)
                .frame(width: 22, height: 22)
                .background(filled ? theme.flowBarForeground : theme.flowBarButtonBackground, in: Circle())
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
    }

    private func isFailure(_ message: FlowMessage) -> Bool {
        if case .failure = message { return true }
        return false
    }

    private func isActionable(_ message: FlowMessage) -> Bool {
        if case .nothingHeard = message { return true }
        if case .copied(.accessibilityMissing) = message { return true }
        guard case let .failure(failure) = message else { return false }
        if failure.permission != nil { return true }
        if case .modelMissing = failure { return true }
        return false
    }

    private func symbol(for message: FlowMessage) -> String {
        switch message {
        case .copied: return "doc.on.clipboard"
        case .nothingHeard: return "mic.slash"
        case .blockedByMeeting: return "person.2.wave.2"
        case .failure: return "exclamationmark.triangle.fill"
        }
    }

    private func text(for message: FlowMessage) -> String {
        switch message {
        case .copied(.secureInput): return loc.t("flowbar.copied.secureInput")
        case .copied(.accessibilityMissing): return loc.t("flowbar.copied.accessibility")
        case .copied(.focusChanged): return loc.t("flowbar.copied.focusChanged")
        case .copied(.pasteFailed): return loc.t("flowbar.copied")
        case .nothingHeard:
            return nothingHeardDevice.map { loc.t("flowbar.nothingHeard.device", $0) } ?? loc.t("flowbar.nothingHeard")
        case .blockedByMeeting: return loc.t("flowbar.meetingInProgress")
        case let .failure(failure):
            switch failure {
            case .microphoneDenied: return loc.t("flowbar.error.microphone")
            case .accessibilityMissing: return loc.t("flowbar.error.accessibility")
            case .inputMonitoringMissing: return loc.t("flowbar.error.inputMonitoring")
            case .modelMissing: return loc.t("flowbar.error.model")
            case let .engineFailed(message), let .recordingFailed(message):
                // Service messages are localized already; long ones fall back to a short line.
                return message.isEmpty || message.count > 48 ? loc.t("flowbar.error.generic") : message
            }
        }
    }
}

/// Eleven bars whose heights follow the input level, tallest in the middle, with a
/// gentle ripple unless Reduce Motion is on.
private struct LevelBars: View {
    @Environment(\.theme) private var theme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.isSnapshot) private var isSnapshot
    let level: Float

    var body: some View {
        if reduceMotion || isSnapshot {
            bars(phase: 0.9, ripple: !reduceMotion)
        } else {
            TimelineView(.animation(minimumInterval: 1.0 / 30)) { _ in
                bars(phase: ProcessInfo.processInfo.systemUptime, ripple: true)
            }
        }
    }

    private func bars(phase: TimeInterval, ripple: Bool) -> some View {
        // Speech RMS sits low on the 0...1 scale; the square root spreads it over the bar height.
        let amplitude = min(1, Double(max(level, 0)).squareRoot() * 1.3)
        return HStack(alignment: .center, spacing: 2.5) {
            ForEach(0..<11, id: \.self) { index in
                let distance = abs(Double(index) - 5) / 5
                let envelope = 1 - 0.62 * distance * distance
                let wobble = ripple ? 0.7 + 0.3 * sin(phase * 7.5 + Double(index) * 1.35) : 1
                Capsule()
                    .fill(theme.flowBarForeground)
                    .frame(width: 3, height: max(3, 3 + 17 * amplitude * envelope * wobble))
            }
        }
        .frame(height: 20)
        .animation(reduceMotion ? nil : .linear(duration: 0.08), value: level)
    }
}

/// Three dots that pulse in turn while text is being recognized.
private struct ProcessingDots: View {
    @Environment(\.theme) private var theme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.isSnapshot) private var isSnapshot

    var body: some View {
        if reduceMotion || isSnapshot {
            dots(phase: 0.4)
        } else {
            TimelineView(.animation(minimumInterval: 1.0 / 30)) { _ in
                dots(phase: ProcessInfo.processInfo.systemUptime)
            }
        }
    }

    private func dots(phase: TimeInterval) -> some View {
        HStack(spacing: 5) {
            ForEach(0..<3, id: \.self) { index in
                let pulse = reduceMotion ? 0.7 : 0.35 + 0.65 * max(0, sin(phase * 4.5 - Double(index) * 0.9))
                Circle()
                    .fill(theme.flowBarForeground)
                    .frame(width: 5, height: 5)
                    .opacity(pulse)
            }
        }
    }
}
