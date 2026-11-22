using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Slovo.App.Localization;

namespace Slovo.App.Views;

public static class ViewConverters
{
    /// <summary>A geometry from Themes/Icons.axaml by its key.</summary>
    public static readonly IValueConverter IconByKey = new FuncValueConverter<string?, Geometry?>(
        key => key is not null && Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null);

    /// <summary>
    /// The string for a key in the current language. The binding does not update by
    /// itself when the language changes; views that show keys this way are rebuilt then.
    /// </summary>
    public static readonly IValueConverter Localized = new FuncValueConverter<string?, string>(
        key => key is null ? "" : Loc.Instance.Get(key));

    public static readonly IValueConverter SpeakerBrush = new FuncValueConverter<int, IBrush?>(
        index => Application.Current?.TryGetResource($"Slovo.Speaker{((index % 8) + 8) % 8}", Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null);

    /// <summary>Speech RMS sits low on 0…1; the square root spreads it over a meter.</summary>
    public static readonly IValueConverter MeterLevel = new FuncValueConverter<float, double>(level => Math.Min(1, Math.Sqrt(Math.Max(0, level)) * 1.3));

    public static readonly IValueConverter IsNotEmpty = new FuncValueConverter<string?, bool>(text => !string.IsNullOrEmpty(text));
}
