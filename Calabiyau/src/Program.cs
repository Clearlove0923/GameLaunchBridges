using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Calabiyau.LaunchBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var noDialog = args.Contains("--no-dialog", StringComparer.Ordinal);
        var logger = new BridgeLogger();
        var stopwatch = Stopwatch.StartNew();
        var exitCode = BridgeExitCode.UnexpectedError;
        logger.Info("session.start",
            $"程序启动。version={typeof(Program).Assembly.GetName().Version}；baseDirectory={AppContext.BaseDirectory}；"
            + $"currentDirectory={Environment.CurrentDirectory}；os={RuntimeInformation.OSDescription}；"
            + $"processArchitecture={RuntimeInformation.ProcessArchitecture}；osArchitecture={RuntimeInformation.OSArchitecture}；"
            + $"runtime={Environment.Version}；arguments={LogSanitizer.FormatArguments(args)}；log={logger.LogPath}");

        if (logger.WriteFailure is not null)
        {
            FailureNotifier.Show("无法创建或写入桥接器日志。", logger, noDialog);
            return BridgeExitCode.LogUnavailable;
        }

        try
        {
            var options = CommandLineOptions.Parse(args);
            logger.Info("command.parsed",
                $"installRoot={options.InstallRoot ?? "<auto>"}；validateOnly={options.ValidateOnly}；"
                + $"attachOnly={options.AttachOnly}；noDialog={options.NoDialog}；steamCommandTailIgnored={options.SteamCommandTailIgnored}");
            if (options.IgnoredArguments.Count > 0)
                logger.Info("command.arguments_ignored",
                    $"已忽略无法识别的启动参数或 Steam 尾随命令：{LogSanitizer.FormatArguments(options.IgnoredArguments)}");
            var resolved = BridgeDiscovery.Resolve(options, AppContext.BaseDirectory, logger);
            logger.Info("configuration.resolved",
                $"source={resolved.ConfigurationPath}；installRoot={resolved.InstallRoot}；directEntry={resolved.LauncherPath}；"
                + $"sdkLauncher={resolved.SdkLauncherPath}；sdkInitializationDelay={resolved.SdkInitializationDelay}；"
                + $"workingDirectory={resolved.WorkingDirectory}；launchArguments={LogSanitizer.FormatArguments(resolved.LauncherArguments)}；"
                + $"gamePaths={string.Join(';', resolved.GameExecutablePaths)}；startupTimeout={resolved.StartupTimeout}；"
                + $"pollInterval={resolved.PollInterval}；exitDelay={resolved.ExitDelay}；runAsAdministrator={resolved.RunAsAdministrator}");

            if (!options.ValidateOnly
                && resolved.MinimizeSdkLauncherToTray
                && !options.ElevatedWorker
                && !ElevationCoordinator.IsElevated())
            {
                exitCode = await ElevationCoordinator.RunElevatedWorkerAsync(args, logger);
                return exitCode;
            }

            using var instanceLock = BridgeInstanceLock.TryAcquire(resolved.InstallRoot);
            if (!instanceLock.Acquired)
            {
                throw new BridgeException(BridgeExitCode.AlreadyRunning, "同一安装目录的桥接器已经在运行。");
            }

            if (options.ValidateOnly)
            {
                logger.Info("validation.success", "路径发现与配置验证通过。");
                exitCode = BridgeExitCode.Success;
            }
            else
            {
                exitCode = await new BridgeCoordinator(new ProcessSnapshotProvider(), logger)
                    .RunAsync(resolved, options.AttachOnly);
            }
        }
        catch (BridgeException ex)
        {
            logger.Exception("bridge.error", ex, "桥接器无法继续");
            FailureNotifier.Show(ex.Message, logger, noDialog);
            exitCode = ex.ExitCode;
        }
        catch (OperationCanceledException ex)
        {
            logger.Exception("session.cancelled", ex, "操作已取消");
            exitCode = BridgeExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            logger.Exception("session.unhandled_exception", ex, "未处理错误");
            FailureNotifier.Show("桥接器发生未处理错误，请查看运行日志。\n\n" + ex.Message, logger, noDialog);
            exitCode = BridgeExitCode.UnexpectedError;
        }
        finally
        {
            stopwatch.Stop();
            logger.Info("session.end", $"程序结束。exitCode={exitCode}；elapsed={stopwatch.Elapsed}");
        }

        return exitCode;
    }
}

public static class BridgeExitCode
{
    public const int Success = 0;
    public const int InvalidConfiguration = 2;
    public const int AlreadyRunning = 3;
    public const int GameAlreadyRunning = 4;
    public const int StartupTimeout = 5;
    public const int LaunchFailed = 6;
    public const int LogUnavailable = 7;
    public const int Cancelled = 8;
    public const int UnexpectedError = 10;
}

public static class ElevationCoordinator
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<int> RunElevatedWorkerAsync(string[] originalArguments, BridgeLogger logger)
    {
        var executable = Environment.ProcessPath
            ?? throw new BridgeException(BridgeExitCode.LaunchFailed, "无法确定桥接器自身路径，不能启动高权限工作进程。");
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas",
        };
        startInfo.ArgumentList.Add("--elevated-worker");
        foreach (var argument in originalArguments.Where(value =>
                     !value.Equals("--elevated-worker", StringComparison.Ordinal)))
            startInfo.ArgumentList.Add(argument);

        logger.Info("elevation.worker_start_request",
            "准备启动高权限工作进程以控制官方启动器窗口；父进程将持续等待并维持 Steam 生命周期。");
        try
        {
            using var worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start 未返回高权限工作进程对象。");
            logger.Info("elevation.worker_started", $"高权限工作进程已启动。PID={worker.Id}");
            await worker.WaitForExitAsync();
            logger.Info("elevation.worker_exited", $"高权限工作进程已退出。PID={worker.Id}；exitCode={worker.ExitCode}");
            return worker.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new BridgeException(BridgeExitCode.Cancelled, "用户取消了桥接器的权限请求。", ex);
        }
        catch (BridgeException) { throw; }
        catch (Exception ex)
        {
            throw new BridgeException(BridgeExitCode.LaunchFailed, "无法启动高权限桥接工作进程。", ex);
        }
    }
}

