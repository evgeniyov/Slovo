namespace Slovo.Core;

/// <summary>
/// Where Slovo keeps its data. The layout inside the folder is the macOS app's
/// (<c>history.json</c>, <c>Meetings/&lt;id&gt;/meeting.json</c>, <c>Models/</c>, …), so
/// meetings can be copied between the two apps. Stores take this instead of reading
/// global paths, so tests run against a temporary folder.
/// </summary>
public sealed record AppPaths(string Base, string? ModelsOverride = null)
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\Slovo</c> on Windows. Elsewhere (developing the Windows app on a
    /// Mac) <c>~/.local/share/Slovo-Windows</c>, never the macOS app's own folder.
    /// <c>SLOVO_DATA_DIR</c> and <c>SLOVO_MODELS_DIR</c> override both.
    /// </summary>
    public static AppPaths Standard
    {
        get
        {
            var overrideBase = Environment.GetEnvironmentVariable("SLOVO_DATA_DIR");
            var models = Environment.GetEnvironmentVariable("SLOVO_MODELS_DIR");
            if (!string.IsNullOrWhiteSpace(overrideBase))
            {
                return new AppPaths(overrideBase, string.IsNullOrWhiteSpace(models) ? null : models);
            }
            string root;
            if (OperatingSystem.IsWindows())
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Slovo");
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(home, ".local", "share", "Slovo-Windows");
            }
            return new AppPaths(root, string.IsNullOrWhiteSpace(models) ? null : models);
        }
    }

    public string SettingsFile => Path.Combine(Base, "settings.json");
    public string HistoryFile => Path.Combine(Base, "history.json");
    public string DictionaryFile => Path.Combine(Base, "dictionary.json");
    public string SnippetsFile => Path.Combine(Base, "snippets.json");
    public string NotesFile => Path.Combine(Base, "notes.json");
    public string AppCategoriesFile => Path.Combine(Base, "app-categories.json");
    /// <summary><c>Recordings/&lt;yyyy-mm-dd&gt;/&lt;uuid&gt;.wav</c>.</summary>
    public string RecordingsFolder => Path.Combine(Base, "Recordings");
    /// <summary><c>Meetings/&lt;id&gt;/</c>.</summary>
    public string MeetingsFolder => Path.Combine(Base, "Meetings");
    public string ModelsFolder => ModelsOverride ?? Path.Combine(Base, "Models");
    public string InboxFolder => Path.Combine(Base, "Inbox");
    public string LogsFolder => Path.Combine(Base, "Logs");

    /// <summary>The file a history entry's AudioFileName points to.</summary>
    public string RecordingPath(string relativePath) =>
        Path.Combine(RecordingsFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string MeetingFolder(Guid id) => Path.Combine(MeetingsFolder, id.ToString("D").ToUpperInvariant());

    /// <summary>Creates the base, recordings, meetings and inbox folders.</summary>
    public void CreateFolders()
    {
        foreach (var folder in new[] { Base, RecordingsFolder, MeetingsFolder, InboxFolder })
        {
            Directory.CreateDirectory(folder);
        }
    }
}
