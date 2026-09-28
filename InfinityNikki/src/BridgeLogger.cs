using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace InfinityNikki.LaunchBridge;

public sealed class BridgeLogger
{
    private const string LogFileName = "InfinityNikkiLaunchBridge.log";
    private const string LogDirectoryName = "InfinityNikkiLaunchBridge-log";
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _path;
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private readonly object _syncRoot = new();
    private DateTimeOffset _nextMaintenanceUtc;
    private string? _writeFailure;

    public BridgeLogger(string? path = null)
    {
        _path = Path.GetFullPath(path ?? DefaultLogPath);
        lock (_syncRoot)
        {
            PruneExpiredEntries(DateTimeOffset.Now);
            _nextMaintenanceUtc = DateTimeOffset.UtcNow + MaintenanceInterval;
        }
    }

    public static string DefaultLogDirectory => Path.Combine(AppContext.BaseDirectory, LogDirectoryName);
    public static string DefaultLogPath => Path.Combine(DefaultLogDirectory, LogFileName);
    public string LogPath => _path;
    public string? WriteFailure => _writeFailure;

    public void Debug(string eventName, string message) => Write("DEBUG", eventName, message);
    public void Info(string message) => Write("INFO", "general", message);
    public void Info(string eventName, string message) => Write("INFO", eventName, message);
    public void Warn(string eventName, string message) => Write("WARN", eventName, message);
    public void Error(string message) => Write("ERROR", "general", message);
    public void Error(string eventName, string message) => Write("ERROR", eventName, message);
    public void Exception(string eventName, Exception exception, string? context = null) =>
        Write("ERROR", eventName, $"{context ?? "异常"} | {exception}");

    private void Write(string level, string eventName, string message)
    {
        try
        {
            lock (_syncRoot)
            {
                if (DateTimeOffset.UtcNow >= _nextMaintenanceUtc)
                {
                    PruneExpiredEntries(DateTimeOffset.Now);
                    _nextMaintenanceUtc = DateTimeOffset.UtcNow + MaintenanceInterval;
                }

                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var line = $"{DateTimeOffset.Now:O} "
                    + $"[{level}] [Session={_sessionId}] [PID={Environment.ProcessId}] "
                    + $"[Thread={Environment.CurrentManagedThreadId}] [Event={Sanitize(eventName)}] {Sanitize(message)}"
                    + Environment.NewLine;
                File.AppendAllText(_path, line, Utf8WithoutBom);
                _writeFailure = null;
            }
        }
        catch (Exception ex)
        {
            _writeFailure = $"{ex.GetType().FullName}: {ex.Message}";
        }
    }

    private void PruneExpiredEntries(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var cutoff = now - RetentionPeriod;
            var lines = File.ReadAllLines(_path, Encoding.UTF8);
            var retained = new List<string>(lines.Length);
            var block = new List<string>();
            DateTimeOffset? blockTimestamp = null;

            void FlushBlock()
            {
                if (block.Count > 0 && (!blockTimestamp.HasValue || blockTimestamp.Value >= cutoff))
                    retained.AddRange(block);
                block.Clear();
            }

            foreach (var line in lines)
            {
                if (TryReadTimestamp(line, out var timestamp))
                {
                    FlushBlock();
                    blockTimestamp = timestamp;
                }
                block.Add(line);
            }
            FlushBlock();

            if (retained.Count != lines.Length)
                File.WriteAllLines(_path, retained, Utf8WithoutBom);
        }
        catch
        {
            // Retention maintenance is best effort; normal logging can still continue.
        }
    }

    private static bool TryReadTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var separator = line.IndexOf(" [", StringComparison.Ordinal);
        if (separator <= 0) return false;
        return DateTimeOffset.TryParse(
            line[..separator],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out timestamp);
    }

    private static string Sanitize(string value) => value
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
