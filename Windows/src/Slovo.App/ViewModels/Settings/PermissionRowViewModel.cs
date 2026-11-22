using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.App.ViewModels.Settings;

/// <summary>
/// A permission with its status and the button that fixes it (the macOS PermissionRow).
/// Desktop apps are never prompted on Windows, so the button always opens the matching
/// page of Windows Settings; the status is read again when the Settings window comes back.
/// </summary>
public sealed partial class PermissionRowViewModel : ViewModelBase
{
    readonly IPermissions permissions;

    public PermissionRowViewModel(IPermissions permissions, PermissionKind kind, string iconKey, string title, string detail)
    {
        this.permissions = permissions;
        Kind = kind;
        IconKey = iconKey;
        Title = title;
        Detail = detail;
        Refresh();
    }

    public PermissionKind Kind { get; }
    public string IconKey { get; }
    public string Title { get; }
    public string Detail { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted), nameof(IsMissing), nameof(StatusText))]
    public partial PermissionStatus Status { get; private set; }

    public bool IsGranted => Status == PermissionStatus.Granted;

    public bool IsMissing => Status is PermissionStatus.Denied or PermissionStatus.NotDetermined;

    /// <summary>Allowed, not allowed, or "see in Windows Settings" when the switch could not be read.</summary>
    public string StatusText => Loc.Instance.Get(Status switch
    {
        PermissionStatus.Granted => "permission.status.granted",
        PermissionStatus.Unknown => "permission.status.unknown",
        _ => "permission.status.missing",
    });

    public void Refresh() => Status = permissions.Status(Kind);

    [RelayCommand]
    void Open() => permissions.OpenSystemSettings(Kind);
}
