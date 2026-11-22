import Foundation

/// What Claude sees in `tools/list`, `prompts/list` and `resources/templates/list`.
///
/// Tool arguments are validated against these same schemas, so what is declared and
/// what is enforced cannot drift apart.
enum MCPSchemas {
    static let datePattern =
        #"^(today|yesterday|[1-9][0-9]{0,2}d|[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?(Z|[+-][0-9]{2}:[0-9]{2})?)?)$"#
    static let meetingIDPattern = "^(latest|[0-9A-Fa-f][0-9A-Fa-f-]{7,35})$"
    static let speakerIDPattern = "^(me|others|s[0-9]{1,3})$"
    static let highlightIDPattern = "^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$"
    /// Claude Code keeps tool results up to this many characters inline.
    static let maximumResultSizeCharacters = 50_000

    static let meetingIDSchema: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern)]

    static let listMeetingsSchema: JSONValue = {
        let query: JSONValue = [
            "type": "string", "minLength": 1, "maxLength": 200,
            "description": "Words that must all appear in the title; case-insensitive, ё matches е."
        ]
        let since: JSONValue = [
            "type": "string", "pattern": .string(datePattern),
            "description": "Start (inclusive): today, yesterday, 7d, 2026-09-01 or an ISO 8601 date-time in local time."
        ]
        let until: JSONValue = [
            "type": "string", "pattern": .string(datePattern),
            "description": "End (exclusive), same formats as since."
        ]
        let limit: JSONValue = ["type": "integer", "minimum": 1, "maximum": 100, "default": 20]
        let cursor: JSONValue = [
            "type": "string", "maxLength": 512, "description": "next_cursor from a previous call."
        ]
        return [
            "type": "object",
            "properties": ["query": query, "since": since, "until": until, "limit": limit, "cursor": cursor],
            "additionalProperties": false
        ]
    }()

    static let getMeetingSchema: JSONValue = {
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern), "default": "latest"]
        let includeHighlights: JSONValue = ["type": "boolean", "default": true]
        return [
            "type": "object",
            "properties": ["meeting_id": meetingID, "include_highlights": includeHighlights],
            "additionalProperties": false
        ]
    }()

    static let getTranscriptSchema: JSONValue = {
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern)]
        let speakerIDs: JSONValue = [
            "type": "array", "items": ["type": "string", "pattern": .string(speakerIDPattern)],
            "minItems": 1, "maxItems": 20, "uniqueItems": true, "description": "Speaker ids from get_meeting."
        ]
        let from: JSONValue = [
            "type": ["string", "number"], "description": "Start of the range: seconds, mm:ss or h:mm:ss."
        ]
        let to: JSONValue = [
            "type": ["string", "number"], "description": "End of the range (exclusive): seconds, mm:ss or h:mm:ss."
        ]
        let maxChars: JSONValue = ["type": "integer", "minimum": 2000, "maximum": 48000, "default": 16000]
        let cursor: JSONValue = ["type": "string", "maxLength": 512]
        return [
            "type": "object",
            "properties": [
                "meeting_id": meetingID, "speaker_ids": speakerIDs, "from": from, "to": to,
                "max_chars": maxChars, "cursor": cursor
            ],
            "required": ["meeting_id"],
            "additionalProperties": false
        ]
    }()

    static let searchMeetingsSchema: JSONValue = {
        let query: JSONValue = [
            "type": "string", "minLength": 2, "maxLength": 200,
            "description": "Case-insensitive; ё matches е; all words must appear in one line."
        ]
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern)]
        let date: JSONValue = ["type": "string", "pattern": .string(datePattern)]
        let limit: JSONValue = ["type": "integer", "minimum": 1, "maximum": 50, "default": 20]
        return [
            "type": "object",
            "properties": ["query": query, "meeting_id": meetingID, "since": date, "until": date, "limit": limit],
            "required": ["query"],
            "additionalProperties": false
        ]
    }()

    static let listHighlightsSchema: JSONValue = {
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern)]
        let date: JSONValue = ["type": "string", "pattern": .string(datePattern)]
        let limit: JSONValue = ["type": "integer", "minimum": 1, "maximum": 200, "default": 50]
        return [
            "type": "object",
            "properties": ["meeting_id": meetingID, "since": date, "until": date, "limit": limit],
            "additionalProperties": false
        ]
    }()

    static let markHighlightSchema: JSONValue = {
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern), "default": "latest"]
        let time: JSONValue = [
            "type": ["string", "number"],
            "description": "Position to mark: seconds, mm:ss or h:mm:ss, from get_transcript or search_meetings."
        ]
        let note: JSONValue = [
            "type": "string", "minLength": 1, "maxLength": .integer(MeetingEdits.maximumNoteCharacters),
            "description": "Why this moment matters, in the user's language. One line, no line breaks."
        ]
        return [
            "type": "object",
            "properties": ["meeting_id": meetingID, "time": time, "note": note],
            "required": ["time"],
            "additionalProperties": false
        ]
    }()

    static let unmarkHighlightSchema: JSONValue = {
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern), "default": "latest"]
        let highlightID: JSONValue = [
            "type": "string", "pattern": .string(highlightIDPattern),
            "description": "A highlight id from get_meeting, list_highlights or mark_highlight."
        ]
        return [
            "type": "object",
            "properties": ["meeting_id": meetingID, "highlight_id": highlightID],
            "required": ["highlight_id"],
            "additionalProperties": false
        ]
    }()

    static let writeMeetingNotesSchema: JSONValue = {
        let meetingID: JSONValue = ["type": "string", "pattern": .string(meetingIDPattern), "default": "latest"]
        let summary: JSONValue = [
            "type": "string", "minLength": 1, "maxLength": .integer(MeetingNotes.maximumSummaryCharacters),
            "description": "What the meeting was about, in the user's language. One line, no line breaks."
        ]
        let item: JSONValue = [
            "type": "string", "minLength": 1, "maxLength": .integer(MeetingNotes.maximumItemCharacters)
        ]
        let decisions: JSONValue = [
            "type": "array", "items": item, "maxItems": .integer(MeetingNotes.maximumItems),
            "description": "What was decided, one decision per item."
        ]
        let tasks: JSONValue = [
            "type": "array", "items": item, "maxItems": .integer(MeetingNotes.maximumItems),
            "description": "What someone is to do, one task per item; name the owner when the meeting did."
        ]
        return [
            "type": "object",
            "properties": ["meeting_id": meetingID, "summary": summary, "decisions": decisions, "tasks": tasks],
            "required": ["meeting_id"],
            "additionalProperties": false
        ]
    }()

    static func inputSchema(for toolName: String) -> JSONValue {
        switch toolName {
        case MCPText.listMeetings.name: return listMeetingsSchema
        case MCPText.getMeeting.name: return getMeetingSchema
        case MCPText.getTranscript.name: return getTranscriptSchema
        case MCPText.searchMeetings.name: return searchMeetingsSchema
        case MCPText.listHighlights.name: return listHighlightsSchema
        case MCPText.markHighlight.name: return markHighlightSchema
        case MCPText.unmarkHighlight.name: return unmarkHighlightSchema
        case MCPText.writeMeetingNotes.name: return writeMeetingNotesSchema
        default: return ["type": "object", "additionalProperties": false]
        }
    }

    /// `tools/list` entries in their fixed order; the same for every connection.
    static let toolEntries: [JSONValue] = MCPText.tools.map { tool in
        // A tool that writes says so, and says that it replaces what was there before.
        let annotations: JSONValue = [
            "title": .string(tool.title), "readOnlyHint": .bool(tool.readOnly),
            "destructiveHint": .bool(!tool.readOnly), "idempotentHint": true, "openWorldHint": false
        ]
        let meta: JSONValue = .object([MCPText.maximumResultSizeMetaKey: .integer(maximumResultSizeCharacters)])
        return [
            "name": .string(tool.name), "title": .string(tool.title), "description": .string(tool.description),
            "inputSchema": inputSchema(for: tool.name), "annotations": annotations, "_meta": meta
        ]
    }

    static let prompts: [JSONValue] = {
        let meetingID: JSONValue = [
            "name": "meeting_id", "description": .string(MCPText.meetingIDArgumentDescription), "required": false
        ]
        let detail: JSONValue = [
            "name": "detail", "description": .string(MCPText.detailArgumentDescription), "required": false
        ]
        let tasks: JSONValue = [
            "name": .string(MCPText.meetingTasksName), "title": .string(MCPText.meetingTasksTitle),
            "description": .string(MCPText.meetingTasksDescription), "arguments": [meetingID]
        ]
        let summary: JSONValue = [
            "name": .string(MCPText.meetingSummaryName), "title": .string(MCPText.meetingSummaryTitle),
            "description": .string(MCPText.meetingSummaryDescription), "arguments": [meetingID, detail]
        ]
        return [tasks, summary]
    }()

    static let resourceTemplates: [JSONValue] = [
        [
            "uriTemplate": .string(MCPText.transcriptURITemplate), "name": .string(MCPText.transcriptTemplateName),
            "title": .string(MCPText.transcriptTemplateTitle),
            "description": .string(MCPText.transcriptTemplateDescription),
            "mimeType": .string(MCPText.markdownMimeType)
        ]
    ]
}

