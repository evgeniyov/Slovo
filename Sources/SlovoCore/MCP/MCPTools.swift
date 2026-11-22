import Foundation

/// A tool answer with `isError: true` and a localized, actionable message.
struct MCPToolFailure: Error {
    let message: String

    init(_ message: String) {
        self.message = message
    }
}

enum MCPToolOutcome: Equatable {
    case structured(JSONValue)
    case text(String)
    case failure(String)

    var result: JSONValue {
        switch self {
        case let .structured(value):
            // The same JSON in both fields: clients differ in which one they read, and
            // one reported client drops the text when structured content is present.
            return .object(["content": Self.textContent(value.serializedString), "structuredContent": value])
        case let .text(text):
            return .object(["content": Self.textContent(text)])
        case let .failure(message):
            return .object(["content": Self.textContent(message), "isError": .bool(true)])
        }
    }

    private static func textContent(_ text: String) -> JSONValue {
        .array([.object(["type": .string("text"), "text": .string(text)])])
    }
}

extension MCPHandler {
    enum MeetingResolution {
        case found(ArchivedMeeting, note: String?)
        case failure(String)
    }

    static let listedSpeakersPerMeeting = 30
    static let detailedSpeakersPerMeeting = 200
    static let ambiguousMatchesShown = 10

    /// Access, then the rate limit, then argument validation, all before any meeting is read.
    func runTool(named name: String, arguments rawArguments: JSONValue?, context: MCPRequestContext) throws
        -> MCPToolOutcome {
        guard context.access.enabled else { return .failure(MCPMessages.disabled(context.language)) }
        if let wait = consumeRateLimit() {
            return .failure(MCPMessages.tooManyRequests(wait, context.language))
        }
        let arguments: [String: JSONValue]
        switch rawArguments {
        case .none, .some(.null): arguments = [:]
        case let .some(.object(object)): arguments = object
        case .some: return .failure(MCPMessages.badArgument("arguments: must be an object", context.language))
        }
        if let problem = validator.problem(with: .object(arguments), schema: MCPSchemas.inputSchema(for: name)) {
            return .failure(MCPMessages.badArgument(problem, context.language))
        }
        do {
            switch name {
            case MCPText.listMeetings.name: return try listMeetings(arguments, context: context)
            case MCPText.getMeeting.name: return try getMeeting(arguments, context: context)
            case MCPText.getTranscript.name: return try getTranscript(arguments, context: context)
            case MCPText.searchMeetings.name: return try searchMeetings(arguments, context: context)
            case MCPText.listHighlights.name: return try listHighlights(arguments, context: context)
            case MCPText.markHighlight.name: return try markHighlight(arguments, context: context)
            case MCPText.unmarkHighlight.name: return try unmarkHighlight(arguments, context: context)
            case MCPText.writeMeetingNotes.name: return try writeMeetingNotes(arguments, context: context)
            default: throw MCPRPCError.invalidParams("Unknown tool")
            }
        } catch let failure as MCPToolFailure {
            return .failure(failure.message)
        }
    }

    // MARK: - list_meetings

