using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Slovo.Core.Json;
using Slovo.Core.Localization;
using Slovo.Core.Logging;
using Slovo.Core.Model;

namespace Slovo.Core.Storage;

/// <summary>What reading a store file found.</summary>
public enum JsonFileLoadKind
{
    Loaded,
    /// <summary>No file yet: start empty.</summary>
    Missing,
    /// <summary>The file could not be decoded and was moved aside; start empty.</summary>
    Corrupt,
    /// <summary>
    /// The file exists but could not be read right now (locked, no access). It is left
    /// alone, and the store must not save over it until it has been read.
    /// </summary>
    Unreadable,
    /// <summary>
    /// A stored array whose unreadable elements were dropped. The file itself was copied to
    /// <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;.json</c> first (<c>MovedTo</c>, null when the copy
    /// failed), so the next save loses nothing that is not kept there.
    /// </summary>
    Partial,
}

/// <param name="MovedTo">Where a corrupt file was moved, or where the original of a partial load was copied.</param>
/// <param name="Dropped">How many elements of a partial load could not be read.</param>
public readonly record struct JsonFileLoad<T>(JsonFileLoadKind Kind, T? Value = default, string? MovedTo = null, int Dropped = 0);

/// <summary>
/// One JSON file of a store: atomic saves and corruption handling shared by every store
/// (port of the macOS <c>JSONFile</c>). A damaged file is never deleted: it is renamed to
/// <c>&lt;name&gt;.corrupt-&lt;yyyyMMdd-HHmmss&gt;.json</c> and the store starts empty. A stored
/// array (<c>List&lt;E&gt;</c> or <c>E[]</c>) keeps every element that decodes and drops the
/// rest, so one damaged or newer-format entry does not cost the whole store.
/// </summary>
public sealed class JsonFile<T>(string path)
{
    static readonly Logger Logger = Log.For("Storage");

    public string Path { get; } = path;

    public JsonFileLoad<T> Load()
    {
        if (!File.Exists(Path))
        {
            return new(JsonFileLoadKind.Missing);
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not damaged, just not readable right now (locked by a sync tool, say): do not
            // move it aside, which could lose it; the store starts empty and must not save.
            Logger.Error($"{System.IO.Path.GetFileName(Path)} could not be read", error);
            return new(JsonFileLoadKind.Unreadable);
        }
        try
        {
            if (LossyArray.ElementType(typeof(T)) is { } elementType)
            {
                var (value, dropped) = LossyArray.Decode<T>(bytes, elementType);
                if (dropped == 0)
                {
                    return new(JsonFileLoadKind.Loaded, value);
                }
                Logger.Error($"{System.IO.Path.GetFileName(Path)}: dropped {dropped} unreadable entries");
                var backup = JsonFileCorruption.CopyAside(Path);
                if (backup is not null)
                {
                    Logger.Error($"Kept the original file as {System.IO.Path.GetFileName(backup)}");
                }
                return new(JsonFileLoadKind.Partial, value, backup, dropped);
            }
            var decoded = JsonSerializer.Deserialize<T>(bytes, SlovoJson.Options) ?? throw new JsonException("The file holds null.");
            return new(JsonFileLoadKind.Loaded, decoded);
        }
        catch (Exception error) when (IsDecodingError(error))
        {
            Logger.Error($"{System.IO.Path.GetFileName(Path)} is unreadable", error);
            return new(JsonFileLoadKind.Corrupt, MovedTo: JsonFileCorruption.MoveAside(Path));
        }
    }

    /// <summary>Encodes the value (keys sorted, like Swift) and replaces the file in one rename. Throws SlovoException.</summary>
    public void Save(T value)
    {
        try
        {
            AtomicFile.WriteText(Path, SlovoJson.Serialize(value));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new SlovoException(CoreStrings.Format("storage.error.saveFailed", System.IO.Path.GetFileName(Path), error.Message), error);
        }
    }

