using System.Text.Json.Serialization;

namespace InfinityNikki.LaunchBridge;

public sealed class BridgeConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public string LauncherRoot { get; init; } = "";
    public ExecutableDiscoveryConfiguration Executable { get; init; } = new();
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool RunAsAdministrator { get; init; }
    public IReadOnlyList<string> WatchProcessNames { get; init; } = [];
    public int StartupTimeoutSeconds { get; init; } = 180;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public int ExitGraceSeconds { get; init; } = 8;
    public bool RefuseIfAlreadyRunning { get; init; } = true;
    public string? LogPath { get; init; }

    [JsonIgnore]
    public string SourcePath { get; internal set; } = "";
}

public sealed class ExecutableDiscoveryConfiguration
{
    public string RelativePath { get; init; } = "";
    public bool SearchVersionDirectories { get; init; }
}

public sealed record ResolvedBridgeConfiguration(
    string SourcePath,
    string LauncherRoot,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool RunAsAdministrator,
    IReadOnlyList<string> WatchProcessNames,
    IReadOnlyList<string> WatchExecutablePaths,
    TimeSpan StartupTimeout,
    TimeSpan PollInterval,
    TimeSpan ExitGrace,
    bool RefuseIfAlreadyRunning,
    string LogPath);
