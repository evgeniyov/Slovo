namespace Slovo.App.ViewModels;

/// <summary>The main window's sidebar destinations, in sidebar order.</summary>
public enum Screen
{
    Home,
    Meetings,
    Insights,
    Dictionary,
    Snippets,
    Style,
    Scratchpad,
}

public static class ScreenInfo
{
    public static readonly IReadOnlyList<Screen> All =
        [Screen.Home, Screen.Meetings, Screen.Insights, Screen.Dictionary, Screen.Snippets, Screen.Style, Screen.Scratchpad];

    public static string TitleKey(this Screen screen) => screen switch
    {
        Screen.Home => "sidebar.home",
        Screen.Meetings => "sidebar.meetings",
        Screen.Insights => "sidebar.insights",
        Screen.Dictionary => "sidebar.dictionary",
        Screen.Snippets => "sidebar.snippets",
        Screen.Style => "sidebar.style",
        _ => "sidebar.scratchpad",
    };

    /// <summary>The key of the line icon in Themes/Icons.axaml.</summary>
    public static string IconKey(this Screen screen) => screen switch
    {
        Screen.Home => "Icon.Mic",
        Screen.Meetings => "Icon.People",
        Screen.Insights => "Icon.Chart",
        Screen.Dictionary => "Icon.Book",
        Screen.Snippets => "Icon.Braces",
        Screen.Style => "Icon.Pen",
        _ => "Icon.Note",
    };
}
