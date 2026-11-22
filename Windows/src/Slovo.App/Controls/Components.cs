using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Slovo.App.Controls;

/// <summary>Page title, subtitle and actions on the right (the macOS PageHeader).</summary>
public sealed class PageHeader : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(Subtitle));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
}

/// <summary>A dark warm card with a serif headline, a message, an optional button and a close button.</summary>
public sealed class BannerCard : ContentControl
{
    public static readonly StyledProperty<string?> HeadlineProperty = AvaloniaProperty.Register<BannerCard, string?>(nameof(Headline));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<BannerCard, string?>(nameof(Message));
    public static readonly StyledProperty<string?> ButtonTextProperty = AvaloniaProperty.Register<BannerCard, string?>(nameof(ButtonText));
    public static readonly StyledProperty<ICommand?> ButtonCommandProperty = AvaloniaProperty.Register<BannerCard, ICommand?>(nameof(ButtonCommand));
    public static readonly StyledProperty<ICommand?> DismissCommandProperty = AvaloniaProperty.Register<BannerCard, ICommand?>(nameof(DismissCommand));

    public string? Headline { get => GetValue(HeadlineProperty); set => SetValue(HeadlineProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ButtonText { get => GetValue(ButtonTextProperty); set => SetValue(ButtonTextProperty, value); }
    public ICommand? ButtonCommand { get => GetValue(ButtonCommandProperty); set => SetValue(ButtonCommandProperty, value); }
    public ICommand? DismissCommand { get => GetValue(DismissCommandProperty); set => SetValue(DismissCommandProperty, value); }
}

/// <summary>What a screen shows when it has nothing yet: an icon, a title, a message and an action.</summary>
public sealed class EmptyState : TemplatedControl
{
    public static readonly StyledProperty<Avalonia.Media.Geometry?> IconProperty = AvaloniaProperty.Register<EmptyState, Avalonia.Media.Geometry?>(nameof(Icon));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Message));
    public static readonly StyledProperty<string?> ActionTextProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(ActionText));
    public static readonly StyledProperty<ICommand?> ActionCommandProperty = AvaloniaProperty.Register<EmptyState, ICommand?>(nameof(ActionCommand));

    public Avalonia.Media.Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ActionText { get => GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
}

/// <summary>A search box with a magnifier.</summary>
public sealed class SearchBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public SearchBox()
    {
        Classes.Add("Search");
    }
}