public sealed class BridgeException : Exception
{
    public int ExitCode { get; }
    public BridgeException(int exitCode, string message) : base(message) => ExitCode = exitCode;
    public BridgeException(int exitCode, string message, Exception innerException) : base(message, innerException) => ExitCode = exitCode;
}

public sealed record CommandLineOptions(
    string? InstallRoot,
    bool ValidateOnly,
    bool AttachOnly,
    bool NoDialog,
    int? StartupTimeoutSeconds,
    int? ExitDelaySeconds,
    IReadOnlyList<string> IgnoredArguments,
    bool SteamCommandTailIgnored,
    bool ElevatedWorker)
{
    public static CommandLineOptions Parse(string[] args)
    {
        string? installRoot = null;
        bool validateOnly = false, attachOnly = false, noDialog = false, steamTail = false, elevatedWorker = false;
        int? startupTimeout = null, exitDelay = null;
        var ignoredArguments = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--install-root" when i + 1 < args.Length:
                    installRoot = args[++i];
                    break;
                case "--install-root":
                    throw MissingValue("--install-root");
                case "--validate":
                case "--dry-run":
                    validateOnly = true;
                    break;
                case "--attach-only":
                    attachOnly = true;
                    break;
                case "--no-dialog":
                    noDialog = true;
                    break;
                case "--elevated-worker":
                    elevatedWorker = true;
                    break;
                case "--startup-timeout-seconds" when i + 1 < args.Length:
                    startupTimeout = ParseRange(args[++i], 1, 3600, "--startup-timeout-seconds");
                    break;
                case "--startup-timeout-seconds":
                    throw MissingValue("--startup-timeout-seconds");
                case "--exit-delay-seconds" when i + 1 < args.Length:
                    exitDelay = ParseRange(args[++i], 0, 300, "--exit-delay-seconds");
                    break;
                case "--exit-delay-seconds":
                    throw MissingValue("--exit-delay-seconds");
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        ignoredArguments.Add(args[i]);
                        break;
                    }

                    steamTail = true;
                    ignoredArguments.AddRange(args[i..]);
                    i = args.Length;
                    break;
            }
        }

        return new(installRoot, validateOnly, attachOnly, noDialog, startupTimeout, exitDelay,
            ignoredArguments, steamTail, elevatedWorker);
    }

    private static BridgeException MissingValue(string name) =>
        new(BridgeExitCode.InvalidConfiguration, $"{name} 缺少参数值。");

    private static int ParseRange(string value, int minimum, int maximum, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
            throw new BridgeException(BridgeExitCode.InvalidConfiguration,
                $"{name} 必须为 {minimum} 到 {maximum} 之间的整数。");
        return parsed;
    }
}

public sealed record BridgeConfigurationDocument(
    string[]? LauncherExecutableNames,
    string[]? LauncherRelativePaths,
    string[]? GameExecutableNames,
    string[]? LauncherArguments,
    string[]? ExcludedDirectoryNames,
    string[]? PreferredLauncherPathKeywords,
    string[]? PreferredGamePathKeywords,
    int? MaxSearchDepth,
    int? StartupTimeoutSeconds,
    int? PollIntervalMilliseconds,
    int? ExitDelaySeconds,
    bool? RunAsAdministrator,
    string[]? SdkLauncherExecutableNames,
    string[]? SdkLauncherRelativePaths,
    int? SdkInitializationDelaySeconds,
    bool? MinimizeSdkLauncherToTray);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(BridgeConfigurationDocument))]
internal partial class BridgeJsonContext : JsonSerializerContext;

public sealed record ResolvedBridgeConfiguration(
    string ConfigurationPath,
    string InstallRoot,
    string LauncherPath,
    IReadOnlyList<string> LauncherArguments,
    string WorkingDirectory,
    IReadOnlyList<string> GameProcessNames,
    IReadOnlyList<string> GameExecutablePaths,
    TimeSpan StartupTimeout,
    TimeSpan PollInterval,
    TimeSpan ExitDelay,
    bool RunAsAdministrator,
    string SdkLauncherPath,
    TimeSpan SdkInitializationDelay,
    bool MinimizeSdkLauncherToTray);

public static class BridgeDiscovery
{
    public const string ConfigurationFileName = "CalabiyauLaunchBridge.json";
    private static readonly string[] DefaultLauncherNames = ["CalabiYau.exe"];
    private static readonly string[] DefaultGameNames = ["Calabiyau-Win64-Shipping.exe"];
    private static readonly string[] DefaultExcludedDirectories =
        ["CalabiyauLaunchBridge-log", "UpdateGUI", "Cache", "CrashSightLog", "GCloudSDKLog", "WebView2RT"];
    private static readonly string[] DefaultLauncherKeywords = ["CalabiyauGame", "Calabiyau"];
    private static readonly string[] DefaultGameKeywords = ["PM", "Binaries", "Win64"];

