import Foundation

/// A resource URI, parsed strictly: scheme `slovo`, host `meetings`, then either nothing
/// or one `latest` or UUID segment followed by `transcript.md`. No file path is ever
/// built from URI text.
enum MCPResourceURI: Equatable {
    case index
    case latest
    case meeting(UUID)

    init?(_ text: String) {
        if text == MCPText.meetingsIndexURI {
            self = .index
            return
        }
        let prefix = MCPText.meetingsIndexURI + "/"
        let suffix = "/transcript.md"
        guard text.hasPrefix(prefix), text.hasSuffix(suffix), text.count > prefix.count + suffix.count else {
            return nil
        }
        let segment = String(text.dropFirst(prefix.count).dropLast(suffix.count))
        if segment == "latest" {
            self = .latest
        } else if segment.count == 36, let id = UUID(uuidString: segment) {
            self = .meeting(id)
        } else {
            return nil
        }
    }
}

enum MCPPromptKind: String {
    case tasks = "meeting_tasks"
    case summary = "meeting_summary"

    var description: String {
        switch self {
        case .tasks: return MCPText.meetingTasksDescription
        case .summary: return MCPText.meetingSummaryDescription
        }
    }
}

extension MCPHandler {
    static let resourceCharacterLimit = 100_000
    static let embeddedDocumentCharacterLimit = 40_000
    static let indexedMeetingCount = 50
    static let listedTranscriptCount = 20
    static let indexedSpeakersPerMeeting = 10

    // MARK: - Resources

    /// Empty while access is off.
    func listResources(context: MCPRequestContext) -> JSONValue {
        guard context.access.enabled else { return .object(["resources": .array([])]) }
        var resources: [JSONValue] = [
            [
                "uri": .string(MCPText.meetingsIndexURI), "name": .string(MCPText.meetingsIndexName),
                "title": .string(MCPText.meetingsIndexTitle), "description": .string(MCPText.meetingsIndexDescription),
                "mimeType": .string(MCPText.markdownMimeType)
            ],
            [
                "uri": .string(MCPText.latestTranscriptURI), "name": .string(MCPText.latestTranscriptName),
                "title": .string(MCPText.latestTranscriptTitle),
                "description": .string(MCPText.latestTranscriptDescription),
                "mimeType": .string(MCPText.markdownMimeType)
            ]
        ]
        let transcribed = context.visibleMeetings.filter { $0.record.status == .transcribed }
        for archived in transcribed.prefix(Self.listedTranscriptCount) {
            let view = MCPMeetingView(archived, language: context.language)
            let annotations: JSONValue = ["lastModified": .string(context.dates.iso(archived.recordModified))]
            resources.append([
                "uri": .string(MCPText.transcriptURI(view.id)), "name": .string("transcript-\(view.shortID)"),
                "title": .string(view.title), "mimeType": .string(MCPText.markdownMimeType),
                "annotations": annotations
            ])
        }
        return .object(["resources": .array(resources)])
    }

    func readResource(params: [String: JSONValue], context: MCPRequestContext) throws -> JSONValue {
        guard context.access.enabled else {
            throw MCPRPCError.invalidParams(MCPMessages.disabled(context.language))
        }
        if let wait = consumeRateLimit() {
            throw MCPRPCError(code: -32603, message: MCPMessages.tooManyRequests(wait, context.language))
        }
        guard let uri = params["uri"]?.stringValue else {
            throw MCPRPCError.invalidParams("resources/read requires a uri")
        }
        guard let target = MCPResourceURI(uri) else {
            throw MCPRPCError.resourceNotFound(uri: uri, era: context.era)
        }
        let text: String
        switch target {
        case .index:
            text = meetingsIndex(context: context)
        case .latest:
            guard let archived = context.visibleMeetings.first(where: { $0.record.status == .transcribed }) else {
                throw MCPRPCError.resourceNotFound(uri: uri, era: context.era)
            }
            text = resourceDocument(MCPMeetingView(archived, language: context.language), context: context)
        case let .meeting(id):
            guard let archived = context.visibleMeetings.first(where: { $0.record.id == id }),
                  archived.record.status == .transcribed else {
                throw MCPRPCError.resourceNotFound(uri: uri, era: context.era)
            }
            text = resourceDocument(MCPMeetingView(archived, language: context.language), context: context)
        }
        let content: JSONValue = [
            "uri": .string(uri), "mimeType": .string(MCPText.markdownMimeType), "text": .string(text)
        ]
        return .object(["contents": .array([content])])
    }

