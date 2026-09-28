using System.Diagnostics;
using InfinityNikki.LaunchBridge;

if (args.Length == 1 && args[0] == "--fake-game")
{
    await Task.Delay(1500);
    return;
}

var failures = new List<string>();
var checks = 0;

void Check(bool condition, string message)
{
    checks++;
    if (!condition) failures.Add(message);
}

void ExpectConfigurationError(Action action, string message)
{
    try
    {
        action();
        failures.Add(message);
    }
    catch (BridgeConfigurationException)
    {
        checks++;
    }
}

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"infinity-nikki-launch-bridge-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryRoot);
try
{
    var oldVersion = Path.Combine(temporaryRoot, "1.2.9");
    var newVersion = Path.Combine(temporaryRoot, "1.10.0");
    var futureVersion = Path.Combine(temporaryRoot, "1.10.0.12345");
    var invalidVersion = Path.Combine(temporaryRoot, "latest");
    Directory.CreateDirectory(oldVersion);
    Directory.CreateDirectory(newVersion);
    Directory.CreateDirectory(futureVersion);
    Directory.CreateDirectory(invalidVersion);
    File.WriteAllText(Path.Combine(oldVersion, "xstarter.exe"), "old");
    File.WriteAllText(Path.Combine(newVersion, "xstarter.exe"), "new");
    File.WriteAllText(Path.Combine(futureVersion, "xstarter.exe"), "future");
    File.WriteAllText(Path.Combine(invalidVersion, "xstarter.exe"), "invalid");
    var gameExecutable = Path.Combine(
        temporaryRoot, "InfinityNikki", "X6Game", "Binaries", "Win64", "X6Game-Win64-Shipping.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(gameExecutable)!);
    File.WriteAllText(gameExecutable, "game");

    var resolvedExecutable = ExecutableResolver.Resolve(
        temporaryRoot,
        new ExecutableDiscoveryConfiguration
        {
            RelativePath = "xstarter.exe",
            SearchVersionDirectories = true,
        });
    Check(resolvedExecutable == Path.Combine(futureVersion, "xstarter.exe"),
        "highest dotted numeric version directory should be selected, including more than four components");

    ExpectConfigurationError(
        () => ExecutableResolver.Resolve(
            temporaryRoot,
            new ExecutableDiscoveryConfiguration
            {
                RelativePath = "..\\outside.exe",
                SearchVersionDirectories = false,
            }),
        "relative executable path must not escape launcherRoot");

    var configurationPath = Path.Combine(temporaryRoot, "bridge.json");
    File.WriteAllText(configurationPath, "{}");
    var parsed = CommandLine.Parse([
        "--config", configurationPath,
        "C:\\SteamLibrary\\placeholder.exe", "-some-original-argument"
    ]);
    Check(parsed.ConfigurationPath == configurationPath,
        "Steam-expanded trailing command tokens should be ignored");

    var autoParsed = CommandLine.Parse([
        "--auto", "C:\\SteamLibrary\\placeholder.exe", "-some-original-argument"
    ]);
    Check(autoParsed.AutoDiscover && autoParsed.ConfigurationPath is null,
        "auto mode should ignore Steam-expanded trailing command tokens");

    var emptyParsed = CommandLine.Parse([]);
    Check(emptyParsed.AutoDiscover && emptyParsed.ConfigurationPath is null,
        "an empty command line should default to auto discovery");

    var positionalParsed = CommandLine.Parse(["C:\\SteamLibrary\\placeholder.exe"]);
    Check(positionalParsed.AutoDiscover,
        "a Steam-provided positional command should default to auto discovery");

    var autoConfiguration = InfinityNikkiAutoDiscovery.Discover(
        temporaryRoot,
        includeSystemLocations: false);
    Check(autoConfiguration.ExecutablePath == Path.Combine(futureVersion, "xstarter.exe"),
        "auto discovery should select the newest xstarter beside the bridge");
    Check(autoConfiguration.WorkingDirectory == Path.TrimEndingDirectorySeparator(temporaryRoot),
        "auto discovery should use the launcher root as working directory");
    Check(autoConfiguration.Arguments.SequenceEqual(["-skiplauncher"]),
        "auto discovery should preserve the official skip-launcher argument");
    Check(autoConfiguration.WatchExecutablePaths.SequenceEqual([gameExecutable], StringComparer.OrdinalIgnoreCase),
        "auto discovery should resolve the exact game executable path");

    var resolvedConfiguration = BridgeConfigurationResolver.Resolve(new BridgeConfiguration
    {
        LauncherRoot = temporaryRoot,
        Executable = new ExecutableDiscoveryConfiguration
        {
            RelativePath = "xstarter.exe",
            SearchVersionDirectories = true,
        },
        WorkingDirectory = ".",
        WatchProcessNames = ["X6Game-Win64-Shipping.exe", "x6game-win64-shipping"],
    });
    Check(resolvedConfiguration.WatchProcessNames.Count == 1
        && resolvedConfiguration.WatchProcessNames[0] == "X6Game-Win64-Shipping",
        "watch process names should be normalized and deduplicated");
    Check(resolvedConfiguration.WorkingDirectory == Path.TrimEndingDirectorySeparator(temporaryRoot),
        "dot working directory should resolve to launcherRoot");
    Check(resolvedConfiguration.LogPath == BridgeLogger.DefaultLogPath,
        "resolved configuration should always log beside the bridge executable");
    Check(Path.GetDirectoryName(resolvedConfiguration.LogPath)!.EndsWith(
        "InfinityNikkiLaunchBridge-log", StringComparison.Ordinal),
        "logger should use the repository-standard sibling log directory");

    var retentionLog = Path.Combine(temporaryRoot, "retention-test.log");
    var oldTimestamp = DateTimeOffset.Now.AddDays(-8).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    var recentTimestamp = DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    File.WriteAllLines(retentionLog,
    [
        $"{oldTimestamp} [INFO] expired entry",
        "expired continuation",
        $"{recentTimestamp} [INFO] retained entry",
    ]);
    var retentionLogger = new BridgeLogger(retentionLog);
    retentionLogger.Info("test.multiline", "first line\nsecond line");
    var retentionContent = File.ReadAllText(retentionLog);
    Check(!retentionContent.Contains("expired entry", StringComparison.Ordinal)
        && !retentionContent.Contains("expired continuation", StringComparison.Ordinal),
        "logger should delete entries older than seven days, including continuation lines");
    Check(retentionContent.Contains("retained entry", StringComparison.Ordinal),
        "logger should retain entries newer than seven days");
    Check(retentionContent.Contains("first line\\nsecond line", StringComparison.Ordinal),
        "logger should keep multiline diagnostics in one physical log line");

    var testExecutable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot resolve test process path.");
    Check(Path.GetFileNameWithoutExtension(testExecutable).Contains("InfinityNikkiLaunchBridge.Tests", StringComparison.Ordinal),
        "test must run through its apphost for the lifecycle test");
    var processProvider = new ProcessSnapshotProvider();
    var currentProcessName = Path.GetFileNameWithoutExtension(testExecutable);
    var matchingSnapshot = processProvider.GetSnapshot([currentProcessName], [testExecutable]);
    Check(matchingSnapshot.MatchingProcessIds.Contains(Environment.ProcessId),
        "process tracking should match the current process by its exact executable path");
    var mismatchedSnapshot = processProvider.GetSnapshot(
        [currentProcessName],
        [Path.Combine(temporaryRoot, Path.GetFileName(testExecutable))]);
    Check(!mismatchedSnapshot.MatchingProcessIds.Contains(Environment.ProcessId),
        "process tracking should reject a same-name process at a different path");

    var coordinatorConfiguration = new ResolvedBridgeConfiguration(
        configurationPath,
        temporaryRoot,
        testExecutable,
        ["--fake-game"],
        temporaryRoot,
        false,
        [Path.GetFileNameWithoutExtension(testExecutable)],
        [testExecutable],
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        false,
        Path.Combine(temporaryRoot, "bridge-test.log"));

    var stopwatch = Stopwatch.StartNew();
    var exitCode = await new BridgeCoordinator(
        new ProcessSnapshotProvider(),
        new BridgeLogger(coordinatorConfiguration.LogPath))
        .RunAsync(coordinatorConfiguration);
    stopwatch.Stop();
    Check(exitCode == BridgeExitCode.Success, "coordinator should complete successfully");
    Check(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.4),
        "bridge must remain alive until the watched child process exits");
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} of {checks + failures.Count} checks failed:");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine($"All {checks} InfinityNikkiLaunchBridge checks passed.");
}