    func listMeetings(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let language = context.language
        let range = try dateRange(arguments, context: context)
        let query = arguments["query"]?.stringValue
        let words = try query.map { try searchWords($0, argument: "query", context: context) }
        let limit = arguments["limit"]?.intValue ?? 20
        // A cursor only continues the listing it was made for.
        let filter = MCPHash.hex(["list_meetings", query ?? "", arguments["since"]?.stringValue ?? "",
                                  arguments["until"]?.stringValue ?? ""].joined(separator: "\u{1F}"))

        var candidates = context.visibleMeetings.filter { range.contains($0.record.createdAt) }
        if let words {
            candidates = candidates.filter { archived in
                MCPSearch.matches(MCPSearch.normalized(MCPSanitizer.sanitize(archived.record.title)), words: words)
            }
        }
        if let cursorText = arguments["cursor"]?.stringValue {
            guard let cursor = MCPListCursor(encoded: cursorText), cursor.filter == filter else {
                throw MCPToolFailure(MCPMessages.badCursor(language))
            }
            candidates = candidates.filter(cursor.precedes)
        }

        let page = Array(candidates.prefix(limit))
        let items = page.map { meetingSummary(MCPMeetingView($0, language: language), context: context) }
        let result = fitted(count: items.count) { count -> JSONValue in
            var fields: [String: JSONValue] = [
                "now": .string(context.dates.iso(context.now)),
                "time_zone": .string(context.dates.timeZone.identifier),
                "meetings": .array(Array(items.prefix(count)))
            ]
            if count > 0, count < candidates.count {
                fields["next_cursor"] = .string(MCPListCursor(after: page[count - 1], filter: filter).encoded)
            }
            if candidates.isEmpty {
                fields["message"] = .string(MCPMessages.noMeetings(language))
            }
            return .object(fields)
        }
        return .structured(result)
    }

    func meetingSummary(_ view: MCPMeetingView, context: MCPRequestContext) -> JSONValue {
        let source = view.archived.record
        let speakers = view.speakers.prefix(Self.listedSpeakersPerMeeting).map { speaker -> JSONValue in
            .object(["id": .string(speaker.id), "name": .string(view.label(for: speaker)), "is_me": .bool(speaker.isMe)])
        }
        var fields: [String: JSONValue] = [
            "id": .string(view.id),
            "short_id": .string(view.shortID),
            "title": .string(view.title),
            "created_at": .string(context.dates.iso(source.createdAt)),
            "duration_seconds": .seconds(source.duration),
            "duration_text": .string(TranscriptMerger.timestamp(source.duration)),
            "status": .string(source.status.rawValue),
            "has_transcript": .bool(source.status == .transcribed),
            "speakers": .array(speakers),
            "highlight_count": .integer(source.highlights.count),
            "engine": .optional(source.engineID?.rawValue),
            "speaker_separation": .string(source.diarization?.state.rawValue ?? "none")
        ]
        if view.speakers.count > Self.listedSpeakersPerMeeting {
            fields["speakers_truncated"] = .bool(true)
        }
        return .object(fields)
    }

    // MARK: - get_meeting

    func getMeeting(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let reference = arguments["meeting_id"]?.stringValue ?? "latest"
        let includeHighlights = arguments["include_highlights"]?.boolValue ?? true
        let (archived, latestNote) = try resolved(reference, context: context)
        let view = MCPMeetingView(archived, language: context.language)
        let source = archived.record

        let meeting: JSONValue = .object([
            "id": .string(view.id),
            "short_id": .string(view.shortID),
            "title": .string(view.title),
            "created_at": .string(context.dates.iso(source.createdAt)),
            "duration_seconds": .seconds(source.duration),
            "duration_text": .string(TranscriptMerger.timestamp(source.duration)),
            "status": .string(source.status.rawValue),
            "engine": .optional(source.engineID?.rawValue),
            "speaker_separation": .string(source.diarization?.state.rawValue ?? "none"),
            "has_system_audio": .bool(source.hasSystemAudio)
        ])
        let speakers = view.speakers.prefix(Self.detailedSpeakersPerMeeting).map { speaker -> JSONValue in
            .object([
                "id": .string(speaker.id), "name": .string(view.label(for: speaker)), "is_me": .bool(speaker.isMe),
                "source": .string(speaker.track.rawValue), "talk_seconds": .seconds(source.talkTime(of: speaker.id))
            ])
        }
        let highlights = includeHighlights ? view.highlights.map { JSONValue.object(highlightFields($0, view: view)) } : []
        let transcript = source.status == .transcribed ? transcriptSummary(view, context: context) : JSONValue.null
        let statusNote = MCPText.statusNote(source.status)

        let result = fitted(count: highlights.count) { count -> JSONValue in
            var fields: [String: JSONValue] = ["meeting": meeting, "speakers": .array(speakers), "transcript": transcript]
            var notes = [latestNote, statusNote].compactMap { $0 }
            if includeHighlights {
                fields["highlights"] = .array(Array(highlights.prefix(count)))
                if count < highlights.count {
                    fields["highlights_truncated"] = .bool(true)
                    notes.append(MCPText.highlightsTruncated)
                }
            }
            if view.speakers.count > Self.detailedSpeakersPerMeeting {
                fields["speakers_truncated"] = .bool(true)
            }
            if !notes.isEmpty {
                fields["note"] = .string(notes.joined(separator: " "))
            }
            return .object(fields)
        }
        return .structured(result)
    }