/// Checks a value against the subset of JSON Schema the tool schemas use: `type`,
/// `properties`, `required`, `additionalProperties: false`, string lengths and patterns,
/// numeric bounds and array items, counts and uniqueness.
final class MCPSchemaValidator {
    private var expressions: [String: NSRegularExpression] = [:]

    /// Actionable hints for pattern failures, by argument name.
    private static let formatHints: [String: String] = [
        "meeting_id": MCPText.meetingIDFormatHint,
        "since": MCPText.dateFormatHint,
        "until": MCPText.dateFormatHint,
        "speaker_ids": MCPText.speakerIDFormatHint,
        "highlight_id": MCPText.highlightIDFormatHint
    ]

    /// The first problem as "argument: what is wrong", or nil when the value is valid.
    func problem(with value: JSONValue, schema: JSONValue, path: String = "") -> String? {
        if let expected = schema["type"] {
            let names = expected.stringValue.map { [$0] } ?? expected.arrayValue?.compactMap(\.stringValue) ?? []
            if !names.isEmpty, !names.contains(where: { Self.value(value, hasType: $0) }) {
                return "\(Self.label(path)): must be \(names.map(Self.typeDescription).joined(separator: " or "))"
            }
        }
        switch value {
        case let .object(members): return objectProblem(members, schema: schema, path: path)
        case let .array(elements): return arrayProblem(elements, schema: schema, path: path)
        case let .string(text): return stringProblem(text, schema: schema, path: path)
        case .integer, .number: return numberProblem(value, schema: schema, path: path)
        case .null, .bool: return nil
        }
    }