    internal static bool IsDecodingError(Exception error) =>
        error is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or OverflowException;
}

public static class JsonFileCorruption
{
    static readonly Logger Logger = Log.For("Storage");

    /// <summary><c>&lt;name&gt;.corrupt-&lt;yyyyMMdd-HHmmss&gt;.json</c>; a counter keeps earlier backups.</summary>
    public static string BackupPath(string path, DateTimeOffset? date = null)
    {
        var folder = System.IO.Path.GetDirectoryName(path) ?? ".";
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var stamp = (date ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = System.IO.Path.Combine(folder, $"{name}.corrupt-{stamp}.json");
        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(folder, $"{name}.corrupt-{stamp}-{counter}.json");
            counter++;
        }
        return candidate;
    }

    /// <summary>Renames a damaged file out of the way; null when even that failed.</summary>
    public static string? MoveAside(string path)
    {
        var destination = BackupPath(path);
        try
        {
            File.Move(path, destination);
            Logger.Error($"Moved the damaged file to {System.IO.Path.GetFileName(destination)}");
            return destination;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not move {System.IO.Path.GetFileName(path)} aside", error);
            return null;
        }
    }

    /// <summary>Copies a partly readable file next to itself before anything overwrites it; null when the copy failed.</summary>
    public static string? CopyAside(string path)
    {
        var destination = BackupPath(path);
        try
        {
            File.Copy(path, destination, overwrite: false);
            return destination;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not copy {System.IO.Path.GetFileName(path)} aside", error);
            return null;
        }
    }
}

/// <summary>
/// Decodes a stored array element by element, the way the macOS app's <c>DecodedSlot</c>
/// does: an element that Swift's <c>Decodable</c> would reject (a missing or null
/// non-optional key, a value of the wrong type, an unknown enum value) is dropped, and the
/// rest are kept in order.
/// </summary>
internal static class LossyArray
{
    static readonly JsonSerializerOptions StrictOptions = new(SlovoJson.Options) { RespectNullableAnnotations = true };
    static readonly ConcurrentDictionary<Type, string[]> RequiredKeys = new();

    /// <summary>The element type when <paramref name="type"/> is <c>List&lt;E&gt;</c> or <c>E[]</c>.</summary>
    public static Type? ElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return type.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>Throws JsonException when the data is not a JSON array at all.</summary>
    public static (T Value, int Dropped) Decode<T>(byte[] bytes, Type elementType)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The file does not hold an array.");
        }
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        var dropped = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (DecodeElement(element, elementType) is { } value)
            {
                list.Add(value);
            }
            else
            {
                dropped++;
            }
        }
        if (typeof(T).IsArray)
        {
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return ((T)(object)array, dropped);
        }
        return ((T)list, dropped);
    }

    static object? DecodeElement(JsonElement element, Type elementType)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in RequiredKeys.GetOrAdd(elementType, FindRequiredKeys))
            {
                if (!element.TryGetProperty(key, out _))
                {
                    return null;
                }
            }
        }
        try
        {
            return element.Deserialize(elementType, StrictOptions);
        }
        catch (Exception error) when (JsonFile<object>.IsDecodingError(error))
        {
            return null;
        }
    }

    /// <summary>
    /// JSON names of the properties Swift's synthesized <c>Decodable</c> requires: every
    /// stored property that is not optional. Here, every serialized property whose type
    /// is not nullable.
    /// </summary>
    static string[] FindRequiredKeys(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            return [];
        }
        var nullability = new NullabilityInfoContext();
        var keys = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length > 0
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null
                || property.GetCustomAttribute<JsonPropertyNameAttribute>() is not { } name)
            {
                continue;
            }
            var isNullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                || (!property.PropertyType.IsValueType && nullability.Create(property).ReadState != NullabilityState.NotNull);
            if (!isNullable)
            {
                keys.Add(name.Name);
            }
        }
        return [.. keys];
    }
}