    func highlightFields(_ highlight: MeetingHighlight, view: MCPMeetingView) -> [String: JSONValue] {
        let line = view.line(of: highlight)
        return [
            "id": .string(highlight.id.uuidString.lowercased()),
            "time_seconds": .seconds(highlight.time),
            "time_text": .string(TranscriptMerger.timestamp(highlight.time)),
            "note": .optional(highlight.note),
            "speaker_id": .optional(line?.speakerID),
            "speaker": .optional(line.map { view.label(forSpeaker: $0.speakerID) }),
            "line": .optional(line.map { MCPMeetingView.capped($0.text, MCPMeetingView.maximumLineExcerptCharacters) })
        ]
    }

    func transcriptSummary(_ view: MCPMeetingView, context: MCPRequestContext) -> JSONValue {
        let selection = MCPTranscriptSelection(maxChars: MCPTranscriptSelection.defaultMaxChars)
        let rendered = MCPTranscriptPager.render(view, selection: selection)
        let characters = rendered.reduce(0) { $0 + $1.text.utf16.count } + max(0, rendered.count - 1)
        let pages = MCPTranscriptPager.pageCount(view: view, selection: selection, rendered: rendered,
                                                 dates: context.dates)
        return .object([
            "lines": .integer(rendered.count), "characters": .integer(characters),
            "pages_at_default_size": .integer(pages)
        ])
    }

    // MARK: - get_transcript

    func getTranscript(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let language = context.language
        let reference = arguments["meeting_id"]?.stringValue ?? "latest"
        let from = try arguments["from"].map { try timelinePosition($0, argument: "from", context: context) }
        let to = try arguments["to"].map { try timelinePosition($0, argument: "to", context: context) }
        if let from, let to, from >= to {
            throw badArgument("from: must be earlier than to", context: context)
        }
        let speakerIDs = arguments["speaker_ids"]?.arrayValue.map { $0.compactMap(\.stringValue).sorted() }
        let maxChars = arguments["max_chars"]?.intValue ?? MCPTranscriptSelection.defaultMaxChars

        let (archived, _) = try resolved(reference, context: context)
        switch archived.record.status {
        case .recording, .transcribing: throw MCPToolFailure(MCPMessages.stillProcessing(language))
        case .recorded, .failed: throw MCPToolFailure(MCPMessages.noTranscript(language))
        case .transcribed: break
        }
        let view = MCPMeetingView(archived, language: language)
        if let speakerIDs {
            let known = view.speakers.map(\.id)
            if let unknown = speakerIDs.first(where: { !known.contains($0) }) {
                throw badArgument("speaker_ids: this meeting has no speaker \(unknown); its speakers are "
                                  + known.joined(separator: ", "), context: context)
            }
        }

        let selection = MCPTranscriptSelection(speakerIDs: speakerIDs, from: from, to: to, maxChars: maxChars)
        let rendered = MCPTranscriptPager.render(view, selection: selection)
        var start = 0
        var offset = 0
        var changed = false
        if let cursorText = arguments["cursor"]?.stringValue {
            guard let cursor = MCPTranscriptCursor(encoded: cursorText), cursor.meeting == view.id,
                  cursor.filter == selection.filterHash else {
                throw MCPToolFailure(MCPMessages.badCursor(language))
            }
            if cursor.fingerprint == view.fingerprint {
                let valid = cursor.index < rendered.count
                    ? cursor.offset < rendered[cursor.index].text.utf16.count
                    : cursor.index == rendered.count && cursor.offset == 0
                guard valid else { throw MCPToolFailure(MCPMessages.badCursor(language)) }
                start = cursor.index
                offset = cursor.offset
            } else {
                // The meeting was saved again, for example after a rename: continue from the
                // same position and say that lines may have shifted.
                changed = true
                start = min(cursor.index, rendered.count)
                offset = start < rendered.count ? cursor.offset : 0
                if start < rendered.count, offset >= rendered[start].text.utf16.count {
                    start += 1
                    offset = 0
                }
            }
        }
        let page = MCPTranscriptPager.page(view: view, selection: selection, rendered: rendered, start: start,
                                           offset: offset, changed: changed, dates: context.dates)
        return .text(page.text)
    }

