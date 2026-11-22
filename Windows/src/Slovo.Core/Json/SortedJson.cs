using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Slovo.Core.Json;

/// <summary>
/// Writes a JSON tree with object keys in ordinal order, like Swift's
/// <c>JSONEncoder.OutputFormatting.sortedKeys</c>, so files written by either app diff
/// cleanly against each other.
/// </summary>
public static class SortedJson
{
    static readonly JsonWriterOptions IndentedOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonWriterOptions CompactOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(JsonNode? node, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, indented ? IndentedOptions : CompactOptions))
        {
            WriteNode(writer, node);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteNode(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteNode(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
