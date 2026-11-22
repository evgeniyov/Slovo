import Foundation

/// Cleans text taken from a meeting before Claude reads it.
///
/// Transcripts, titles, names and notes are untrusted: a hand-edited `meeting.json` or
/// an unlucky recognition result must not be able to draw on a terminal, reorder text
/// with invisible direction marks, hide text Claude reads but the user cannot see, or close
/// the wrapper that marks content as data.
public enum MCPSanitizer {
    /// Removes C0/C1 control characters, ANSI escape sequences, bidirectional controls
    /// (U+200E, U+200F, U+202A–U+202E, U+2066–U+2069) and invisible characters (U+200B,
    /// U+200C, U+2060–U+2064, U+FEFF, the tag characters U+E0000–U+E007F, and U+200D except
    /// inside an emoji sequence), and turns `<transcript` and `</transcript` into
    /// `‹transcript` and `‹/transcript`, so content cannot close the wrapper that marks it
    /// as untrusted.
    ///
    /// The tag is recognised in any case, with `＜`, `﹤`, `／`, fullwidth letters and Cyrillic
    /// look-alikes standing in for their ASCII forms, and with any whitespace or invisible
    /// character around the slash or between the letters: whitespace is collapsed into plain
    /// spaces later, when lines are built, and a no-break space must not survive this step
    /// only to become the space of `< /transcript>` afterwards.
    ///
    /// Tabs and line breaks become spaces instead of disappearing, so the words they
    /// separated stay apart.
    public static func sanitize(_ text: String) -> String {
        guard text.unicodeScalars.contains(where: needsAttention) else { return text }
        return neutralizeTags(stripControls(Array(text.unicodeScalars)))
    }

    /// True for a scalar `sanitize` would drop or turn into a space: a control character,
    /// part of an ANSI escape, or a bidirectional control. `<` is not one of them, because
    /// only the transcript tag built from it is neutralized.
    ///
    /// Text coming the other way, from Claude into a meeting, is refused rather than
    /// cleaned, and this is the test it is refused by.
    public static func isControl(_ scalar: Unicode.Scalar) -> Bool {
        let value = scalar.value
        return value < 0x20 || (0x7F...0x9F).contains(value) || isBidiControl(value)
    }

    /// True for an invisible character `sanitize` removes: zero-width spaces and joiners,
    /// word joiners and invisible operators, the byte order mark, and the tag characters
    /// that can spell out text a model reads and a person never sees. `sanitize` keeps the
    /// zero-width joiner only where it joins two emoji into one picture.
    public static func isInvisible(_ scalar: Unicode.Scalar) -> Bool {
        let value = scalar.value
        return (0x200B...0x200D).contains(value) || (0x2060...0x2064).contains(value) || value == 0xFEFF
            || (0xE0000...0xE007F).contains(value)
    }

    private static func needsAttention(_ scalar: Unicode.Scalar) -> Bool {
        isControl(scalar) || isInvisible(scalar) || tagOpeners.contains(scalar.value)
    }

    private static func isBidiControl(_ value: UInt32) -> Bool {
        value == 0x200E || value == 0x200F || (0x202A...0x202E).contains(value) || (0x2066...0x2069).contains(value)
    }

    private static let escape: UInt32 = 0x1B
    private static let bell: UInt32 = 0x07
    private static let stringTerminator: UInt32 = 0x9C
    private static let zeroWidthJoiner: UInt32 = 0x200D
    /// `<`, the small form `﹤` and the fullwidth `＜`.
    private static let tagOpeners: Set<UInt32> = [0x3C, 0xFE64, 0xFF1C]
    /// `/` and the fullwidth `／`.
    private static let slashes: Set<UInt32> = [0x2F, 0xFF0F]

    private static func stripControls(_ scalars: [Unicode.Scalar]) -> [Unicode.Scalar] {
        var output: [Unicode.Scalar] = []
        output.reserveCapacity(scalars.count)
        var index = 0
        while index < scalars.count {
            let value = scalars[index].value
            switch value {
            case escape:
                index = skipEscapeSequence(scalars, from: index)
            case 0x9B:
                // C1 control sequence introducer, the one-character form of ESC [.
                index = skipControlSequence(scalars, parametersFrom: index + 1)
            case 0x90, 0x98, 0x9D, 0x9E, 0x9F:
                // C1 device control, start of string, OSC, privacy message, APC: text up to a terminator.
                index = skipControlString(scalars, from: index + 1)
            case 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x85:
                output.append(" ")
                index += 1
            case 0x00..<0x20, 0x7F...0x9F:
                index += 1
            case zeroWidthJoiner:
                // Inside an emoji sequence (👩‍💻) the joiner is part of one picture; anywhere
                // else it only hides something.
                if joinsEmoji(before: output, after: scalars, from: index + 1) {
                    output.append(scalars[index])
                }
                index += 1
            default:
                if !isBidiControl(value), !isInvisible(scalars[index]) {
                    output.append(scalars[index])
                }
                index += 1
            }
        }
        return output
    }

    /// Whether the text kept so far ends in an emoji (ignoring variation selectors) and the
    /// next scalar starts one.
    private static func joinsEmoji(before output: [Unicode.Scalar], after scalars: [Unicode.Scalar], from next: Int) -> Bool {
        guard next < scalars.count, isEmojiPart(scalars[next]) else { return false }
        guard let previous = output.last(where: { !(0xFE00...0xFE0F).contains($0.value) }) else { return false }
        return isEmojiPart(previous)
    }

