using CommunityToolkit.Mvvm.Input;
using Slovo.App.Localization;
using Slovo.Core;
using Slovo.Core.Platform;

namespace Slovo.App.ViewModels.Settings;

public sealed record LicenseLine(string Name, string License, string Detail);

/// <summary>Settings → About: version, engines and their licenses.</summary>
public sealed partial class AboutSettingsViewModel(IShell shell, AppPaths paths) : ViewModelBase
{
    public string VersionText => Loc.Instance.Format("settings.about.version", AppInfo.Version, OperatingSystem.IsWindows() ? "Windows" : "dev");

    public IReadOnlyList<LicenseLine> Licenses { get; } =
    [
        new("GigaAM v3", "MIT", Loc.Instance.Get("settings.about.gigaam")),
        new("NVIDIA Parakeet TDT 0.6B v3", "CC-BY-4.0", Loc.Instance.Get("settings.about.parakeet")),
        new("sherpa-onnx", "Apache-2.0", "k2-fsa · ONNX Runtime (MIT)"),
        new("pyannote segmentation 3.0", "MIT", "pyannoteAI"),
        new("WeSpeaker ResNet34-LM", "CC-BY-4.0", "WeSpeaker · VoxCeleb · sherpa-onnx ONNX export"),
        new("Avalonia", "MIT", "AvaloniaUI"),
        new("NAudio", "MIT", "Mark Heath & contributors"),
    ];

    [RelayCommand]
    void OpenModelsFolder()
    {
        Directory.CreateDirectory(paths.ModelsFolder);
        shell.OpenFolder(paths.ModelsFolder);
    }
}