    public static ResolvedBridgeConfiguration Resolve(
        CommandLineOptions options,
        string baseDirectory,
        BridgeLogger? logger = null)
    {
        var installRoot = ResolveInstallRoot(options.InstallRoot, baseDirectory, logger);

        var configPath = Path.Combine(baseDirectory, ConfigurationFileName);
        var document = LoadConfiguration(configPath);
        var launcherNames = NormalizeExecutableNames(document.LauncherExecutableNames, DefaultLauncherNames, "launcherExecutableNames");
        var sdkLauncherNames = NormalizeExecutableNames(
            document.SdkLauncherExecutableNames, DefaultLauncherNames, "sdkLauncherExecutableNames");
        var gameNames = NormalizeExecutableNames(document.GameExecutableNames, DefaultGameNames, "gameExecutableNames");
        var excluded = NormalizeValues(document.ExcludedDirectoryNames, DefaultExcludedDirectories, allowEmpty: true);
        var launcherKeywords = NormalizeValues(document.PreferredLauncherPathKeywords, DefaultLauncherKeywords, allowEmpty: true);
        var gameKeywords = NormalizeValues(document.PreferredGamePathKeywords, DefaultGameKeywords, allowEmpty: true);
        var maxDepth = document.MaxSearchDepth ?? 12;
        if (maxDepth is < 1 or > 32)
            throw new BridgeException(BridgeExitCode.InvalidConfiguration, "maxSearchDepth 必须为 1 到 32 之间的整数。");

        var candidates = FindExecutables(installRoot, launcherNames, excluded, maxDepth).ToList();
        foreach (var relative in document.LauncherRelativePaths ?? [])
        {
            var fullPath = SafeCombine(installRoot, relative, "launcherRelativePaths");
            if (File.Exists(fullPath)) candidates.Add(fullPath);
        }
        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var candidate in candidates)
            logger?.Info("discovery.entry_candidate", DescribeFile(candidate));
        var launcher = candidates
            .OrderBy(path => GetLauncherScore(path, installRoot, launcherKeywords))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new BridgeException(BridgeExitCode.InvalidConfiguration,
                $"在 {installRoot} 内找不到游戏包装入口。候选文件名：{string.Join(", ", launcherNames)}。");

