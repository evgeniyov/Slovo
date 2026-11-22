import SlovoCore
import SwiftUI

/// One downloadable model: name, trade-off, status badge, size, the damaged reason and
/// Download, Cancel or Delete. Shared by speech engines and the speaker model.
struct ModelRow<Badge: View>: View {
    @EnvironmentObject private var loc: Localizer
    @Environment(\.theme) private var theme
    @State private var isConfirmingDelete = false

    let title: String
    let tradeoff: String
    let status: ModelStatus
    /// Bytes a download fetches, shown before and during it.
    let downloadBytes: Int64
    /// A load reads the model files, so file actions wait for it.
    var isLoading = false
    /// Shown instead of any action when the model cannot run on this Mac.
    var unavailableReason: String?
    let deleteTitle: String
    let deleteMessage: String
    var onDownload: () -> Void = {}
    var onCancel: () -> Void = {}
    var onDelete: () -> Void = {}
    private let badge: Badge

    init(title: String, tradeoff: String, status: ModelStatus, downloadBytes: Int64, isLoading: Bool = false,
         unavailableReason: String? = nil, deleteTitle: String, deleteMessage: String,
         onDownload: @escaping () -> Void = {}, onCancel: @escaping () -> Void = {},
         onDelete: @escaping () -> Void = {}, @ViewBuilder badge: () -> Badge) {
        self.title = title
        self.tradeoff = tradeoff
        self.status = status
        self.downloadBytes = downloadBytes
        self.isLoading = isLoading
        self.unavailableReason = unavailableReason
        self.deleteTitle = deleteTitle
        self.deleteMessage = deleteMessage
        self.onDownload = onDownload
        self.onCancel = onCancel
        self.onDelete = onDelete
        self.badge = badge()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .center, spacing: 14) {
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 8) {
                        Text(title)
                            .font(theme.bodyMediumFont)
                            .foregroundStyle(theme.textPrimary)
                        badge
                    }
                    Text(tradeoff)
                        .font(theme.captionFont)
                        .foregroundStyle(theme.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                    HStack(spacing: 8) {
                        if let unavailableReason {
                            StatusBadge(text: unavailableReason, tone: .neutral)
                        } else {
                            statusBadge
                            if isLoading {
                                StatusBadge(text: loc.t("engine.status.loading"), tone: .accent)
                            }
                            if let size = sizeText {
                                Text(size)
                                    .font(theme.captionFont)
                                    .foregroundStyle(theme.textSecondary)
                            }
                        }
                    }
                    .padding(.top, 2)
                    if unavailableReason == nil, case let .damaged(reason) = status {
                        Text(reason)
                            .font(theme.captionFont)
                            .foregroundStyle(theme.destructive)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                if unavailableReason == nil {
                    actions
                        // A load reads the model files; replacing or deleting them under it
                        // would fail the load or crash the runtime reading them.
                        .disabled(isLoading)
                        .help(isLoading ? loc.t("engine.busy.loading") : "")
                }
            }
            if unavailableReason == nil, case let .downloading(progress) = status {
                ThemedProgressBar(fraction: progress)
            }
        }
        .padding(.vertical, 12)
        .alert(deleteTitle, isPresented: $isConfirmingDelete) {
            Button(loc.t("common.delete"), role: .destructive, action: onDelete)
            Button(loc.t("common.cancel"), role: .cancel) {}
        } message: {
            Text(deleteMessage)
        }
    }

    private var statusBadge: some View {
        switch status {
        case .unknown:
            return StatusBadge(text: loc.t("engine.status.checking"), tone: .neutral)
        case .ready:
            return StatusBadge(text: loc.t("engine.status.ready"), tone: .success)
        case .notDownloaded:
            return StatusBadge(text: loc.t("engine.status.notDownloaded"), tone: .neutral)
        case let .downloading(progress):
            return StatusBadge(text: loc.t("engine.status.downloading", Int((progress * 100).rounded())), tone: .accent)
        case .damaged:
            return StatusBadge(text: loc.t("engine.status.damaged"), tone: .destructive)
        case let .systemManaged(available):
            return available ? StatusBadge(text: loc.t("engine.status.system"), tone: .success)
                             : StatusBadge(text: loc.t("engine.status.unavailable"), tone: .neutral)
        }
    }

    private var sizeText: String? {
        switch status {
        case let .ready(diskBytes):
            return diskBytes > 0 ? loc.t("engine.size.onDisk", loc.bytes(diskBytes)) : nil
        case let .notDownloaded(bytes):
            let size = bytes > 0 ? bytes : downloadBytes
            return size > 0 ? loc.t("engine.size.download", loc.bytes(size)) : nil
        case .downloading:
            return downloadBytes > 0 ? loc.bytes(downloadBytes) : nil
        default:
            return nil
        }
    }

    @ViewBuilder
    private var actions: some View {
        switch status {
        case .notDownloaded:
            Button(action: onDownload) {
                Label(loc.t("engine.download"), systemImage: "arrow.down.circle")
            }
            .buttonStyle(SecondaryButtonStyle())
        case .damaged:
            Button(action: onDownload) {
                Label(loc.t("engine.redownload"), systemImage: "arrow.clockwise")
            }
            .buttonStyle(SecondaryButtonStyle())
        case .downloading:
            Button(loc.t("common.cancel"), action: onCancel)
                .buttonStyle(SecondaryButtonStyle())
        case .ready:
            Button(loc.t("common.delete")) {
                isConfirmingDelete = true
            }
            .buttonStyle(SecondaryButtonStyle(isDestructive: true))
        case .unknown, .systemManaged:
            EmptyView()
        }
    }
}

extension ModelRow where Badge == EmptyView {
    init(title: String, tradeoff: String, status: ModelStatus, downloadBytes: Int64, isLoading: Bool = false,
         unavailableReason: String? = nil, deleteTitle: String, deleteMessage: String,
         onDownload: @escaping () -> Void = {}, onCancel: @escaping () -> Void = {},
         onDelete: @escaping () -> Void = {}) {
        self.init(title: title, tradeoff: tradeoff, status: status, downloadBytes: downloadBytes, isLoading: isLoading,
                  unavailableReason: unavailableReason, deleteTitle: deleteTitle, deleteMessage: deleteMessage,
                  onDownload: onDownload, onCancel: onCancel, onDelete: onDelete) { EmptyView() }
    }
}
