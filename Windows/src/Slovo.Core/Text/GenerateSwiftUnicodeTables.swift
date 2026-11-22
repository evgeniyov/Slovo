// Writes SwiftUnicodeTables.g.cs: the Unicode data Swift's String and Character use, so the
// Windows text pipeline answers exactly as the macOS one does, whatever Unicode version
// .NET or the system ICU carry. Run it on a Mac after a Swift update and commit the result:
//
//   swiftc -O Windows/src/Slovo.Core/Text/GenerateSwiftUnicodeTables.swift -o /tmp/gen-unicode
//   /tmp/gen-unicode > Windows/src/Slovo.Core/Text/SwiftUnicodeTables.g.cs
//
// Not part of any build: the C# project only compiles .cs files.
import Foundation

/// Sorted values as runs of (first, last, step) with a step of 1 or 2.
func runs(_ values: [Int]) -> [(first: Int, last: Int, step: Int)] {
    var result: [(Int, Int, Int)] = []
    var index = 0
    while index < values.count {
        let first = values[index]
        if index + 1 < values.count {
            let step = values[index + 1] - first
            if step == 1 || step == 2 {
                var end = index + 1
                while end + 1 < values.count && values[end + 1] - values[end] == step { end += 1 }
                if step == 1 || end > index + 1 {
                    result.append((first, values[end], step))
                    index = end + 1
                    continue
                }
            }
        }
        result.append((first, first, 1))
        index += 1
    }
    return result
}

/// Single-scalar mappings as runs of (first, last, step, delta).
func mappingRuns(_ pairs: [(Int, Int)]) -> [(Int, Int, Int, Int)] {
    var result: [(Int, Int, Int, Int)] = []
    var index = 0
    while index < pairs.count {
        let (first, target) = pairs[index]
        let delta = target - first
        var end = index
        var step = 0
        if index + 1 < pairs.count, pairs[index + 1].1 - pairs[index + 1].0 == delta {
            let gap = pairs[index + 1].0 - first
            if gap == 1 || gap == 2 {
                step = gap
                end = index + 1
                while end + 1 < pairs.count, pairs[end + 1].0 - pairs[end].0 == step,
                      pairs[end + 1].1 - pairs[end + 1].0 == delta {
                    end += 1
                }
            }
        }
        result.append((first, pairs[end].0, max(step, 1), delta))
        index = end + 1
    }
    return result
}

func hex(_ value: Int) -> String {
    value < 0 ? "-0x" + String(-value, radix: 16, uppercase: true) : "0x" + String(value, radix: 16, uppercase: true)
}

func csharpString(_ scalars: [Int]) -> String {
    "\"" + scalars.map { value -> String in
        value > 0xFFFF ? String(format: "\\U%08X", value) : String(format: "\\u%04X", value)
    }.joined() + "\""
}

func emit(_ name: String, _ comment: String, _ numbers: [Int], perLine: Int) -> String {
    var lines = ["    /// \(comment)", "    static ReadOnlySpan<int> \(name) =>", "    ["]
    var index = 0
    while index < numbers.count {
        let slice = numbers[index..<min(index + perLine, numbers.count)]
        lines.append("        " + slice.map(hex).joined(separator: ", ") + ",")
        index += perLine
    }
    lines.append("    ];")
    return lines.joined(separator: "\n")
}

var alphabetic: [Int] = [], numeric: [Int] = [], uppercase: [Int] = [], lowercase: [Int] = [], cased: [Int] = []
var upperSimple: [(Int, Int)] = [], lowerSimple: [(Int, Int)] = []
var upperSpecial: [(Int, [Int])] = [], lowerSpecial: [(Int, [Int])] = []
var asciiFolding: [Int] = []
for value in 0...0x10FFFF {
    guard let scalar = Unicode.Scalar(value) else { continue }
    let properties = scalar.properties
    if properties.isAlphabetic { alphabetic.append(value) }
    if properties.numericType != nil { numeric.append(value) }
    if properties.isUppercase { uppercase.append(value) }
    if properties.isLowercase { lowercase.append(value) }
    if properties.isCased { cased.append(value) }
    let text = String(scalar)
    let upper = text.uppercased().unicodeScalars.map { Int($0.value) }
    if upper != [value] {
        if upper.count == 1 { upperSimple.append((value, upper[0])) } else { upperSpecial.append((value, upper)) }
    }
    let lower = text.lowercased().unicodeScalars.map { Int($0.value) }
    if lower != [value] {
        if lower.count == 1 { lowerSimple.append((value, lower[0])) } else { lowerSpecial.append((value, lower)) }
    }
    let isAsciiLetterOrDigit = (0x61...0x7A).contains(value) || (0x30...0x39).contains(value)
    if !isAsciiLetterOrDigit {
        let kept = text.folding(options: .diacriticInsensitive, locale: nil).unicodeScalars
            .filter { (0x61...0x7A).contains($0.value) || (0x30...0x39).contains($0.value) }
        precondition(kept.count <= 1, "folding \(value) leaves more than one ASCII letter")
        if let only = kept.first { asciiFolding += [value, Int(only.value)] }
    }
}

