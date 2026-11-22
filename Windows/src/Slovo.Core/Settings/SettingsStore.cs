using System.Text.Json;
using System.Text.Json.Nodes;
using Slovo.Core.Json;
using Slovo.Core.Logging;
using Slovo.Core.Storage;

namespace Slovo.Core.Settings;

/// <summary>
/// Key-value settings in <c>settings.json</c>, the Windows counterpart of UserDefaults.
/// Thread-safe. Every change is written at once (atomically); settings change rarely and
/// the MCP process reads the file on every request, so a switch turned off in the app
/// takes effect with Claude's next call.
/// </summary>
public sealed class SettingsStore
{
    static readonly Logger Logger = Log.For("Settings");
    readonly Lock gate = new();
    readonly JsonObject values;
    readonly bool readOnly;

    SettingsStore(string? path, JsonObject values, bool readOnly)
    {
        FilePath = path;
        this.values = values;
        this.readOnly = readOnly;
    }

    /// <summary>The file behind the store; null for an in-memory store.</summary>
    public string? FilePath { get; }

    /// <summary>Raised after a value changes, with the key, on the thread that changed it.</summary>
    public event Action<string>? Changed;

    /// <summary>Loads the file for reading and writing. A damaged file is moved aside and defaults are used.</summary>
    public static SettingsStore Open(string path) => new(path, ReadFile(path, moveAsideWhenDamaged: true), readOnly: false);

    /// <summary>A snapshot that never writes, for the MCP process.</summary>
    public static SettingsStore ReadOnlySnapshot(string path) => new(path, ReadFile(path, moveAsideWhenDamaged: false), readOnly: true);

    /// <summary>An in-memory store for tests.</summary>
    public static SettingsStore InMemory(JsonObject? values = null) => new(null, values ?? [], readOnly: false);

    public bool Contains(string key)
    {
        lock (gate)
        {
            return values.ContainsKey(key);
        }
    }

    public bool? GetBool(string key) => Read(key, node => node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node.GetValue<bool>() : (bool?)null);

    public string? GetString(string key) => Read(key, node => node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null);

    public double? GetDouble(string key) => Read(key, NumberValue);

    public int? GetInt(string key) => Read(key, node =>
        NumberValue(node) is { } value && value is >= int.MinValue and <= int.MaxValue && value == Math.Floor(value) ? (int)value : (int?)null);

    /// <summary>
    /// A number however it is held: parsed from the file it is a JSON element, set in this
    /// session it wraps the CLR value (an int must not be read as a double directly).
    /// </summary>
    static double? NumberValue(JsonNode node)
    {
        if (node.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        var value = node.AsValue();
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<float>(out var f)) return f;
        if (value.TryGetValue<decimal>(out var m)) return (double)m;
        return double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
    }

    public IReadOnlyList<string>? GetStringArray(string key) => Read(key, node =>
        node is JsonArray array && array.All(item => item?.GetValueKind() == JsonValueKind.String)
            ? array.Select(item => item!.GetValue<string>()).ToList()
            : null);

    public T? GetObject<T>(string key) where T : class => Read(key, node =>
    {
        try
        {
            return node.Deserialize<T>(SlovoJson.Compact);
        }
        catch (JsonException)
        {
            return null;
        }
    });

    public void Set(string key, bool value) => Write(key, JsonValue.Create(value));
    public void Set(string key, string value) => Write(key, JsonValue.Create(value));
    public void Set(string key, double value) => Write(key, JsonValue.Create(value));
    public void Set(string key, int value) => Write(key, JsonValue.Create(value));
    public void Set(string key, IEnumerable<string> value) => Write(key, new JsonArray(value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
    public void SetObject<T>(string key, T value) => Write(key, JsonSerializer.SerializeToNode(value, SlovoJson.Compact));

    public void Remove(string key)
    {
        lock (gate)
        {
            if (readOnly || !values.Remove(key))
            {
                return;
            }
            Save();
        }
        Changed?.Invoke(key);
    }

    T? Read<T>(string key, Func<JsonNode, T?> convert)
    {
        lock (gate)
        {
            return values.TryGetPropertyValue(key, out var node) && node is not null ? convert(node) : default;
        }
    }

    void Write(string key, JsonNode? node)
    {
        lock (gate)
        {
            if (readOnly)
            {
                throw new InvalidOperationException("This settings snapshot is read-only.");
            }
            if (values.TryGetPropertyValue(key, out var existing) && JsonNode.DeepEquals(existing, node))
            {
                return;
            }
            values[key] = node;
            Save();
        }
        Changed?.Invoke(key);
    }

    void Save()
    {
        if (FilePath is null)
        {
            return;
        }
        try
        {
            AtomicFile.WriteText(FilePath, SortedJson.Write(values, indented: true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Could not save settings", error);
        }
    }

    static JsonObject ReadFile(string path, bool moveAsideWhenDamaged)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is JsonObject obj)
            {
                return obj;
            }
            throw new JsonException("settings.json must hold an object.");
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.Error("settings.json is unreadable", error);
            if (moveAsideWhenDamaged)
            {
                JsonFileCorruption.MoveAside(path);
            }
            return [];
        }
    }
}