    /// A pictographic emoji or a skin tone; digits and `#`, which Unicode also counts as
    /// emoji for keycaps, are not joined.
    private static func isEmojiPart(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value > 0x7F && (scalar.properties.isEmoji || scalar.properties.isEmojiModifier)
    }

    /// Returns the index after an escape sequence that starts with ESC at `start`.
    private static func skipEscapeSequence(_ scalars: [Unicode.Scalar], from start: Int) -> Int {
        let next = start + 1
        guard next < scalars.count else { return next }
        switch scalars[next].value {
        case 0x5B: // ESC [ : control sequence
            return skipControlSequence(scalars, parametersFrom: next + 1)
        case 0x5D, 0x50, 0x58, 0x5E, 0x5F: // ESC ] OSC, ESC P DCS, ESC X SOS, ESC ^ PM, ESC _ APC
            return skipControlString(scalars, from: next + 1)
        case 0x20...0x2F: // intermediate bytes, then one final byte
            var index = next
            while index < scalars.count, (0x20...0x2F).contains(scalars[index].value) { index += 1 }
            if index < scalars.count, (0x30...0x7E).contains(scalars[index].value) { index += 1 }
            return index
        case 0x30...0x7E: // two-character escape
            return next + 1
        default:
            return next
        }
    }

    /// Parameter bytes 0x30–0x3F, intermediate bytes 0x20–0x2F, then a final byte 0x40–0x7E.
    private static func skipControlSequence(_ scalars: [Unicode.Scalar], parametersFrom start: Int) -> Int {
        var index = start
        while index < scalars.count, (0x30...0x3F).contains(scalars[index].value) { index += 1 }
        while index < scalars.count, (0x20...0x2F).contains(scalars[index].value) { index += 1 }
        if index < scalars.count, (0x40...0x7E).contains(scalars[index].value) { index += 1 }
        return index
    }

    /// An operating system command or similar string ends at BEL, ESC \ or ST; without a
    /// terminator a terminal would swallow the rest, so the rest is dropped too.
    private static func skipControlString(_ scalars: [Unicode.Scalar], from start: Int) -> Int {
        var index = start
        while index < scalars.count {
            let value = scalars[index].value
            if value == bell || value == stringTerminator {
                return index + 1
            }
            if value == escape, index + 1 < scalars.count, scalars[index + 1].value == 0x5C {
                return index + 2
            }
            index += 1
        }
        return index
    }

    /// `<transcript`, `</transcript` and the look-alikes described at `sanitize` lose their
    /// angle bracket. A space-separated form is not a valid tag, but a model may still read
    /// it as one.
    private static func neutralizeTags(_ scalars: [Unicode.Scalar]) -> String {
        let word = Array("transcript".unicodeScalars)
        var output = String.UnicodeScalarView()
        output.reserveCapacity(scalars.count)
        for (index, scalar) in scalars.enumerated() {
            guard tagOpeners.contains(scalar.value) else {
                output.append(scalar)
                continue
            }
            var cursor = skipFiller(scalars, from: index + 1)
            if cursor < scalars.count, slashes.contains(scalars[cursor].value) {
                cursor = skipFiller(scalars, from: cursor + 1)
            }
            var matches = true
            for letter in word {
                cursor = skipInvisible(scalars, from: cursor)
                guard cursor < scalars.count, folded(scalars[cursor]) == letter else {
                    matches = false
                    break
                }
                cursor += 1
            }
            output.append(matches ? "‹" : scalar)
        }
        return String(output)
    }

    /// Skips whitespace of any kind and invisible characters.
    private static func skipFiller(_ scalars: [Unicode.Scalar], from start: Int) -> Int {
        var index = start
        while index < scalars.count,
              scalars[index].properties.isWhitespace || scalars[index].properties.isDefaultIgnorableCodePoint {
            index += 1
        }
        return index
    }

    /// Skips characters that take no room, such as a soft hyphen or a variation selector.
    private static func skipInvisible(_ scalars: [Unicode.Scalar], from start: Int) -> Int {
        var index = start
        while index < scalars.count, scalars[index].properties.isDefaultIgnorableCodePoint {
            index += 1
        }
        return index
    }

    /// Cyrillic letters drawn exactly like the Latin letters of "transcript", which a model
    /// reading the text would take for them.
    private static let lookAlikes: [UInt32: UInt8] = [
        0x0430: UInt8(ascii: "a"), 0x0410: UInt8(ascii: "a"), 0x0441: UInt8(ascii: "c"), 0x0421: UInt8(ascii: "c"),
        0x0440: UInt8(ascii: "p"), 0x0420: UInt8(ascii: "p"), 0x0456: UInt8(ascii: "i"), 0x0406: UInt8(ascii: "i"),
        0x0455: UInt8(ascii: "s"), 0x0405: UInt8(ascii: "s"), 0x0422: UInt8(ascii: "t")
    ]

    /// A letter as the tag match compares it: fullwidth forms and Cyrillic look-alikes as
    /// ASCII, then lowercased.
    private static func folded(_ scalar: Unicode.Scalar) -> Unicode.Scalar {
        var value = scalar.value
        if let latin = lookAlikes[value] {
            return Unicode.Scalar(latin)
        }
        if (0xFF01...0xFF5E).contains(value) {
            value -= 0xFEE0
        }
        guard let ascii = Unicode.Scalar(value), ascii.isASCII else { return scalar }
        return Unicode.Scalar(UInt8(ascii.value).lowercasedASCII)
    }
}

private extension UInt8 {
    var lowercasedASCII: UInt8 {
        (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(self) ? self + 0x20 : self
    }
}
