using System.Diagnostics;
using System.Text;
using Calabiyau.LaunchBridge;

if (args.SequenceEqual(["--exit-code-test"])) return 42;

var failures = new List<string>();
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) failures.Add(message);
}

void ExpectBridgeError(Action action, string message)
{
    try { action(); failures.Add(message); }
    catch (BridgeException) { checks++; }
}

void WriteOfficialLaunchLog(
    string installRoot,
    string wrapperPath,
    string argumentSeparator = ", ",
    params string[] launchArguments)
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var logDirectory = Path.Combine(installRoot, "log");
    Directory.CreateDirectory(logDirectory);
    var renderedArguments = string.Join(argumentSeparator, launchArguments.Select(argument => $"\"{argument}\""));
    var line = $"10-10 12:00:00.000: Debug/(0): start game: \"{wrapperPath.Replace('\\', '/')}\"  args: ({renderedArguments})";
    File.WriteAllText(Path.Combine(logDirectory, "launcher.log"), line, Encoding.GetEncoding(936));
}

var root = Path.Combine(Path.GetTempPath(), $"calabiyau-launch-bridge-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var standard = Path.Combine(root, "standard", "CalabiYau");
    var standardLauncher = Path.Combine(standard, "CalabiYau.exe");
    var standardWrapper = Path.Combine(standard, "Calabiyau", "CalabiyauGame", "Calabiyau.exe");
    var standardGame = Path.Combine(standard, "Calabiyau", "CalabiyauGame", "PM", "Binaries", "Win64", "Calabiyau-Win64-Shipping.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(standardGame)!);
    Directory.CreateDirectory(Path.Combine(standard, "UpdateGUI"));
    File.WriteAllText(standardLauncher, "launcher");
    File.WriteAllText(Path.Combine(standard, "config.ini"), "GamePath=Calabiyau/CalabiyauGame");
    File.WriteAllText(standardWrapper, "wrapper");
    File.WriteAllText(standardGame, "game");
    WriteOfficialLaunchLog(standard, standardWrapper, ", ",
        "-Channel=channel=PCCNGF80001", "-dx12", "-Lanuch=IdsCn", "-AdsPath=fixture");

    var options = CommandLineOptions.Parse(["--install-root", standard, "C:\\Steam\\original.exe", "-ignored"]);
    Check(options.SteamCommandTailIgnored, "Steam command tail should be ignored");
    var standardResolved = BridgeDiscovery.Resolve(options, standard);
    Check(standardResolved.LauncherPath.Equals(standardWrapper, StringComparison.OrdinalIgnoreCase),
        "standard layout should select the nested game wrapper instead of the root official launcher");
    Check(standardResolved.SdkLauncherPath.Equals(standardLauncher, StringComparison.OrdinalIgnoreCase),
        "standard layout should select the root official launcher for SDK initialization");
    Check(standardResolved.SdkInitializationDelay == TimeSpan.FromSeconds(12) && standardResolved.MinimizeSdkLauncherToTray,
        "SDK initialization should default to a 12-second warmup with launcher tray minimization");
    Check(standardResolved.ExitDelay == TimeSpan.FromSeconds(3),
        "game exit debounce should default to three seconds");
    Check(standardResolved.GameExecutablePaths.SequenceEqual([standardGame], StringComparer.OrdinalIgnoreCase),
        "standard layout should find the Shipping executable");
    Check(standardResolved.LauncherArguments.SequenceEqual(
            ["-Channel=channel=PCCNGF80001", "-dx12", "-Lanuch=IdsCn", "-AdsPath=fixture"]),
        "standard layout should reuse the latest validated official direct-launch arguments");
    Check(!standardResolved.RunAsAdministrator,
        "default launch should preserve the non-elevated official shortcut context");

    var tencent = Path.Combine(root, "tencent", "CalabiYau");
    var tencentLauncher = Path.Combine(tencent, "CalabiYau.exe");
    var tencentWrapper = Path.Combine(tencent, "Calabiyau", "CalabiyauGame", "Calabiyau.exe");
    var tencentGame = Path.Combine(tencent, "Calabiyau", "CalabiyauGame", "PM", "Binaries", "Win64", "Calabiyau-Win64-Shipping.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(tencentGame)!);
    Directory.CreateDirectory(Path.Combine(tencent, "Tenprotect"));
    Directory.CreateDirectory(Path.Combine(tencent, "UpdateGUI"));
    File.WriteAllText(tencentLauncher, "launcher");
    File.WriteAllText(Path.Combine(tencent, "config.ini"), "GamePath=Calabiyau/CalabiyauGame");
    File.WriteAllText(Path.Combine(tencent, "Tenprotect", "TASLogin64.exe"), "login");
    File.WriteAllText(tencentWrapper, "wrapper");
    File.WriteAllText(tencentGame, "game");
    WriteOfficialLaunchLog(tencent, tencentWrapper, " ",
        "-Channel=channel=TENCENT", "-dx11");
    var tencentResolved = BridgeDiscovery.Resolve(
        CommandLineOptions.Parse(["--install-root", tencent]), tencent);
    Check(tencentResolved.LauncherPath.Equals(tencentWrapper, StringComparison.OrdinalIgnoreCase),
        "Tencent layout should select its nested game wrapper");
    Check(tencentResolved.SdkLauncherPath.Equals(tencentLauncher, StringComparison.OrdinalIgnoreCase),
        "Tencent layout should select its own root launcher for SDK initialization");
    Check(tencentResolved.GameExecutablePaths.SequenceEqual([tencentGame], StringComparer.OrdinalIgnoreCase),
        "Tencent layout should find the known game structure without depending on Tenprotect");
    Check(tencentResolved.LauncherArguments.SequenceEqual(
            ["-Channel=channel=TENCENT", "-dx11"]),
        "Tencent layout should accept the official channel-only launch record");

    var custom = Path.Combine(root, "custom");
    var customSdkLauncher = Path.Combine(custom, "SdkLauncher.exe");
    var customLauncher = Path.Combine(custom, "entry", "NewLauncher.exe");
    var customGame = Path.Combine(custom, "content", "NewGame.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(customLauncher)!);
    Directory.CreateDirectory(Path.GetDirectoryName(customGame)!);
    File.WriteAllText(customLauncher, "launcher");
    File.WriteAllText(customSdkLauncher, "sdk-launcher");
    File.WriteAllText(customGame, "game");
    File.WriteAllText(Path.Combine(custom, BridgeDiscovery.ConfigurationFileName), """
        {
          "launcherExecutableNames": ["NewLauncher.exe"],
          "launcherRelativePaths": ["entry/NewLauncher.exe"],
          "sdkLauncherExecutableNames": ["SdkLauncher.exe"],
          "sdkLauncherRelativePaths": ["SdkLauncher.exe"],
          "sdkInitializationDelaySeconds": 3,
          "minimizeSdkLauncherToTray": false,
          "gameExecutableNames": ["NewGame.exe"],
          "launcherArguments": ["--channel", "custom"],
          "maxSearchDepth": 4,
          "runAsAdministrator": false
        }
        """);
    var customResolved = BridgeDiscovery.Resolve(CommandLineOptions.Parse(["--install-root", custom]), custom);
    Check(customResolved.LauncherPath == customLauncher && customResolved.GameExecutablePaths.Contains(customGame),
        "configuration should override launcher and game names");
    Check(customResolved.SdkLauncherPath == customSdkLauncher
            && customResolved.SdkInitializationDelay == TimeSpan.FromSeconds(3)
            && !customResolved.MinimizeSdkLauncherToTray,
        "configuration should override SDK launcher discovery and warmup behavior");
    Check(customResolved.LauncherArguments.SequenceEqual(["--channel", "custom"]),
        "configuration should override captured direct-launch arguments");

    ExpectBridgeError(
        () => BridgeDiscovery.Resolve(CommandLineOptions.Parse(["--install-root", Path.Combine(root, "missing")]), root),
        "missing install root should fail");

    var missingGame = Path.Combine(root, "missing-game");
    Directory.CreateDirectory(missingGame);
    File.WriteAllText(Path.Combine(missingGame, "CalabiYau.exe"), "launcher");
    ExpectBridgeError(
        () => BridgeDiscovery.Resolve(CommandLineOptions.Parse(["--install-root", missingGame]), missingGame),
        "launcher-only installation should fail validation");

    Check(LogSanitizer.FormatArguments(["-Channel=channel-id", "-Lanuch=source", "-AdsPath=encoded-path", "--token", "secret", "-dx12"])
            == "-Channel=<redacted> -Lanuch=<redacted> -AdsPath=<redacted> --token <redacted> -dx12",
        "logs should redact dynamic login and token values");

    var retentionPath = Path.Combine(root, "retention.log");
    File.WriteAllLines(retentionPath,
    [
        $"{DateTimeOffset.Now.AddDays(-8):O} [INFO] expired",
        "expired continuation",
        $"{DateTimeOffset.Now.AddDays(-1):O} [INFO] retained",
    ]);
    var logger = new BridgeLogger(retentionPath);
    logger.Info("test.stack", "first\nsecond");
    var content = File.ReadAllText(retentionPath);
    Check(!content.Contains("expired", StringComparison.Ordinal) && content.Contains("retained", StringComparison.Ordinal),
        "logger should prune entries older than seven days and retain recent entries");
    Check(content.Contains("first\\nsecond", StringComparison.Ordinal),
        "logger should keep multiline stack information in one physical line");

    var executable = Environment.ProcessPath!;
    var provider = new ProcessSnapshotProvider();
    var name = Path.GetFileNameWithoutExtension(executable);
    Check(provider.GetSnapshot([name], [executable]).MatchingProcessIds.Contains(Environment.ProcessId),
        "process tracking should match the exact executable path");
    Check(!provider.GetSnapshot([name], [Path.Combine(root, Path.GetFileName(executable))]).MatchingProcessIds.Contains(Environment.ProcessId),
        "process tracking should reject a same-name process at another path");

    using var child = Process.Start(new ProcessStartInfo(executable, "--exit-code-test") { UseShellExecute = false })!;
    using var exitTracker = ProcessExitTracker.TryOpen(child.Id);
    child.WaitForExit();
    Check(exitTracker?.ReadExitCode() == "42",
        "exit tracker should retain the real process exit code after the process disappears");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} of {checks + failures.Count} checks failed:");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}
Console.WriteLine($"All {checks} CalabiyauLaunchBridge checks passed.");
return 0;