let flat = { (runs: [(first: Int, last: Int, step: Int)]) in runs.flatMap { [$0.first, $0.last, $0.step] } }
let flatMappings = { (runs: [(Int, Int, Int, Int)]) in runs.flatMap { [$0.0, $0.1, $0.2, $0.3] } }

var output = """
// <auto-generated>
// Generated by GenerateSwiftUnicodeTables.swift from the Unicode data of \(swiftVersion()).
// Do not edit; run the generator again instead.
// </auto-generated>
namespace Slovo.Core.Text;

static partial class SwiftUnicode
{

"""
output += emit("AlphabeticRuns", "Unicode.Scalar.Properties.isAlphabetic as runs of (first, last, step).", flat(runs(alphabetic)), perLine: 12) + "\n\n"
output += emit("NumericRuns", "Scalars whose Unicode.Scalar.Properties.numericType is not nil, as runs of (first, last, step).", flat(runs(numeric)), perLine: 12) + "\n\n"
output += emit("UppercaseRuns", "Unicode.Scalar.Properties.isUppercase as runs of (first, last, step).", flat(runs(uppercase)), perLine: 12) + "\n\n"
output += emit("LowercaseRuns", "Unicode.Scalar.Properties.isLowercase as runs of (first, last, step).", flat(runs(lowercase)), perLine: 12) + "\n\n"
output += emit("CasedRuns", "Unicode.Scalar.Properties.isCased as runs of (first, last, step).", flat(runs(cased)), perLine: 12) + "\n\n"
output += emit("UppercaseMappingRuns", "String.uppercased() of single scalars that map to one other scalar, as runs of (first, last, step, delta).", flatMappings(mappingRuns(upperSimple)), perLine: 12) + "\n\n"
output += emit("LowercaseMappingRuns", "String.lowercased() of single scalars that map to one other scalar, as runs of (first, last, step, delta).", flatMappings(mappingRuns(lowerSimple)), perLine: 12) + "\n\n"
output += emit("AsciiFoldingPairs", "Scalars that String.folding(options: .diacriticInsensitive) turns into an ASCII letter or digit, as (scalar, ASCII) pairs sorted by scalar.", asciiFolding, perLine: 12) + "\n\n"
for (name, list) in [("UppercaseSpecial", upperSpecial), ("LowercaseSpecial", lowerSpecial)] {
    output += "    /// Scalars whose \(name.hasPrefix("Upper") ? "uppercased()" : "lowercased()") has more than one scalar (full case mapping).\n"
    output += "    static readonly Dictionary<int, string> \(name) = new()\n    {\n"
    for (value, mapped) in list {
        output += "        [\(hex(value))] = \(csharpString(mapped)),\n"
    }
    output += "    };\n\n"
}
output.removeLast()
output += "}\n"
print(output, terminator: "")

/// The compiler and the newest Unicode version its data knows, for the file header.
func swiftVersion() -> String {
    var newest = (major: 0, minor: 0)
    for value in 0...0x10FFFF {
        guard let age = Unicode.Scalar(value)?.properties.age else { continue }
        if age.major > newest.major || (age.major == newest.major && age.minor > newest.minor) {
            newest = (age.major, age.minor)
        }
    }
    #if compiler(>=7.0)
    let compiler = "Swift 7 or later"
    #elseif compiler(>=6.4)
    let compiler = "Swift 6.4"
    #elseif compiler(>=6.2)
    let compiler = "Swift 6.2"
    #else
    let compiler = "Swift 6"
    #endif
    return "\(compiler) (Unicode \(newest.major).\(newest.minor))"
}