    private func timelinePosition(_ value: JSONValue, argument: String, context: MCPRequestContext) throws
        -> TimeInterval {
        guard let seconds = MCPTranscriptPager.timelinePosition(value) else {
            throw badArgument("\(argument): \(MCPText.timelineFormatHint)", context: context)
        }
        return seconds
    }

    // MARK: - search_meetings

    func searchMeetings(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let language = context.language
        let words = try searchWords(arguments["query"]?.stringValue ?? "", argument: "query", context: context)
        let range = try dateRange(arguments, context: context)
        let limit = arguments["limit"]?.intValue ?? 20
        let scope = try meetingScope(arguments, range: range, context: context)

        var hits: [JSONValue] = []
        var nextStep: String?
        var truncated = false
        search: for archived in scope {
            let view = MCPMeetingView(archived, language: language)
            let common: [String: JSONValue] = [
                "meeting_id": .string(view.id), "short_id": .string(view.shortID), "title": .string(view.title),
                "created_at": .string(context.dates.iso(archived.record.createdAt))
            ]
            // Ordered within the meeting: a title hit first, then by line.
            var found: [(order: Int, time: TimeInterval?, fields: [String: JSONValue])] = []
            if MCPSearch.matches(MCPSearch.normalized(MCPSanitizer.sanitize(archived.record.title)), words: words) {
                var fields = common
                fields["time_seconds"] = .null
                fields["time_text"] = .null
                fields["speaker_id"] = .null
                fields["speaker"] = .null
                fields["snippet"] = .string(view.title)
                found.append((-1, nil, fields))
            }
            var hitLines: Set<Int> = []
            for (index, line) in view.lines.enumerated()
            where MCPSearch.matches(MCPSearch.normalized(line.text), words: words) {
                hitLines.insert(index)
                let snippet = MCPSearch.snippet(line.text, words: words)
                found.append((index, line.start, lineHit(common, line: line, view: view, snippet: snippet)))
            }
            for speaker in view.speakers
            where MCPSearch.matches(MCPSearch.normalized(view.label(for: speaker)), words: words) {
                guard let index = view.lines.firstIndex(where: { $0.speakerID == speaker.id }),
                      !hitLines.contains(index) else { continue }
                hitLines.insert(index)
                let line = view.lines[index]
                let snippet = MCPMeetingView.capped(line.text, MCPMeetingView.maximumLineExcerptCharacters)
                found.append((index, line.start, lineHit(common, line: line, view: view, snippet: snippet)))
            }
            found.sort { $0.order < $1.order }
            for hit in found {
                guard hits.count < limit else {
                    truncated = true
                    break search
                }
                if nextStep == nil {
                    nextStep = step(for: view, time: hit.time, context: context)
                }
                hits.append(.object(hit.fields))
            }
        }

        let result = fitted(count: hits.count) { count -> JSONValue in
            var fields: [String: JSONValue] = [
                "hits": .array(Array(hits.prefix(count))), "truncated": .bool(truncated || count < hits.count)
            ]
            if count > 0, let nextStep {
                fields["next_step"] = .string(nextStep)
            }
            if hits.isEmpty {
                fields["message"] = .string(MCPMessages.noMeetings(language))
            }
            return .object(fields)
        }
        return .structured(result)
    }

