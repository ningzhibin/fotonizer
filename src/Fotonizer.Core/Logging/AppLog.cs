using System.Collections.Concurrent;
using System.Text;

namespace Fotonizer.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, string? Detail = null)
{
    public string LevelText => Level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => Level.ToString()
    };
}

/// <summary>
/// Process-wide log sink: keeps a ring buffer for the UI and appends to a daily text file.
/// Deliberately dependency-free and never throws.
/// </summary>
public sealed class AppLog
{
    public static AppLog Shared { get; } = new();

    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _recent = new();
    private readonly ConcurrentQueue<LogEntry> _pendingFileWrites = new();
    private string? _filePath;
    private int _capacity = 2000;
    private int _fileFlushScheduled;

    public event EventHandler<LogEntry>? EntryAdded;

    public int Capacity
    {
        get => _capacity;
        set => _capacity = Math.Clamp(value, 50, 100_000);
    }

    public void ConfigureFile(string? path)
    {
        lock (_gate) _filePath = path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            catch
            {
                // logging must never break the app
            }
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _recent.ToList();
    }

    public void Clear()
    {
        lock (_gate) _recent.Clear();
    }

    public void Debug(string message, Exception? ex = null) => Write(LogLevel.Debug, message, ex);
    public void Info(string message, Exception? ex = null) => Write(LogLevel.Info, message, ex);
    public void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message ?? string.Empty, ex?.ToString());
        lock (_gate)
        {
            _recent.AddLast(entry);
            while (_recent.Count > _capacity) _recent.RemoveFirst();
        }

        try
        {
            EntryAdded?.Invoke(this, entry);
        }
        catch
        {
            // a bad subscriber must not kill logging
        }

        _pendingFileWrites.Enqueue(entry);
        ScheduleFileFlush();
    }

    private void ScheduleFileFlush()
    {
        if (Interlocked.Exchange(ref _fileFlushScheduled, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                FlushToFile();
            }
            finally
            {
                Interlocked.Exchange(ref _fileFlushScheduled, 0);
                if (!_pendingFileWrites.IsEmpty) ScheduleFileFlush();
            }
        });
    }

    private void FlushToFile()
    {
        string? path;
        lock (_gate) path = _filePath;
        if (string.IsNullOrWhiteSpace(path)) return;

        var sb = new StringBuilder();
        while (_pendingFileWrites.TryDequeue(out var entry))
        {
            if (entry.Level < LogLevel.Info) continue;
            sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(entry.Level.ToString().ToUpperInvariant()).Append("] ")
              .Append(entry.Message);
            if (!string.IsNullOrEmpty(entry.Detail))
                sb.AppendLine().Append(entry.Detail);
            sb.AppendLine();
        }

        if (sb.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ignore disk/permission problems
        }
    }

    /// <summary>Deletes log files older than the retention window. Best effort.</summary>
    public static void PruneOldLogs(string directory, int days = 14)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(directory, "fotonizer-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
