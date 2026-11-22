using Avalonia.Controls;

namespace Slovo.App.Onboarding;

public partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
        // Statuses change in Windows Settings while Slovo waits; look again on return.
        Activated += (_, _) => (DataContext as OnboardingViewModel)?.RefreshPermissions();
        Closed += (_, _) => (DataContext as OnboardingViewModel)?.Closed();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is OnboardingViewModel model)
            {
                model.Finished += Close;
            }
        };
    }
}
