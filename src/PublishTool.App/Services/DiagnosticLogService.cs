using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace PublishTool.App.Services;

internal static class DiagnosticLogService
{
    private const int MaxQueuedEntries = 5_000;
    private static readonly BlockingCollection<string> Entries = new(
        new ConcurrentQueue<string>(), MaxQueuedEntries);

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PublishTool",
        "logs");

    static DiagnosticLogService()
    {
        _ = Task.Run(WriteLoop);
    }

    public static void Write(string area, string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        Entries.TryAdd($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{area}] {singleLine}");
    }

    private static void WriteLoop()
    {
        StreamWriter? writer = null;
        string? currentPath = null;

        foreach (var entry in Entries.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"diagnostic-{DateTime.Now:yyyyMMdd}.log");
                if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    writer?.Dispose();
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    currentPath = path;
                }

                writer!.WriteLine(entry);
            }
            catch
            {
                writer?.Dispose();
                writer = null;
                currentPath = null;
            }
        }

        writer?.Dispose();
    }
}