    private func lineHit(_ common: [String: JSONValue], line: TranscriptLine, view: MCPMeetingView,
                         snippet: String) -> [String: JSONValue] {
        var fields = common
        fields["time_seconds"] = .seconds(line.start)
        fields["time_text"] = .string(TranscriptMerger.timestamp(line.start))
        fields["speaker_id"] = .string(line.speakerID)
        fields["speaker"] = .string(view.label(forSpeaker: line.speakerID))
        fields["snippet"] = .string(snippet)
        return fields
    }

    /// What to call next for the first hit: its transcript around the hit, with the short
    /// id when it is unambiguous.
    private func step(for view: MCPMeetingView, time: TimeInterval?, context: MCPRequestContext) -> String {
        let sharing = context.visibleMeetings.filter { $0.record.id.uuidString.lowercased().hasPrefix(view.shortID) }
        let reference = sharing.count == 1 ? view.shortID : view.id
        guard let time else { return "get_meeting meeting_id=\(reference)" }
        return "get_transcript meeting_id=\(reference) from=\(TranscriptMerger.timestamp(max(0, time - 30))) "
            + "to=\(TranscriptMerger.timestamp(time + 60))"
    }

    // MARK: - list_highlights

    func listHighlights(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let range = try dateRange(arguments, context: context)
        let limit = arguments["limit"]?.intValue ?? 50
        let scope = try meetingScope(arguments, range: range, context: context)

        var items: [JSONValue] = []
        var truncated = false
        collect: for archived in scope where !archived.record.highlights.isEmpty {
            let view = MCPMeetingView(archived, language: context.language)
            for highlight in view.highlights {
                guard items.count < limit else {
                    truncated = true
                    break collect
                }
                var fields = highlightFields(highlight, view: view)
                fields["meeting_id"] = .string(view.id)
                fields["short_id"] = .string(view.shortID)
                fields["meeting_title"] = .string(view.title)
                fields["source"] = .string(highlight.source.rawValue)
                items.append(.object(fields))
            }
        }
        let result = fitted(count: items.count) { count -> JSONValue in
            .object([
                "highlights": .array(Array(items.prefix(count))), "truncated": .bool(truncated || count < items.count)
            ])
        }
        return .structured(result)
    }

    // MARK: - mark_highlight

    /// Marks a moment in a transcribed meeting as important, the way a star on a transcript
    /// line does. Idempotent: marking the same time and note again finds the highlight that
    /// call already made and changes nothing.
    func markHighlight(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let language = context.language
        guard context.access.canWrite else { return .failure(MCPMessages.writeDisabled(language)) }
        guard let writer = highlights else { return .failure(MCPMessages.writeFailed(language)) }

        let reference = arguments["meeting_id"]?.stringValue ?? "latest"
        // The schema requires this argument, so it is always present and of the right type.
        let time = try timelinePosition(arguments["time"] ?? .null, argument: "time", context: context)
        let note = try arguments["note"].map {
            try checkedNotesText($0, argument: "note", limit: MeetingEdits.maximumNoteCharacters, context: context)
        }

        let (archived, _) = try resolved(reference, context: context)
        switch archived.record.status {
        case .recording, .transcribing: throw MCPToolFailure(MCPMessages.stillProcessing(language))
        case .recorded, .failed: throw MCPToolFailure(MCPMessages.noTranscript(language))
        case .transcribed: break
        }

        let result: HighlightMarkResult
        do {
            result = try writer.mark(time: time, note: note, on: archived.record.id, now: context.now)
        } catch {
            throw MCPToolFailure(MCPMessages.writeFailed(language))
        }

        let view = MCPMeetingView(ArchivedMeeting(record: result.record, recordModified: context.now, recordSize: 0),
                                  language: language)
        return .structured([
            "meeting_id": .string(view.id), "short_id": .string(view.shortID), "title": .string(view.title),
            "highlight": .object(highlightFields(result.highlight, view: view)), "created": .bool(result.created),
            "note": .string(MCPText.highlightMarked(created: result.created))
        ])
    }

