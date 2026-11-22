namespace Slovo.Engines.Recognition;

/// <summary>Checks model files on disk without loading them.</summary>
public static class ModelFiles
{
    /// <summary>
    /// The expected files that are missing (or not regular files) and those whose size
    /// differs from the release, in the order given. Symbolic links count as their target.
    /// </summary>
    public static (IReadOnlyList<string> Missing, IReadOnlyList<string> WrongSize) Check(string folder, IEnumerable<ModelFile> files)
    {
        var missing = new List<string>();
        var wrongSize = new List<string>();
        foreach (var file in files)
        {
            var size = RegularFileSize(Path.Combine(folder, file.Name));
            if (size is null)
            {
                missing.Add(file.Name);
            }
            else if (size != file.Size)
            {
                wrongSize.Add(file.Name);
            }
        }
        return (missing, wrongSize);
    }

    /// <summary>The size of a regular file, following symbolic links; null when there is none.</summary>
    public static long? RegularFileSize(string path)
    {
        try
        {
            FileSystemInfo info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            }
            return info is FileInfo { Exists: true } file && !file.Attributes.HasFlag(FileAttributes.Directory) ? file.Length : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Bytes of every regular file under a folder; 0 when it does not exist.</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Recursive))
            {
                total += file.Length;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    /// <summary>
    /// Whether a folder holds any file. Hidden dot files (a Finder <c>.DS_Store</c>) do not
    /// count when <paramref name="ignoreDotFiles"/> is set, so empty folders left by a
    /// cancelled download count as nothing downloaded.
    /// </summary>
    public static bool ContainsFiles(string folder, bool ignoreDotFiles = false)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", Recursive)
                .Any(file => !ignoreDotFiles || !file.Name.StartsWith('.'));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };
}
