using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Slovo.App.Controls;

/// <summary>A settings row: title and detail on the left, the control on the right.</summary>
public sealed class SettingsRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Detail));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }
}

/// <summary>A titled group of settings rows on a card.</summary>
public sealed class SettingsGroup : HeaderedItemsControl
{
}