        var sdkCandidates = FindExecutables(installRoot, sdkLauncherNames, excluded, maxDepth).ToList();
        foreach (var relative in document.SdkLauncherRelativePaths ?? [])
        {
            var fullPath = SafeCombine(installRoot, relative, "sdkLauncherRelativePaths");
            if (File.Exists(fullPath)) sdkCandidates.Add(fullPath);
        }
        var sdkLauncher = sdkCandidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetSdkLauncherScore(path, installRoot))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => !path.Equals(launcher, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeException(BridgeExitCode.InvalidConfiguration,
                $"在 {installRoot} 内找不到用于初始化 SDK 的根目录官方入口。候选文件名：{string.Join(", ", sdkLauncherNames)}。");
        logger?.Info("discovery.sdk_launcher", DescribeFile(sdkLauncher));

        var gamePaths = FindExecutables(installRoot, gameNames, excluded, maxDepth)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetGameScore(path, installRoot, gameKeywords))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var candidate in gamePaths)
            logger?.Info("discovery.game_candidate", DescribeFile(candidate));
        if (gamePaths.Length == 0)
            throw new BridgeException(BridgeExitCode.InvalidConfiguration,
                $"在 {installRoot} 内找不到游戏本体。请先通过官方启动器完成下载或更新。候选文件名：{string.Join(", ", gameNames)}。");

        var configuredArguments = document.LauncherArguments?
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        var launchArguments = configuredArguments.Length > 0
            ? configuredArguments
            : OfficialLaunchRecordResolver.Resolve(installRoot, launcher, logger).Arguments;
        logger?.Info("discovery.selected",
            $"directEntry={DescribeFile(launcher)}；game={DescribeFile(gamePaths[0])}；"
            + $"arguments={LogSanitizer.FormatArguments(launchArguments)}");
        var startupSeconds = options.StartupTimeoutSeconds ?? document.StartupTimeoutSeconds ?? 180;
        var exitDelaySeconds = options.ExitDelaySeconds ?? document.ExitDelaySeconds ?? 3;
        var pollMilliseconds = document.PollIntervalMilliseconds ?? 1000;
        var sdkInitializationSeconds = document.SdkInitializationDelaySeconds ?? 12;
        if (startupSeconds is < 1 or > 3600 || exitDelaySeconds is < 0 or > 300 || pollMilliseconds is < 100 or > 10000
            || sdkInitializationSeconds is < 1 or > 120)
            throw new BridgeException(BridgeExitCode.InvalidConfiguration, "配置中的超时、轮询或退出防抖数值超出允许范围。");

        return new(
            File.Exists(configPath) ? configPath : "<built-in>",
            installRoot,
            launcher,
            launchArguments,
            Path.GetDirectoryName(launcher)!,
            gameNames.Select(name => Path.GetFileNameWithoutExtension(name)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            gamePaths,
            TimeSpan.FromSeconds(startupSeconds),
            TimeSpan.FromMilliseconds(pollMilliseconds),
            TimeSpan.FromSeconds(exitDelaySeconds),
            document.RunAsAdministrator ?? false,
            sdkLauncher,
            TimeSpan.FromSeconds(sdkInitializationSeconds),
            document.MinimizeSdkLauncherToTray ?? true);
    }

    public static string ResolveInstallRoot(
        string? explicitInstallRoot,
        string baseDirectory,
        BridgeLogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitInstallRoot))
        {
            var explicitRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(explicitInstallRoot));
            if (!Directory.Exists(explicitRoot))
                throw new BridgeException(BridgeExitCode.InvalidConfiguration, $"安装目录不存在：{explicitRoot}");
            logger?.Info("discovery.install_root", $"source=explicit；selected={explicitRoot}");
            return explicitRoot;
        }

        var bridgeDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        if (!Directory.Exists(bridgeDirectory))
            throw new BridgeException(BridgeExitCode.InvalidConfiguration, $"桥接器所在目录不存在：{bridgeDirectory}");

        for (var candidate = new DirectoryInfo(bridgeDirectory); candidate is not null; candidate = candidate.Parent)
        {
            if (!LooksLikeOfficialInstallRoot(candidate.FullName)) continue;
            var selected = Path.TrimEndingDirectorySeparator(candidate.FullName);
            logger?.Info("discovery.install_root",
                $"source=bridge-ancestor；bridgeDirectory={bridgeDirectory}；selected={selected}");
            return selected;
        }

        throw new BridgeException(BridgeExitCode.InvalidConfiguration,
            $"无法从桥接器所在目录向上识别《卡拉彼丘》安装根目录：{bridgeDirectory}。"
            + "请把桥接器放在游戏安装目录内的任意位置，或使用 --install-root 显式指定安装根目录。");
    }

    private static bool LooksLikeOfficialInstallRoot(string directory)
    {
        var hasOfficialEntry = DefaultLauncherNames.Any(name => File.Exists(Path.Combine(directory, name)));
        if (!hasOfficialEntry) return false;
        return Directory.Exists(Path.Combine(directory, "log"))
               || Directory.Exists(Path.Combine(directory, "Calabiyau"))
               || File.Exists(Path.Combine(directory, "config.ini"));
    }

    public static IReadOnlyList<string> FindExecutables(
        string root,
        IReadOnlyList<string> executableNames,
        IReadOnlyList<string> excludedDirectoryNames,
        int maxDepth)
    {
        var result = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.GetFullPath(root), 0));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(current.Path, executableName);
                if (File.Exists(candidate)) result.Add(Path.GetFullPath(candidate));
            }
            if (current.Depth >= maxDepth) continue;

            string[] directories;
            try { directories = Directory.GetDirectories(current.Path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
            {
                try
                {
                    var attributes = File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (excludedDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)) continue;
                    pending.Push((directory, current.Depth + 1));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    private static BridgeConfigurationDocument LoadConfiguration(string path)
    {
        if (!File.Exists(path)) return new(
            null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null);
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(path, Encoding.UTF8),
                BridgeJsonContext.Default.BridgeConfigurationDocument)
                ?? throw new InvalidDataException("配置文件为空。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new BridgeException(BridgeExitCode.InvalidConfiguration, $"无法读取配置文件 {path}：{ex.Message}", ex);
        }
    }

    private static string[] NormalizeExecutableNames(string[]? values, string[] defaults, string field)
    {
        var normalized = NormalizeValues(values, defaults, allowEmpty: false);
        foreach (var value in normalized)
        {
            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                throw new BridgeException(BridgeExitCode.InvalidConfiguration, $"{field} 只能包含不带路径的 EXE 文件名：{value}");
        }
        return normalized;
    }

    private static string[] NormalizeValues(string[]? values, string[] defaults, bool allowEmpty)
    {
        var normalized = (values ?? defaults).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return normalized.Length == 0 && !allowEmpty ? defaults : normalized;
    }

    private static string SafeCombine(string root, string relativePath, string field)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new BridgeException(BridgeExitCode.InvalidConfiguration, $"{field} 必须是安装目录内的相对路径。");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException(BridgeExitCode.InvalidConfiguration, $"{field} 不能离开安装目录：{relativePath}");
        return combined;
    }

    private static int GetLauncherScore(string path, string root, IReadOnlyList<string> keywords)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var score = segments.Length * 100;
        if (Path.GetDirectoryName(path)!.Equals(root, StringComparison.OrdinalIgnoreCase)) score += 100000;
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(Path.Combine(directory, "PM"))) score -= 10000;
        if (segments.Any(segment => segment.Equals("CalabiyauGame", StringComparison.OrdinalIgnoreCase))) score -= 50000;
        if (segments.Any(segment => segment.Equals("Binaries", StringComparison.OrdinalIgnoreCase))) score += 50000;
        for (var i = 0; i < keywords.Count; i++)
            if (segments.Any(segment => segment.Equals(keywords[i], StringComparison.OrdinalIgnoreCase))) score -= 1000 - i * 10;
        return score;
    }

    private static int GetGameScore(string path, string root, IReadOnlyList<string> keywords)
    {
        var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var score = segments.Length * 100;
        for (var i = 0; i < keywords.Count; i++)
            if (segments.Any(segment => segment.Equals(keywords[i], StringComparison.OrdinalIgnoreCase))) score -= 1000 - i * 10;
        return score;
    }

    private static int GetSdkLauncherScore(string path, string root)
    {
        var directory = Path.GetDirectoryName(path)!;
        var score = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length * 100;
        if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) score -= 100000;
        if (File.Exists(Path.Combine(directory, "config.ini"))) score -= 10000;
        if (Directory.Exists(Path.Combine(directory, "UpdateGUI"))) score -= 10000;
        return score;
    }

    private static string DescribeFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return $"path={file.FullName}；size={file.Length}；lastWriteUtc={file.LastWriteTimeUtc:O}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"path={path}；metadataError={ex.GetType().Name}: {ex.Message}";
        }
    }
}

public sealed record OfficialLaunchRecord(string LogPath, string ExecutablePath, IReadOnlyList<string> Arguments);

