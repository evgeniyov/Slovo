using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Slovo.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Slovo's log: one file under <c>Logs/slovo.log</c> in the data folder (rotated at 2 MB),
/// and optionally stderr. Nothing is ever written to stdout, which belongs to the MCP
/// protocol in <c>--mcp</c> mode. Transcribed text is never logged.
/// </summary>
public static class Log
{
    static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    static readonly Lock Gate = new();
    static string? filePath;
    static bool toStderr;
    static Thread? writer;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Starts writing to a file (and stderr when asked). Safe to call again.</summary>
    public static void Configure(string? logFolder, bool alsoToStderr = false)
    {
        lock (Gate)
        {
            toStderr = alsoToStderr;
            if (logFolder is not null)
            {
                try
                {
                    Directory.CreateDirectory(logFolder);
                    filePath = Path.Combine(logFolder, "slovo.log");
                }
                catch (IOException)
                {
                    filePath = null;
                }
                catch (UnauthorizedAccessException)
                {
                    filePath = null;
                }
            }
            if (writer is null)
            {
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "Slovo log" };
                writer.Start();
            }
        }
    }

    public static string? FilePath => filePath;

    public static Logger For(string category) => new(category);

    internal static void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level.ToString().ToUpperInvariant(),-7} [{category}] {message}");
        if (toStderr)
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
        Queue.TryAdd(line);
    }

    /// <summary>Writes whatever is queued; called before the process exits.</summary>
    public static void Flush()
    {
        var lines = new List<string>();
        while (Queue.TryTake(out var line))
        {
            lines.Add(line);
        }
        Append(lines);
    }

    static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            var lines = new List<string> { line };
            while (Queue.TryTake(out var more))
            {
                lines.Add(more);
            }
            Append(lines);
        }
    }

    static void Append(List<string> lines)
    {
        var path = filePath;
        if (path is null || lines.Count == 0)
        {
            return;
        }
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllLines(path, lines, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>A category of the log, like <c>os.Logger</c>'s category on macOS.</summary>
public readonly struct Logger(string category)
{
    public void Debug(string message) => Log.Write(LogLevel.Debug, category, message);
    public void Info(string message) => Log.Write(LogLevel.Info, category, message);
    public void Warning(string message) => Log.Write(LogLevel.Warning, category, message);
    public void Error(string message) => Log.Write(LogLevel.Error, category, message);
    public void Error(string message, Exception exception) =>
        Log.Write(LogLevel.Error, category, $"{message}: {exception.GetType().Name}: {exception.Message}");
}