    /// The header, notice and untrusted wrapper of `get_transcript` around the meeting's
    /// `transcript.md` document, cut on a line boundary at 100,000 characters.
    ///
    /// The document holds what was said and nothing else: `MCPMeetingView` keeps Claude's
    /// own notes out of the record it renders, and their existence is reported here, above
    /// the notice and outside the wrapper, so a second run of a prompt cannot read an
    /// earlier summary as if it were the recording.
    func resourceDocument(_ view: MCPMeetingView, context: MCPRequestContext) -> String {
        var head = MCPTranscriptPager.headerLines(view, dates: context.dates).joined(separator: "\n") + "\n"
        if let notes = view.archived.record.claudeNotes {
            head += MCPText.claudeNotesLine(author: notes.author, written: context.dates.iso(notes.writtenAt)) + "\n"
        }
        head += MCPText.transcriptNotice + "\n\n" + MCPText.transcriptOpenTag + "\n"
        var markdown = TranscriptMerger.markdown(meeting: view.record, language: context.language)
        if !markdown.hasSuffix("\n") {
            markdown += "\n"
        }
        let closing = MCPText.transcriptCloseTag + "\n"
        let whole = head + markdown + closing
        guard whole.utf16.count > Self.resourceCharacterLimit else { return whole }

        let tail = "\n" + MCPText.resourceTruncated(id: view.id) + "\n"
        var budget = Self.resourceCharacterLimit - head.utf16.count - closing.utf16.count - tail.utf16.count
        var kept = ""
        for line in markdown.split(separator: "\n", omittingEmptySubsequences: false) {
            let piece = line + "\n"
            let length = piece.utf16.count
            guard length <= budget else { break }
            kept += piece
            budget -= length
        }
        return head + kept + closing + tail
    }

    /// One line per meeting, newest first.
    func meetingsIndex(context: MCPRequestContext) -> String {
        var lines = [MCPText.meetingsIndexHeading, "", MCPText.meetingsIndexNotice, ""]
        let meetings = context.visibleMeetings.prefix(Self.indexedMeetingCount)
        if meetings.isEmpty {
            lines.append(MCPText.meetingsIndexEmpty)
        }
        for archived in meetings {
            let view = MCPMeetingView(archived, language: context.language)
            var speakers = view.speakers.prefix(Self.indexedSpeakersPerMeeting).map { view.label(for: $0) }
            if view.speakers.count > Self.indexedSpeakersPerMeeting {
                speakers.append("…")
            }
            let source = archived.record
            lines.append(MCPText.indexLine(
                created: context.dates.iso(source.createdAt), duration: TranscriptMerger.timestamp(source.duration),
                title: view.title, status: source.status.rawValue,
                speakers: speakers.isEmpty ? MCPText.noSpeakers : speakers.joined(separator: ", "),
                moments: source.highlights.count, shortID: view.shortID))
        }
        return lines.joined(separator: "\n") + "\n"
    }

    // MARK: - Prompts