public static class OfficialLaunchRecordResolver
{
    private static readonly System.Text.RegularExpressions.Regex RecordPattern = new(
        "start game:\\s*\"(?<exe>[^\"]+)\"\\s+args:\\s*\\((?<args>.*)\\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ArgumentPattern = new(
        "\"(?<arg>[^\"]*)\"",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static OfficialLaunchRecord Resolve(string installRoot, string expectedExecutable, BridgeLogger? logger = null)
    {
        var logDirectory = Path.Combine(installRoot, "log");
        if (!Directory.Exists(logDirectory))
            throw CreateMissingRecordError(logDirectory);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var logPath in Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string[] lines;
            try { lines = File.ReadAllLines(logPath, gbk); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                logger?.Warn("launch_record.unreadable", $"path={logPath}；error={ex.GetType().Name}: {ex.Message}");
                continue;
            }

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var match = RecordPattern.Match(lines[i]);
                if (!match.Success) continue;

                string executable;
                try { executable = Path.GetFullPath(match.Groups["exe"].Value); }
                catch (Exception) { continue; }
                if (!executable.Equals(Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsWithinRoot(executable, installRoot) || !File.Exists(executable)) continue;

                var arguments = ArgumentPattern.Matches(match.Groups["args"].Value)
                    .Select(argument => argument.Groups["arg"].Value).ToArray();
                if (!IsExpectedArgumentSet(arguments)) continue;

                logger?.Info("launch_record.selected",
                    $"source={logPath}；entry={executable}；arguments={LogSanitizer.FormatArguments(arguments)}");
                return new(logPath, executable, arguments);
            }
        }

        throw CreateMissingRecordError(logDirectory);
    }

    private static bool IsExpectedArgumentSet(IReadOnlyList<string> arguments)
    {
        if (arguments.Count is < 1 or > 8) return false;
        var hasChannel = false;
        var hasLaunch = false;
        var hasAdsPath = false;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("-Channel=", StringComparison.OrdinalIgnoreCase)) hasChannel = argument.Length > 9;
            else if (argument.StartsWith("-Lanuch=", StringComparison.OrdinalIgnoreCase)) hasLaunch = argument.Length > 8;
            else if (argument.StartsWith("-AdsPath=", StringComparison.OrdinalIgnoreCase)) hasAdsPath = argument.Length > 9;
            else if (!argument.Equals("-dx11", StringComparison.OrdinalIgnoreCase)
                     && !argument.Equals("-dx12", StringComparison.OrdinalIgnoreCase)) return false;
        }
        // The standard launcher supplies the login-source and ads-path pair,
        // while the Tencent launcher keeps that context in its background SDK
        // process and records only -Channel plus an optional renderer switch.
        // Accept either complete shape, but never a partial standard-only pair.
        return hasChannel && hasLaunch == hasAdsPath;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static BridgeException CreateMissingRecordError(string logDirectory) => new(
        BridgeExitCode.InvalidConfiguration,
        $"在 {logDirectory} 中找不到可验证的官方成功启动记录。请先通过官方启动器成功进入游戏一次，再使用桥接器直接启动。");
}

public sealed record ProcessSnapshot(
    IReadOnlySet<int> MatchingProcessIds,
    IReadOnlyDictionary<int, string> MatchingPaths,
    IReadOnlyList<string> IgnoredPaths,
    int UnreadableCount);

public interface IProcessSnapshotProvider
{
    ProcessSnapshot GetSnapshot(IReadOnlyList<string> processNames, IReadOnlyList<string> expectedExecutablePaths);
}