    // MARK: - unmark_highlight

    /// Removes one important moment by its id. Idempotent: an id that is already gone
    /// changes nothing.
    func unmarkHighlight(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let language = context.language
        guard context.access.canWrite else { return .failure(MCPMessages.writeDisabled(language)) }
        guard let writer = highlights else { return .failure(MCPMessages.writeFailed(language)) }

        let reference = arguments["meeting_id"]?.stringValue ?? "latest"
        guard let idText = arguments["highlight_id"]?.stringValue, let highlightID = UUID(uuidString: idText) else {
            throw badArgument("highlight_id: \(MCPText.highlightIDFormatHint)", context: context)
        }

        let (archived, _) = try resolved(reference, context: context)
        switch archived.record.status {
        case .recording, .transcribing: throw MCPToolFailure(MCPMessages.stillProcessing(language))
        case .recorded, .failed: throw MCPToolFailure(MCPMessages.noTranscript(language))
        case .transcribed: break
        }

        let result: HighlightUnmarkResult
        do {
            result = try writer.unmark(highlightID, on: archived.record.id)
        } catch {
            throw MCPToolFailure(MCPMessages.writeFailed(language))
        }

        let view = MCPMeetingView(ArchivedMeeting(record: result.record, recordModified: context.now, recordSize: 0),
                                  language: language)
        return .structured([
            "meeting_id": .string(view.id), "short_id": .string(view.shortID), "title": .string(view.title),
            "highlight_id": .string(highlightID.uuidString.lowercased()), "removed": .bool(result.removed),
            "note": .string(MCPText.highlightUnmarked(removed: result.removed))
        ])
    }

    // MARK: - write_meeting_notes