    func getPrompt(params: [String: JSONValue], context: MCPRequestContext) throws -> JSONValue {
        guard let name = params["name"]?.stringValue, let kind = MCPPromptKind(rawValue: name) else {
            throw MCPRPCError.invalidParams("Unknown prompt")
        }
        let language = context.language
        guard context.access.enabled else { throw MCPRPCError.invalidParams(MCPMessages.disabled(language)) }
        if let wait = consumeRateLimit() {
            throw MCPRPCError(code: -32603, message: MCPMessages.tooManyRequests(wait, language))
        }

        let arguments = try promptArguments(params["arguments"], kind: kind, language: language)
        // Clients may send an empty string for an optional argument the user left out.
        let reference = arguments["meeting_id"].flatMap { $0.isEmpty ? nil : $0 } ?? "latest"
        if let problem = validator.problem(with: .string(reference), schema: MCPSchemas.meetingIDSchema,
                                           path: "meeting_id") {
            throw MCPRPCError.invalidParams(MCPMessages.badArgument(problem, language))
        }
        let detail = arguments["detail"].flatMap { $0.isEmpty ? nil : $0 } ?? "short"
        guard detail == "short" || detail == "full" else {
            throw MCPRPCError.invalidParams(MCPMessages.badArgument("detail: use short or full", language))
        }

        let archived: ArchivedMeeting
        switch resolveMeeting(reference, context: context) {
        case let .found(found, _): archived = found
        case let .failure(message): throw MCPRPCError.invalidParams(message)
        }
        switch archived.record.status {
        case .recording, .transcribing: throw MCPRPCError.invalidParams(MCPMessages.stillProcessing(language))
        case .recorded, .failed: throw MCPRPCError.invalidParams(MCPMessages.noTranscript(language))
        case .transcribed: break
        }

        let view = MCPMeetingView(archived, language: language)
        let template: String
        switch kind {
        case .tasks: template = MCPMessages.tasksPrompt(language)
        case .summary: template = detail == "full" ? MCPMessages.fullSummaryPrompt(language)
                                                   : MCPMessages.shortSummaryPrompt(language)
        }
        let speakers = view.speakers.prefix(Self.listedSpeakersPerMeeting).map { speaker -> String in
            speaker.isMe ? "\(view.label(for: speaker)) (\(MCPMessages.slovoUser(language)))" : view.label(for: speaker)
        }
        let instruction = MCPText.fill(template, [
            "title": view.title,
            "date": context.dates.localized(archived.record.createdAt, language: language),
            "duration": TranscriptMerger.timestamp(archived.record.duration),
            "speakers": speakers.joined(separator: ", ")
        ])

        let document = resourceDocument(view, context: context)
        let attachment: JSONValue
        if document.utf16.count <= Self.embeddedDocumentCharacterLimit {
            let resource: JSONValue = [
                "uri": .string(MCPText.transcriptURI(view.id)), "mimeType": .string(MCPText.markdownMimeType),
                "text": .string(document)
            ]
            attachment = ["type": "resource", "resource": resource]
        } else {
            let paging = MCPText.fill(MCPMessages.pagingPrompt(language), ["meeting_id": view.id])
            attachment = ["type": "text", "text": .string(paging)]
        }
        let first: JSONValue = ["role": "user", "content": ["type": "text", "text": .string(instruction)]]
        let second: JSONValue = ["role": "user", "content": attachment]
        return .object(["description": .string(kind.description), "messages": .array([first, second])])
    }

    private func promptArguments(_ raw: JSONValue?, kind: MCPPromptKind, language: AppLanguage) throws
        -> [String: String] {
        let object: [String: JSONValue]
        switch raw {
        case .none, .some(.null): return [:]
        case let .some(.object(value)): object = value
        case .some: throw MCPRPCError.invalidParams(MCPMessages.badArgument("arguments: must be an object", language))
        }
        let allowed: Set<String> = kind == .tasks ? ["meeting_id"] : ["meeting_id", "detail"]
        var arguments: [String: String] = [:]
        for key in object.keys.sorted() {
            guard allowed.contains(key) else {
                let detail = "\(MCPSchemaValidator.displayName(key)): unknown argument"
                throw MCPRPCError.invalidParams(MCPMessages.badArgument(detail, language))
            }
            guard let text = object[key]?.stringValue else {
                throw MCPRPCError.invalidParams(MCPMessages.badArgument("\(key): must be a string", language))
            }
            arguments[key] = text
        }
        return arguments
    }
}
