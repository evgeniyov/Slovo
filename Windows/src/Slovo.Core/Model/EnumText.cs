using System.Reflection;
using System.Text.Json.Serialization;

namespace Slovo.Core.Model;

/// <summary>
/// Raw string values of the model enums (the names Swift and the JSON files use), for
/// settings and for tolerant decoding where an unknown value must fall back instead of
/// failing the whole file.
/// </summary>
public static class EnumText
{
    public static string Raw<T>(T value) where T : struct, Enum => Cache<T>.ToRaw[value];

    public static bool TryParse<T>(string? raw, out T value) where T : struct, Enum
    {
        if (raw is not null && Cache<T>.FromRaw.TryGetValue(raw, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    public static T? Parse<T>(string? raw) where T : struct, Enum =>
        TryParse<T>(raw, out var value) ? value : null;

    public static T ParseOr<T>(string? raw, T fallback) where T : struct, Enum =>
        TryParse<T>(raw, out var value) ? value : fallback;

    static class Cache<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> ToRaw = Build();
        public static readonly Dictionary<string, T> FromRaw = ToRaw.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

        static Dictionary<T, string> Build()
        {
            var result = new Dictionary<T, string>();
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var value = (T)field.GetValue(null)!;
                var name = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                    ?? char.ToLowerInvariant(field.Name[0]) + field.Name[1..];
                result[value] = name;
            }
            return result;
        }
    }
}