    /// An argument name taken from a request, safe to repeat in a message.
    static func displayName(_ name: String) -> String {
        MCPMeetingView.singleLine(name, limit: 40)
    }

    private func objectProblem(_ members: [String: JSONValue], schema: JSONValue, path: String) -> String? {
        let properties = schema["properties"]?.objectValue ?? [:]
        for name in schema["required"]?.arrayValue?.compactMap(\.stringValue) ?? [] where members[name] == nil {
            return "\(Self.join(path, name)): is required"
        }
        if schema["additionalProperties"] == .bool(false),
           let unknown = members.keys.sorted().first(where: { properties[$0] == nil }) {
            return "\(Self.join(path, Self.displayName(unknown))): unknown argument"
        }
        for name in members.keys.sorted() {
            guard let member = members[name], let memberSchema = properties[name] else { continue }
            if let problem = problem(with: member, schema: memberSchema, path: Self.join(path, name)) {
                return problem
            }
        }
        return nil
    }

    private func arrayProblem(_ elements: [JSONValue], schema: JSONValue, path: String) -> String? {
        let label = Self.label(path)
        if let minimum = schema["minItems"]?.intValue, elements.count < minimum {
            return "\(label): must have at least \(minimum) item\(minimum == 1 ? "" : "s")"
        }
        if let maximum = schema["maxItems"]?.intValue, elements.count > maximum {
            return "\(label): must have at most \(maximum) items"
        }
        if schema["uniqueItems"] == .bool(true) {
            for index in elements.indices where elements[..<index].contains(elements[index]) {
                return "\(label): must not repeat items"
            }
        }
        if let itemSchema = schema["items"] {
            for element in elements {
                // Items are named after the array, so the format hint of the array applies.
                if let problem = problem(with: element, schema: itemSchema, path: path) {
                    return problem
                }
            }
        }
        return nil
    }