    /// The one tool that changes a meeting's notes.
    ///
    /// It needs its own switch, and writes through `MeetingNotesWriting`, which reads the
    /// record again immediately before replacing it, so nothing the app saved meanwhile is
    /// lost. It always replaces: running it twice leaves one set of notes, not two. Nothing
    /// it writes reaches the segments, so the transcript stays what was said.
    func writeMeetingNotes(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPToolOutcome {
        let language = context.language
        guard context.access.canWrite else { return .failure(MCPMessages.writeDisabled(language)) }
        guard let writer = notes else { return .failure(MCPMessages.writeFailed(language)) }

        let reference = arguments["meeting_id"]?.stringValue ?? "latest"
        let summary = try arguments["summary"].map {
            try checkedNotesText($0, argument: "summary", limit: MeetingNotes.maximumSummaryCharacters,
                                 context: context)
        }
        let decisions = try checkedNotesItems(arguments["decisions"], argument: "decisions", context: context)
        let tasks = try checkedNotesItems(arguments["tasks"], argument: "tasks", context: context)
        guard summary != nil || !decisions.isEmpty || !tasks.isEmpty else {
            throw badArgument("summary: write a summary, decisions or tasks", context: context)
        }

        let (archived, _) = try resolved(reference, context: context)
        // Notes about a meeting with no transcript would be about nothing.
        switch archived.record.status {
        case .recording, .transcribing: throw MCPToolFailure(MCPMessages.stillProcessing(language))
        case .recorded, .failed: throw MCPToolFailure(MCPMessages.noTranscript(language))
        case .transcribed: break
        }

        let replaced = archived.record.claudeNotes != nil
        let written = MeetingNotes(summary: summary ?? "", decisions: decisions, tasks: tasks,
                                   author: MeetingNotes.defaultAuthor, writtenAt: context.now)
        // What the file holds afterwards is reported, so the date is the one the user will
        // see: notes that repeat what the meeting already holds keep their first date.
        let stored: MeetingNotes
        do {
            stored = try writer.write(written, to: archived.record.id) ?? written
        } catch {
            throw MCPToolFailure(MCPMessages.writeFailed(language))
        }

        let view = MCPMeetingView(archived, language: language)
        return .structured([
            "meeting_id": .string(view.id), "short_id": .string(view.shortID), "title": .string(view.title),
            "author": .string(stored.author), "written_at": .string(context.dates.iso(stored.writtenAt)),
            "summary_written": .bool(!stored.summary.isEmpty), "decisions": .integer(stored.decisions.count),
            "tasks": .integer(stored.tasks.count), "replaced": .bool(replaced),
            "note": .string(MCPText.notesWritten(replaced: replaced))
        ])
    }

    /// Text Claude writes into a meeting is held to the rules Slovo's own output follows:
    /// one line, no control characters, no transcript tag, and nothing the recovery parser
    /// of `transcript.md` would read back as a spoken line or a marked moment.
    ///
    /// Bad text is refused, never repaired: quietly rewriting what Claude wrote would put
    /// words in its mouth, and a refusal says what to fix.
    private func checkedNotesText(_ value: JSONValue, argument: String, limit: Int,
                                  context: MCPRequestContext) throws -> String {
        guard let text = value.stringValue else {
            throw badArgument("\(argument): must be a string", context: context)
        }
        guard !text.unicodeScalars.contains(where: MCPSanitizer.isControl) else {
            throw badArgument("\(argument): must be one line, without control characters", context: context)
        }
        guard MCPSanitizer.sanitize(text) == text else {
            let invisible = text.unicodeScalars.contains(where: MCPSanitizer.isInvisible)
            throw badArgument("\(argument): " + (invisible ? "must not contain invisible characters"
                                                           : "must not contain a transcript tag"), context: context)
        }
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else {
            throw badArgument("\(argument): must not be empty", context: context)
        }
        guard trimmed.count <= limit else {
            throw badArgument("\(argument): must be at most \(limit) characters long", context: context)
        }
        guard !trimmed.hasPrefix("["), !trimmed.contains(TranscriptMerger.highlightMark) else {
            throw badArgument("\(argument): must not start with \"[\" or contain "
                              + "\(TranscriptMerger.highlightMark), which mark lines of a transcript", context: context)
        }
        return trimmed
    }

    private func checkedNotesItems(_ value: JSONValue?, argument: String,
                                   context: MCPRequestContext) throws -> [String] {
        guard let elements = value?.arrayValue else { return [] }
        guard elements.count <= MeetingNotes.maximumItems else {
            throw badArgument("\(argument): must have at most \(MeetingNotes.maximumItems) items", context: context)
        }
        return try elements.enumerated().map { index, element in
            try checkedNotesText(element, argument: "\(argument)[\(index)]",
                                 limit: MeetingNotes.maximumItemCharacters, context: context)
        }
    }

    // MARK: - Shared argument handling

    func resolveMeeting(_ reference: String, context: MCPRequestContext) -> MeetingResolution {
        let language = context.language
        let visible = context.visibleMeetings
        if reference == "latest" {
            guard let index = visible.firstIndex(where: { $0.record.status == .transcribed }) else {
                if visible.contains(where: { Self.isInProgress($0.record.status) }) {
                    return .failure(MCPMessages.stillProcessing(language))
                }
                return .failure(visible.isEmpty ? MCPMessages.noMeetings(language)
                                                : MCPMessages.notFound(reference, language))
            }
            let note = visible[..<index].first(where: { Self.isInProgress($0.record.status) }).map { newer in
                MCPText.newerMeetingInProgress(title: MCPMeetingView(newer, language: language).title,
                                               created: context.dates.iso(newer.record.createdAt))
            }
            return .found(visible[index], note: note)
        }

        let lowered = reference.lowercased()
        if lowered.count == 36, let id = UUID(uuidString: lowered) {
            if let match = visible.first(where: { $0.record.id == id }) {
                return .found(match, note: nil)
            }
            if let created = archive.unreadableMeetingDate(id: id), context.isVisible(createdAt: created) {
                return .failure(MCPMessages.damaged(language))
            }
            return .failure(MCPMessages.notFound(reference, language))
        }
        let matches = visible.filter { archived in
            let full = archived.record.id.uuidString.lowercased()
            return lowered.contains("-")
                ? full.hasPrefix(lowered)
                : full.replacingOccurrences(of: "-", with: "").hasPrefix(lowered)
        }
        if matches.count == 1, let only = matches.first {
            return .found(only, note: nil)
        }
        guard !matches.isEmpty else { return .failure(MCPMessages.notFound(reference, language)) }
        let listed = matches.prefix(Self.ambiguousMatchesShown).map { archived -> String in
            let view = MCPMeetingView(archived, language: language)
            return "- \(view.id) «\(view.title)» \(context.dates.iso(archived.record.createdAt))"
        }
        return .failure(([MCPMessages.ambiguous(language)] + listed).joined(separator: "\n"))
    }

    func resolved(_ reference: String, context: MCPRequestContext) throws -> (ArchivedMeeting, String?) {
        switch resolveMeeting(reference, context: context) {
        case let .found(archived, note): return (archived, note)
        case let .failure(message): throw MCPToolFailure(message)
        }
    }

    static func isInProgress(_ status: MeetingStatus) -> Bool {
        status == .recording || status == .transcribing
    }

    /// One meeting when `meeting_id` is given, otherwise every visible meeting; both
    /// limited to the date range.
    func meetingScope(_ arguments: [String: JSONValue], range: MCPDateRange, context: MCPRequestContext) throws
        -> [ArchivedMeeting] {
        if let reference = arguments["meeting_id"]?.stringValue {
            let (archived, _) = try resolved(reference, context: context)
            return range.contains(archived.record.createdAt) ? [archived] : []
        }
        return context.visibleMeetings.filter { range.contains($0.record.createdAt) }
    }

    func dateRange(_ arguments: [String: JSONValue], context: MCPRequestContext) throws -> MCPDateRange {
        var range = MCPDateRange()
        for name in ["since", "until"] {
            guard let text = arguments[name]?.stringValue else { continue }
            guard let date = context.dates.parse(text) else {
                throw badArgument("\(name): \(MCPText.dateFormatHint)", context: context)
            }
            if name == "since" {
                range.since = date
            } else {
                range.until = date
            }
        }
        if let since = range.since, let until = range.until, since >= until {
            throw badArgument("since: must be earlier than until", context: context)
        }
        return range
    }

    func searchWords(_ query: String, argument: String, context: MCPRequestContext) throws -> [String] {
        let words = MCPSearch.words(query)
        guard !words.isEmpty else {
            throw badArgument("\(argument): must contain at least one word", context: context)
        }
        return words
    }

    func badArgument(_ detail: String, context: MCPRequestContext) -> MCPToolFailure {
        MCPToolFailure(MCPMessages.badArgument(detail, context.language))
    }

    /// The result built with as many of `count` items as keep its text within 48,000
    /// characters, measured in UTF-16 units as Claude's clients count string length.
    func fitted(count: Int, build: (Int) -> JSONValue) -> JSONValue {
        let whole = build(count)
        guard whole.serializedString.utf16.count > Self.maximumResultCharacters else { return whole }
        var best = build(0)
        var low = 1
        var high = count - 1
        while low <= high {
            let middle = (low + high) / 2
            let candidate = build(middle)
            if candidate.serializedString.utf16.count <= Self.maximumResultCharacters {
                best = candidate
                low = middle + 1
            } else {
                high = middle - 1
            }
        }
        return best
    }
}
