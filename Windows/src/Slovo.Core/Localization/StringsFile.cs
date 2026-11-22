using System.Text;
using System.Xml.Linq;

namespace Slovo.Core.Localization;

/// <summary>
/// Reads Apple <c>.strings</c> and <c>.stringsdict</c> tables, so the Windows app shares
/// its copy with the macOS app instead of keeping a translation of its own.
/// </summary>
public static class StringsFile
{
    /// <summary>Parses <c>"key" = "value";</c> lines with comments and escapes.</summary>
    public static Dictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        while (true)
        {
            SkipTrivia(content, ref index);
            if (index >= content.Length)
            {
                break;
            }
            var key = ReadToken(content, ref index);
            SkipTrivia(content, ref index);
            if (index >= content.Length || content[index] != '=')
            {
                throw new FormatException($"Expected '=' after \"{key}\" at {index}.");
            }
            index++;
            SkipTrivia(content, ref index);
            var value = ReadToken(content, ref index);
            SkipTrivia(content, ref index);
            if (index >= content.Length || content[index] != ';')
            {
                throw new FormatException($"Expected ';' after the value of \"{key}\" at {index}.");
            }
            index++;
            result[key] = value;
        }
        return result;
    }

    static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                index++;
            }
            else if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
            }
            else if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = text.IndexOf('\n', index + 2);
                index = end < 0 ? text.Length : end + 1;
            }
            else
            {
                break;
            }
        }
    }

    static string ReadToken(string text, ref int index)
    {
        if (index >= text.Length)
        {
            throw new FormatException("Unexpected end of the table.");
        }
        if (text[index] != '"')
        {
            // Unquoted keys are allowed for identifiers.
            var start = index;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.' or '-'))
            {
                index++;
            }
            if (start == index)
            {
                throw new FormatException($"Unexpected '{text[index]}' at {index}.");
            }
            return text[start..index];
        }
        index++;
        var builder = new StringBuilder();
        while (index < text.Length)
        {
            var c = text[index++];
            if (c == '"')
            {
                return builder.ToString();
            }
            if (c != '\\' || index >= text.Length)
            {
                builder.Append(c);
                continue;
            }
            var escaped = text[index++];
            switch (escaped)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '0': builder.Append('\0'); break;
                case 'U' or 'u':
                    var hex = text.Substring(index, Math.Min(4, text.Length - index));
                    builder.Append((char)Convert.ToInt32(hex, 16));
                    index += 4;
                    break;
                default: builder.Append(escaped); break;
            }
        }
        throw new FormatException("Unterminated string in the table.");
    }

    /// <summary>
    /// Parses a <c>.stringsdict</c> plist: key → plural forms (zero/one/two/few/many/other)
    /// for the single variable of each entry.
    /// </summary>
    public static Dictionary<string, PluralEntry> ParseStringsDict(string xml)
    {
        var result = new Dictionary<string, PluralEntry>(StringComparer.Ordinal);
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root?.Element("dict");
        if (root is null)
        {
            return result;
        }
        foreach (var (key, value) in Pairs(root))
        {
            if (value.Name != "dict")
            {
                continue;
            }
            var entry = Pairs(value).ToDictionary(p => p.Key, p => p.Value);
            if (!entry.TryGetValue("NSStringLocalizedFormatKey", out var formatElement))
            {
                continue;
            }
            var forms = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (variable, rule) in entry)
            {
                if (variable == "NSStringLocalizedFormatKey" || rule.Name != "dict")
                {
                    continue;
                }
                foreach (var (form, text) in Pairs(rule))
                {
                    if (form is "zero" or "one" or "two" or "few" or "many" or "other")
                    {
                        forms[form] = text.Value;
                    }
                }
            }
            result[key] = new PluralEntry(formatElement.Value, forms);
        }
        return result;
    }

    static IEnumerable<(string Key, XElement Value)> Pairs(XElement dict)
    {
        string? pendingKey = null;
        foreach (var element in dict.Elements())
        {
            if (element.Name == "key")
            {
                pendingKey = element.Value;
            }
            else if (pendingKey is not null)
            {
                yield return (pendingKey, element);
                pendingKey = null;
            }
        }
    }
}

/// <summary>One plural entry: the outer format (such as <c>%1$#@count@</c>) and its forms.</summary>
public sealed record PluralEntry(string Format, IReadOnlyDictionary<string, string> Forms);