public sealed class ProcessSnapshotProvider : IProcessSnapshotProvider
{
    public ProcessSnapshot GetSnapshot(IReadOnlyList<string> processNames, IReadOnlyList<string> expectedExecutablePaths)
    {
        var expected = expectedExecutablePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        var paths = new Dictionary<int, string>();
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        foreach (var processName in processNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Process[] processes;
            var normalizedProcessName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(processName)
                : processName;
            try { processes = Process.GetProcessesByName(normalizedProcessName); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        var path = TryGetProcessPath(process.Id);
                        if (path is null) { unreadable++; continue; }
                        var fullPath = Path.GetFullPath(path);
                        if (expected.Contains(fullPath)) { ids.Add(process.Id); paths[process.Id] = fullPath; }
                        else ignored.Add(fullPath);
                    }
                    catch { unreadable++; }
                }
            }
        }
        return new(ids, paths, ignored.ToArray(), unreadable);
    }

    private static string? TryGetProcessPath(int processId)
    {
        const uint queryLimitedInformation = 0x1000;
        var handle = OpenProcess(queryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768);
            var capacity = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref capacity) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class BridgeCoordinator
{
    private readonly IProcessSnapshotProvider _processes;
    private readonly BridgeLogger _logger;
    public BridgeCoordinator(IProcessSnapshotProvider processes, BridgeLogger logger) => (_processes, _logger) = (processes, logger);

    public async Task<int> RunAsync(ResolvedBridgeConfiguration configuration, bool attachOnly)
    {
        Process? ownedSdkLauncher = null;
        SdkLauncherTrayController? trayController = null;
        try
        {
            var initial = GetGameSnapshot(configuration);
            LogSnapshot("game.initial", initial);
            if (initial.MatchingProcessIds.Count == 0)
            {
                if (attachOnly)
                    throw new BridgeException(BridgeExitCode.GameAlreadyRunning, "未找到与已发现本体完整路径一致的运行中游戏进程。");

                if (configuration.MinimizeSdkLauncherToTray)
                {
                    trayController = new SdkLauncherTrayController(
                        _processes, configuration.SdkLauncherPath, _logger);
                    trayController.Start();
                }
                ownedSdkLauncher = await EnsureSdkLauncherAsync(configuration, trayController);

                var entryName = Path.GetFileNameWithoutExtension(configuration.LauncherPath);
                var entrySnapshot = _processes.GetSnapshot([entryName], [configuration.LauncherPath]);
                if (entrySnapshot.MatchingProcessIds.Count == 0)
                {
                    var startInfo = new ProcessStartInfo(configuration.LauncherPath)
                    {
                        WorkingDirectory = configuration.WorkingDirectory,
                        UseShellExecute = true,
                        Verb = configuration.RunAsAdministrator ? "runas" : string.Empty,
                    };
                    foreach (var argument in configuration.LauncherArguments) startInfo.ArgumentList.Add(argument);
                    _logger.Info("direct_entry.start_request",
                        $"command={LogSanitizer.FormatCommand(configuration.LauncherPath, configuration.LauncherArguments)}；workingDirectory={configuration.WorkingDirectory}");
                    try
                    {
                        using var entry = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Process.Start 未返回进程对象。");
                        _logger.Info("direct_entry.started", $"游戏包装入口已直接启动。PID={entry.Id}。无需用户点击开始游戏。");
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        throw new BridgeException(BridgeExitCode.Cancelled, "用户取消了游戏包装入口的 UAC 请求。", ex);
                    }
                    catch (Exception ex)
                    {
                        throw new BridgeException(BridgeExitCode.LaunchFailed, "无法直接启动卡拉彼丘游戏包装入口。", ex);
                    }
                }
                else
                {
                    _logger.Info("direct_entry.attached", $"游戏包装入口已运行，PID={string.Join(',', entrySnapshot.MatchingProcessIds)}。");
                }
            }

            var startupDeadline = DateTimeOffset.UtcNow + configuration.StartupTimeout;
            ProcessSnapshot snapshot = initial;
            while (snapshot.MatchingProcessIds.Count == 0 && DateTimeOffset.UtcNow < startupDeadline)
            {
                await Task.Delay(configuration.PollInterval);
                _logger.Maintain();
                snapshot = GetGameSnapshot(configuration);
            }
            if (snapshot.MatchingProcessIds.Count == 0)
                throw new BridgeException(BridgeExitCode.StartupTimeout,
                    $"直接启动后等待游戏进程超时（{configuration.StartupTimeout}）。请先通过官方启动器确认账号、更新和反作弊状态正常。");

            var processHandles = new Dictionary<int, ProcessExitTracker>();
            var previousIds = new HashSet<int>();
            DateTimeOffset? missingSince = null;
            while (true)
            {
                foreach (var id in snapshot.MatchingProcessIds.Except(previousIds))
                {
                    var tracker = ProcessExitTracker.TryOpen(id);
                    if (tracker is not null) processHandles[id] = tracker;
                    _logger.Info("game.appeared", $"PID={id}；path={snapshot.MatchingPaths.GetValueOrDefault(id, "<unknown>")}");
                }
                foreach (var id in previousIds.Except(snapshot.MatchingProcessIds))
                    _logger.Info("game.disappeared", $"PID={id}；exitCode={processHandles.GetValueOrDefault(id)?.ReadExitCode() ?? "<unavailable>"}");

                if (snapshot.MatchingProcessIds.Count == 0)
                {
                    missingSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - missingSince >= configuration.ExitDelay) break;
                }
                else missingSince = null;

                previousIds = snapshot.MatchingProcessIds.ToHashSet();
                await Task.Delay(configuration.PollInterval);
                _logger.Maintain();
                snapshot = GetGameSnapshot(configuration);
            }

            foreach (var pair in processHandles)
            {
                _logger.Info("game.exit", $"PID={pair.Key}；exitCode={pair.Value.ReadExitCode()}");
                pair.Value.Dispose();
            }
            _logger.Info("game.session_complete", $"游戏进程连续消失 {configuration.ExitDelay}，桥接器准备退出。");
            return BridgeExitCode.Success;
        }
        finally
        {
            if (ownedSdkLauncher is not null)
                await StopOwnedSdkLauncherAsync(ownedSdkLauncher);
            if (trayController is not null)
                await trayController.DisposeAsync();
        }
    }

    private async Task<Process?> EnsureSdkLauncherAsync(
        ResolvedBridgeConfiguration configuration,
        SdkLauncherTrayController? trayController)
    {
        var sdkName = Path.GetFileNameWithoutExtension(configuration.SdkLauncherPath);
        var snapshot = _processes.GetSnapshot([sdkName], [configuration.SdkLauncherPath]);
        Process? ownedProcess = null;
        if (snapshot.MatchingProcessIds.Count == 0)
        {
            var workingDirectory = Path.GetDirectoryName(configuration.SdkLauncherPath)!;
            var windowStyle = configuration.MinimizeSdkLauncherToTray
                ? ProcessWindowStyle.Hidden
                : ProcessWindowStyle.Minimized;
            _logger.Info("sdk_launcher.start_request",
                $"command={LogSanitizer.FormatCommand(configuration.SdkLauncherPath, [])}；workingDirectory={workingDirectory}；window={windowStyle}");
            try
            {
                ownedProcess = Process.Start(new ProcessStartInfo(configuration.SdkLauncherPath)
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                    WindowStyle = windowStyle,
                }) ?? throw new InvalidOperationException("Process.Start 未返回进程对象。");
                trayController?.RegisterOwnedProcessId(ownedProcess.Id);
                _logger.Info("sdk_launcher.started", $"后台 SDK 入口已启动。PID={ownedProcess.Id}；owned=true");
            }
            catch (Exception ex)
            {
                throw new BridgeException(BridgeExitCode.LaunchFailed, "无法启动用于初始化 SDK 的官方入口。", ex);
            }
        }
        else
        {
            _logger.Info("sdk_launcher.attached",
                $"检测到路径一致的 SDK 入口。PID={string.Join(',', snapshot.MatchingProcessIds)}；owned=false");
        }

        var deadline = DateTimeOffset.UtcNow + configuration.SdkInitializationDelay;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ownedProcess is not null)
            {
                ownedProcess.Refresh();
                if (ownedProcess.HasExited)
                    throw new BridgeException(BridgeExitCode.LaunchFailed,
                        $"SDK 入口在初始化完成前退出。PID={ownedProcess.Id}；exitCode={ownedProcess.ExitCode}");
            }
            else
            {
                snapshot = _processes.GetSnapshot([sdkName], [configuration.SdkLauncherPath]);
                if (snapshot.MatchingProcessIds.Count == 0)
                    throw new BridgeException(BridgeExitCode.LaunchFailed, "SDK 入口在初始化完成前消失。");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            _logger.Maintain();
        }
        _logger.Info("sdk_launcher.ready",
            $"初始化等待完成。delay={configuration.SdkInitializationDelay}；trayRequests={trayController?.TrayRequestCount ?? 0}");
        return ownedProcess;
    }

    private async Task StopOwnedSdkLauncherAsync(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                _logger.Info("sdk_launcher.cleanup", $"桥接器启动的 SDK 入口已自行退出。PID={process.Id}；exitCode={process.ExitCode}");
                return;
            }

            _logger.Info("sdk_launcher.cleanup_request", $"准备关闭桥接器启动的 SDK 入口。PID={process.Id}");
            if (process.CloseMainWindow())
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
            }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }
            _logger.Info("sdk_launcher.cleanup",
                $"桥接器启动的 SDK 入口清理完成。PID={process.Id}；exited={process.HasExited}；exitCode={(process.HasExited ? process.ExitCode : "<still-running>")}");
        }
        catch (Exception ex)
        {
            _logger.Error("sdk_launcher.cleanup_failed",
                $"清理桥接器启动的 SDK 入口失败。PID={process.Id}；exception={ex}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private ProcessSnapshot GetGameSnapshot(ResolvedBridgeConfiguration configuration)
    {
        var snapshot = _processes.GetSnapshot(configuration.GameProcessNames, configuration.GameExecutablePaths);
        if (snapshot.IgnoredPaths.Count > 0)
            _logger.Warn("game.same_name_ignored", $"已忽略同名但路径不匹配的进程：{string.Join(';', snapshot.IgnoredPaths)}");
        if (snapshot.UnreadableCount > 0)
            _logger.Warn("game.path_unreadable", $"有 {snapshot.UnreadableCount} 个同名进程无法读取完整路径，未将其视为游戏本体。");
        return snapshot;
    }

    private void LogSnapshot(string eventName, ProcessSnapshot snapshot) =>
        _logger.Info(eventName, $"matchingPids={string.Join(',', snapshot.MatchingProcessIds)}；matchingPaths={string.Join(';', snapshot.MatchingPaths.Values)}");

}

public static class SdkLauncherWindowController
{
    private const uint GetOwner = 4;

    public static int HideMainWindows(IReadOnlyCollection<int> processIds)
    {
        if (processIds.Count == 0) return 0;
        var ids = processIds.ToHashSet();
        var requested = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (ids.Contains(unchecked((int)processId))
                && GetWindow(window, GetOwner) == IntPtr.Zero
                && GetWindowTextLengthW(window) > 0
                && IsWindowVisible(window))
            {
                if (ShowWindowAsync(window, 0)) requested++;
            }
            return true;
        }, IntPtr.Zero);
        return requested;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
}

