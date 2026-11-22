namespace Slovo.App.ViewModels;

/// <summary>Shows a view model as a sheet over the main window (the macOS .sheet).</summary>
public interface ISheetHost
{
    void ShowSheet(SheetViewModel sheet);
    void CloseSheet();
    /// <summary>Asks a yes/no question; true when the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = true);
    /// <summary>Shows an error in the main window.</summary>
    void ShowAlert(string title, string message);
}

/// <summary>A sheet's view model; it closes itself through the host.</summary>
public abstract class SheetViewModel : ViewModelBase
{
    public ISheetHost? Host { get; set; }

    protected void Close() => Host?.CloseSheet();
}

/// <summary>A confirmation sheet: title, message, confirm and cancel.</summary>
public sealed partial class ConfirmSheetViewModel(string title, string message, string confirmText, bool destructive) : SheetViewModel
{
    readonly TaskCompletionSource<bool> result = new();

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public bool IsDestructive { get; } = destructive;
    public Task<bool> Result => result.Task;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void Confirm()
    {
        result.TrySetResult(true);
        Close();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void Cancel()
    {
        result.TrySetResult(false);
        Close();
    }

    /// <summary>Called when the sheet goes away some other way.</summary>
    public void Dismissed() => result.TrySetResult(false);
}

/// <summary>An error or notice with one OK button.</summary>
public sealed partial class AlertSheetViewModel(string title, string message) : SheetViewModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void Ok() => Close();
}
