using CommunityToolkit.Mvvm.ComponentModel;
using Slovo.App.Localization;
using Slovo.Core.Platform;
using Slovo.Core.Settings;

namespace Slovo.App.ViewModels.Settings;

/// <summary>Settings → System: launch at login, the idle Flow bar, sounds, the tray icon.</summary>
public sealed partial class SystemSettingsViewModel(AppSettings settings, IAutostart autostart) : ViewModelBase
{
    [ObservableProperty] public partial string? LaunchAtLoginError { get; private set; }

    public bool LaunchAtLogin
    {
        get => autostart.IsEnabled;
        set
        {
            if (value == autostart.IsEnabled) return;
            if (autostart.SetEnabled(value))
            {
                settings.LaunchAtLogin = value;
                LaunchAtLoginError = null;
            }
            else
            {
                LaunchAtLoginError = Loc.Instance.Get("settings.system.launchAtLogin.failed");
            }
            OnPropertyChanged();
        }
    }

    public bool ShowFlowBarWhenIdle
    {
        get => settings.ShowFlowBarWhenIdle;
        set { settings.ShowFlowBarWhenIdle = value; OnPropertyChanged(); }
    }

    public bool SoundsEnabled
    {
        get => settings.SoundsEnabled;
        set { settings.SoundsEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>The tray icon; the app follows the setting at once.</summary>
    public bool ShowMenuBarIcon
    {
        get => settings.ShowMenuBarIcon;
        set { settings.ShowMenuBarIcon = value; OnPropertyChanged(); }
    }
}
