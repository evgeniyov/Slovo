using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;

namespace Slovo.App.FlowBar;

public static class FlowBarConverters
{
    public static readonly IValueConverter RecordingPadding = new FuncValueConverter<bool, Thickness>(
        handsFree => handsFree ? new Thickness(6, 0) : new Thickness(16, 0));

    public static readonly IValueConverter RemainingBrush = new FuncValueConverter<bool, IBrush?>(
        low => Resource(low ? "Slovo.FlowBarWarning" : "Slovo.FlowBarSecondary"));

    public static readonly IValueConverter IconBrush = new FuncValueConverter<bool, IBrush?>(
        warning => Resource(warning ? "Slovo.FlowBarWarning" : "Slovo.FlowBarAccent"));

    public static readonly IValueConverter Icon = new FuncValueConverter<string?, Geometry?>(
        key => key is not null && Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null);

    static IBrush? Resource(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;

    /// <summary>Formats with the invariant culture, for the few converters that print numbers.</summary>
    public static CultureInfo Invariant => CultureInfo.InvariantCulture;
}
