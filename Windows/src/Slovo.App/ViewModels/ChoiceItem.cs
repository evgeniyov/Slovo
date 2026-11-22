namespace Slovo.App.ViewModels;

/// <summary>One option of a pop-up choice: the value and its title in the current language.</summary>
public sealed record ChoiceItem<T>(T Value, string Title)
{
    public override string ToString() => Title;
}