public sealed class SdkLauncherTrayController : IAsyncDisposable
{
    private readonly IProcessSnapshotProvider _processes;
    private readonly string _sdkLauncherPath;
    private readonly BridgeLogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _ownedProcessIds = new();
    private Task? _worker;
    private int _trayRequestCount;
    public int TrayRequestCount => Volatile.Read(ref _trayRequestCount);

    public SdkLauncherTrayController(
        IProcessSnapshotProvider processes,
        string sdkLauncherPath,
        BridgeLogger logger) =>
        (_processes, _sdkLauncherPath, _logger) = (processes, sdkLauncherPath, logger);

    public void Start()
    {
        if (_worker is not null) return;
        _worker = Task.Run(RunAsync);
        _logger.Info("sdk_launcher.tray_monitor_started",
            $"开始按完整路径监控官方入口窗口并请求关闭到托盘。path={_sdkLauncherPath}");
    }

    public void RegisterOwnedProcessId(int processId)
    {
        _ownedProcessIds.TryAdd(processId, 0);
        _logger.Info("sdk_launcher.tray_pid_registered",
            $"已登记桥接器启动的官方入口 PID，用于托盘控制。PID={processId}");
    }

    private async Task RunAsync()
    {
        var name = Path.GetFileNameWithoutExtension(_sdkLauncherPath);
        var highFrequencyUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var snapshot = _processes.GetSnapshot([name], [_sdkLauncherPath]);
                var candidateIds = snapshot.MatchingProcessIds
                    .Concat(_ownedProcessIds.Keys)
                    .Distinct()
                    .ToArray();
                var requested = SdkLauncherWindowController.HideMainWindows(candidateIds);
                if (requested > 0)
                {
                    Interlocked.Add(ref _trayRequestCount, requested);
                    _logger.Info("sdk_launcher.taskbar_window_hidden",
                        $"已隐藏官方入口主窗口并保留托盘进程。count={requested}；pids={string.Join(',', candidateIds)}");
                }
                var delay = DateTimeOffset.UtcNow < highFrequencyUntil
                    ? TimeSpan.FromMilliseconds(10)
                    : TimeSpan.FromMilliseconds(100);
                await Task.Delay(delay, _cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error("sdk_launcher.tray_monitor_failed", $"官方入口托盘监控失败。exception={ex}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_worker is not null) await _worker;
        _cancellation.Dispose();
        _logger.Info("sdk_launcher.tray_monitor_stopped",
            $"官方入口托盘监控已停止。trayRequests={TrayRequestCount}");
    }
}

public sealed class ProcessExitTracker : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint StillActive = 259;
    private IntPtr _handle;

    private ProcessExitTracker(IntPtr handle) => _handle = handle;

    public static ProcessExitTracker? TryOpen(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
        return handle == IntPtr.Zero ? null : new ProcessExitTracker(handle);
    }

    public string ReadExitCode()
    {
        if (_handle == IntPtr.Zero || !GetExitCodeProcess(_handle, out var exitCode)) return "<unavailable>";
        return exitCode == StillActive ? "<still-running>" : exitCode.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class BridgeLogger
{
    private const string LogDirectoryName = "CalabiyauLaunchBridge-log";
    private const string LogFileName = "CalabiyauLaunchBridge.log";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object _syncRoot = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private DateTimeOffset _nextMaintenanceUtc;
    public string LogPath { get; }
    public string? WriteFailure { get; private set; }
    public static string DefaultLogPath => Path.Combine(AppContext.BaseDirectory, LogDirectoryName, LogFileName);

    public BridgeLogger(string? path = null)
    {
        LogPath = Path.GetFullPath(path ?? DefaultLogPath);
        lock (_syncRoot)
        {
            Prune(DateTimeOffset.Now);
            _nextMaintenanceUtc = DateTimeOffset.UtcNow + MaintenanceInterval;
        }
    }

    public void Info(string eventName, string message) => Write("INFO", eventName, message);
    public void Warn(string eventName, string message) => Write("WARN", eventName, message);
    public void Error(string eventName, string message) => Write("ERROR", eventName, message);
    public void Exception(string eventName, Exception exception, string context) => Write("ERROR", eventName, $"{context} | {exception}");

    public void Maintain()
    {
        lock (_syncRoot)
        {
            if (DateTimeOffset.UtcNow < _nextMaintenanceUtc) return;
            Prune(DateTimeOffset.Now);
            _nextMaintenanceUtc = DateTimeOffset.UtcNow + MaintenanceInterval;
        }
    }

    private void Write(string level, string eventName, string message)
    {
        try
        {
            lock (_syncRoot)
            {
                if (DateTimeOffset.UtcNow >= _nextMaintenanceUtc)
                {
                    Prune(DateTimeOffset.Now);
                    _nextMaintenanceUtc = DateTimeOffset.UtcNow + MaintenanceInterval;
                }
                var directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);
                var line = $"{DateTimeOffset.Now:O} [{level}] [Session={_sessionId}] [PID={Environment.ProcessId}] "
                    + $"[Thread={Environment.CurrentManagedThreadId}] [Event={Sanitize(eventName)}] {Sanitize(message)}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Utf8WithoutBom);
                WriteFailure = null;
            }
        }
        catch (Exception ex) { WriteFailure = $"{ex.GetType().FullName}: {ex.Message}"; }
    }

    private void Prune(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var cutoff = now - Retention;
            var lines = File.ReadAllLines(LogPath, Encoding.UTF8);
            var retained = new List<string>();
            var block = new List<string>();
            DateTimeOffset? blockTimestamp = null;
            void Flush()
            {
                if (block.Count > 0 && (!blockTimestamp.HasValue || blockTimestamp >= cutoff)) retained.AddRange(block);
                block.Clear();
            }
            foreach (var line in lines)
            {
                if (TryReadTimestamp(line, out var timestamp)) { Flush(); blockTimestamp = timestamp; }
                block.Add(line);
            }
            Flush();
            if (retained.Count != lines.Length) File.WriteAllLines(LogPath, retained, Utf8WithoutBom);
        }
        catch (Exception ex) { WriteFailure = $"日志保留清理失败：{ex.GetType().FullName}: {ex.Message}"; }
    }

    private static bool TryReadTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var separator = line.IndexOf(" [", StringComparison.Ordinal);
        return separator > 0 && DateTimeOffset.TryParse(line[..separator], CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out timestamp);
    }