    private func stringProblem(_ text: String, schema: JSONValue, path: String) -> String? {
        let label = Self.label(path)
        let length = text.unicodeScalars.count
        if let minimum = schema["minLength"]?.intValue, length < minimum {
            return "\(label): must be at least \(minimum) character\(minimum == 1 ? "" : "s") long"
        }
        if let maximum = schema["maxLength"]?.intValue, length > maximum {
            return "\(label): must be at most \(maximum) characters long"
        }
        if let pattern = schema["pattern"]?.stringValue, !matches(text, pattern: pattern) {
            let hint = Self.formatHints[path].map { "; \($0)" } ?? ""
            return "\(label): has an invalid format\(hint)"
        }
        return nil
    }

    private func numberProblem(_ value: JSONValue, schema: JSONValue, path: String) -> String? {
        guard let number = value.doubleValue else { return nil }
        let label = Self.label(path)
        if let minimum = schema["minimum"]?.doubleValue, number < minimum {
            return "\(label): must be at least \(Self.format(minimum))"
        }
        if let maximum = schema["maximum"]?.doubleValue, number > maximum {
            return "\(label): must be at most \(Self.format(maximum))"
        }
        return nil
    }

    private func matches(_ text: String, pattern: String) -> Bool {
        let expression: NSRegularExpression
        if let cached = expressions[pattern] {
            expression = cached
        } else {
            guard let compiled = try? NSRegularExpression(pattern: pattern) else { return false }
            expressions[pattern] = compiled
            expression = compiled
        }
        let range = NSRange(text.startIndex..., in: text)
        guard let match = expression.firstMatch(in: text, options: [], range: range) else { return false }
        // ICU's `$` also matches before a final line break; the whole value must match.
        return match.range == range
    }

    private static func value(_ value: JSONValue, hasType type: String) -> Bool {
        switch (type, value) {
        case ("object", .object), ("array", .array), ("string", .string), ("boolean", .bool), ("null", .null):
            return true
        case ("integer", .integer), ("number", .integer), ("number", .number):
            return true
        case let ("integer", .number(number)):
            return Int(exactly: number) != nil
        default:
            return false
        }
    }

    private static func typeDescription(_ type: String) -> String {
        switch type {
        case "integer": return "an integer"
        case "array": return "an array"
        case "object": return "an object"
        case "boolean": return "true or false"
        default: return "a \(type)"
        }
    }

    private static func label(_ path: String) -> String {
        path.isEmpty ? "arguments" : path
    }

    private static func join(_ path: String, _ name: String) -> String {
        path.isEmpty ? name : "\(path).\(name)"
    }

    private static func format(_ number: Double) -> String {
        Int(exactly: number).map(String.init) ?? "\(number)"
    }
}
