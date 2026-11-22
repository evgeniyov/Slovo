using System.Reflection;

namespace Slovo.App;

public static class AppInfo
{
    /// <summary>The informational version without the source-control suffix, such as "0.3.0".</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// <summary>The executable's full path, for registering Slovo in Claude and autostart.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Slovo.exe");
}