    private static string Sanitize(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}

public static class LogSanitizer
{
    private static readonly string[] SensitiveKeys = ["token", "ticket", "cookie", "password", "passwd", "secret", "session", "key", "login", "launch", "lanuch", "channel", "adspath"];
    public static string FormatArguments(IEnumerable<string> arguments)
    {
        var result = new List<string>();
        var redactNext = false;
        foreach (var argument in arguments)
        {
            if (redactNext) { result.Add("<redacted>"); redactNext = false; continue; }
            var separator = argument.IndexOf('=');
            var key = separator >= 0 ? argument[..separator] : argument;
            var sensitive = SensitiveKeys.Any(item => key.Contains(item, StringComparison.OrdinalIgnoreCase));
            if (sensitive && separator >= 0) result.Add(key + "=<redacted>");
            else
            {
                result.Add(Quote(argument));
                redactNext = sensitive;
            }
        }
        return string.Join(' ', result);
    }
    public static string FormatCommand(string executable, IEnumerable<string> arguments) => $"{Quote(executable)} {FormatArguments(arguments)}".TrimEnd();
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

public sealed class BridgeInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    public bool Acquired { get; }
    private BridgeInstanceLock(Mutex mutex, bool acquired) => (_mutex, Acquired) = (mutex, acquired);
    public static BridgeInstanceLock TryAcquire(string installRoot)
    {
        var normalized = Path.GetFullPath(installRoot).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        var mutex = new Mutex(true, $"Local\\CalabiyauLaunchBridge-{hash}", out var createdNew);
        return new(mutex, createdNew);
    }
    public void Dispose()
    {
        if (Acquired) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
    }
}

public static class FailureNotifier
{
    public static void Show(string message, BridgeLogger logger, bool suppressDialog = false)
    {
        if (suppressDialog) return;
        var text = message + $"\n\n日志：{logger.LogPath}"
            + (logger.WriteFailure is null ? "" : $"\n日志写入失败：{logger.WriteFailure}");
        try { MessageBoxW(IntPtr.Zero, text, "CalabiyauLaunchBridge 启动失败", 0x10); }
        catch { }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
