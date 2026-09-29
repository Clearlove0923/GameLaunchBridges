using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WhereWindsMeetLaunchBridge;

internal static class Program
{
    private const string Title = "燕云十六声直启实验";
    private const string ApplicationName = "WhereWindsMeetLaunchBridge";
    private static readonly string[] DefaultExecutableNames = ["yysls.exe"];
    private static readonly string[] DefaultExcludedDirectoryNames =
        ["Patch", "BinPatch", "Backup", "Backups", "Download", "Downloads", "Temp", "Tmp", "Cache", "Caches"];
    private static readonly string[] DefaultPreferredPathKeywords = ["Win64r", "Win64rh"];
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly object LogSync = new();
    private static readonly string SessionId = Guid.NewGuid().ToString("N")[..8];
    private static bool _noDialog;
    private static string? _logWriteFailure;

    private static int Main(string[] args)
    {
        try
        {
            PruneOldLogEntries();
            using var logMaintenance = new Timer(_ => PruneOldLogEntries(), null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            Log($"Bridge started: version={typeof(Program).Assembly.GetName().Version}, OS={RuntimeInformation.OSDescription}, architecture={RuntimeInformation.ProcessArchitecture}, executable={Environment.ProcessPath}");

            var options = Options.Parse(args);
            _noDialog = options.NoDialog;
            Log($"Options: installRoot={options.InstallRoot ?? "<exe directory>"}, dryRun={options.DryRun}, attachOnly={options.AttachOnly}, exitDelay={options.ExitDelaySeconds}s, startupTimeout={options.StartupTimeoutSeconds}s, variant={options.Variant ?? "auto"}");
            var configuration = BridgeConfiguration.Load(ConfigPath);
            Log($"Configuration: path={ConfigPath}, exists={File.Exists(ConfigPath)}, executableNames={string.Join(',', configuration.ExecutableNames)}, launchArguments={configuration.LaunchArguments}, maxSearchDepth={configuration.MaxSearchDepth}, excludedDirectories={string.Join(',', configuration.ExcludedDirectoryNames)}, preferredPathKeywords={string.Join(',', configuration.PreferredPathKeywords)}");
            var installRoot = ResolveInstallRoot(options.InstallRoot);
            Log($"Install root: {installRoot}");
            var gamePaths = FindGameExecutables(installRoot, configuration, requestedVariant: null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Log($"Tracked game paths: {string.Join("; ", gamePaths)}");
            foreach (var gamePath in gamePaths)
                LogExecutableDetails("Candidate", gamePath);
            if (gamePaths.Count == 0)
                throw CreateGameNotFoundException(configuration);

            if (options.DryRun)
            {
                Log("Dry run completed without starting the game.");
                Console.WriteLine($"安装目录: {installRoot}");
                Console.WriteLine($"退出延时: {options.ExitDelaySeconds} 秒");
                Console.WriteLine($"仅跟踪已启动游戏: {options.AttachOnly}");
                if (!options.AttachOnly)
                {
                    var startInfo = CreateStartInfo(ResolveExecutable(installRoot, configuration, options.Variant), configuration.LaunchArguments);
                    Log($"Dry run command: {FormatCommand(startInfo)}; workingDirectory={startInfo.WorkingDirectory}");
                    Console.WriteLine($"工作目录: {startInfo.WorkingDirectory}");
                    Console.WriteLine($"启动命令: {FormatCommand(startInfo)}");
                }
                return 0;
            }

            var observed = FindGameProcesses(gamePaths);
            Log($"Initial game scan: matchingPids={string.Join(',', observed.MatchingIds)}, unreadableProcesses={observed.UnreadableCount}");
            if (observed.MatchingIds.Count == 0 && observed.UnreadableCount > 0)
                throw new InvalidOperationException("检测到 yysls.exe，但无法核实其安装路径；请以管理员身份运行桥接器。");

            using var launched = observed.MatchingIds.Count > 0 || options.AttachOnly
                ? null
                : StartGame(CreateStartInfo(ResolveExecutable(installRoot, configuration, options.Variant), configuration.LaunchArguments));
            if (observed.MatchingIds.Count > 0)
                Log($"Attached to running game: pids={string.Join(',', observed.MatchingIds)}");
            else if (options.AttachOnly)
                Log($"Waiting for official game start: timeout={options.StartupTimeoutSeconds}s");

            var stopwatch = Stopwatch.StartNew();
            var gameSeen = observed.MatchingIds.Count > 0 || launched is not null;
            var previousMatchingPids = string.Join(',', observed.MatchingIds);
            var startDeadline = TimeSpan.FromSeconds(options.StartupTimeoutSeconds);
            TimeSpan? emptySince = null;
            while (true)
            {
                observed = FindGameProcesses(gamePaths);
                var currentMatchingPids = string.Join(',', observed.MatchingIds);
                if (currentMatchingPids != previousMatchingPids)
                {
                    Log($"Game process change: previousPids={previousMatchingPids}, currentPids={currentMatchingPids}, unreadableProcesses={observed.UnreadableCount}");
                    previousMatchingPids = currentMatchingPids;
                }
                var launchedAlive = launched is not null && !launched.HasExited;
                var running = observed.MatchingIds.Count > 0 || launchedAlive;
                if (running)
                {
                    if (!gameSeen)
                        Log($"Game appeared: pids={string.Join(',', observed.MatchingIds)}");
                    gameSeen = true;
                    emptySince = null;
                }
                else if (!gameSeen)
                {
                    if (stopwatch.Elapsed >= startDeadline)
                        throw new TimeoutException($"等待官方游戏进程超过 {options.StartupTimeoutSeconds} 秒。");
                }
                else
                {
                    if (emptySince is null)
                        Log("Game process no longer detected; waiting for exit delay.");
                    emptySince ??= stopwatch.Elapsed;
                    if (stopwatch.Elapsed - emptySince >= TimeSpan.FromSeconds(options.ExitDelaySeconds))
                        break;
                }

                Thread.Sleep(PollInterval);
            }

            var exitCode = launched is null ? 0 : TryGetExitCode(launched);
            if (exitCode != 0)
                LogError($"Game exited abnormally: code={exitCode}, bridgeElapsed={stopwatch.Elapsed}, delay={options.ExitDelaySeconds}s");
            else
                Log($"Bridge completed: gameExitCode={exitCode}, bridgeElapsed={stopwatch.Elapsed}, delay={options.ExitDelaySeconds}s");
            if (launched is not null && stopwatch.Elapsed < TimeSpan.FromSeconds(20) && exitCode != 0)
            {
                ShowError($"游戏本体很快退出（代码 {exitCode}）。\n\n" +
                    "已使用官方捕获的本体路径与命令行；如果仍无法进入游戏，可能还需要启动器提供的工作目录、环境或登录上下文。\n\n" +
                    $"日志：{LogPath}");
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            LogError($"Error: {ex}");
            ShowError($"无法启动游戏：{ex.Message}\n\n日志：{LogPath}");
            return 1;
        }
    }

    private static Process StartGame(ProcessStartInfo startInfo)
    {
        Log($"Starting game: {FormatCommand(startInfo)}; workingDirectory={startInfo.WorkingDirectory}");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows 未返回游戏进程。");
        Log($"Game start returned: pid={process.Id}");
        return process;
    }

    private static int TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return 0; }
        catch (System.ComponentModel.Win32Exception) { return 0; }
    }

    private static GameProcesses FindGameProcesses(HashSet<string> expectedPaths)
    {
        var matchingIds = new List<int>();
        var unreadableCount = 0;
        var processNames = expectedPaths.Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var processName in processNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.HasExited)
                        continue;

                    var path = TryGetProcessPath(process.Id);
                    if (path is null)
                    {
                        unreadableCount++;
                        continue;
                    }

                    if (expectedPaths.Contains(Path.GetFullPath(path)))
                        matchingIds.Add(process.Id);
                }
            }
        }

        return new GameProcesses(matchingIds, unreadableCount);
    }

    private static string? TryGetProcessPath(int processId)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var path = new StringBuilder(32768);
            var capacity = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref capacity) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string ResolveInstallRoot(string? explicitRoot)
    {
        var root = Path.GetFullPath(explicitRoot ?? AppContext.BaseDirectory);
        if (Directory.Exists(root))
            return root;

        throw new DirectoryNotFoundException(
            $"游戏安装目录不存在：{root}");
    }

    private static IReadOnlyList<string> FindGameExecutables(
        string installRoot,
        BridgeConfiguration configuration,
        string? requestedVariant)
    {
        var executables = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((installRoot, 0));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var executableName in configuration.ExecutableNames)
            {
                var candidate = Path.Combine(current.Path, executableName);
                if (File.Exists(candidate) &&
                    (requestedVariant is null || HasPathSegment(candidate, requestedVariant)))
                    executables.Add(Path.GetFullPath(candidate));
            }

            if (current.Depth >= configuration.MaxSearchDepth)
                continue;

            string[] childDirectories;
            try { childDirectories = Directory.GetDirectories(current.Path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var childDirectory in childDirectories)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(childDirectory); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var directoryName = Path.GetFileName(childDirectory);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    configuration.ExcludedDirectoryNames.Contains(directoryName, StringComparer.OrdinalIgnoreCase) ||
                    directoryName.Equals($"{ApplicationName}-log", StringComparison.OrdinalIgnoreCase))
                    continue;

                pending.Push((childDirectory, current.Depth + 1));
            }
        }

        return executables.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetCandidateScore(path, installRoot, configuration))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasPathSegment(string path, string expectedSegment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(expectedSegment, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetCandidateScore(
        string path,
        string installRoot,
        BridgeConfiguration configuration)
    {
        var relativePath = Path.GetRelativePath(installRoot, path);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var score = segments.Length * 10;
        var executableNameIndex = Array.FindIndex(configuration.ExecutableNames,
            name => name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        if (executableNameIndex >= 0)
            score += executableNameIndex * 100;

        for (var i = 0; i < configuration.PreferredPathKeywords.Length; i++)
        {
            if (segments.Any(segment => segment.Equals(
                    configuration.PreferredPathKeywords[i], StringComparison.OrdinalIgnoreCase)))
            {
                score -= 1000 - i * 10;
                break;
            }
        }

        return score;
    }

    private static string ResolveExecutable(
        string installRoot,
        BridgeConfiguration configuration,
        string? requestedVariant)
    {
        var executable = FindGameExecutables(installRoot, configuration, requestedVariant).FirstOrDefault();
        if (executable is not null)
        {
            LogExecutableDetails("Selected", executable);
            return executable;
        }

        throw new FileNotFoundException(
            CreateGameNotFoundException(configuration).Message);
    }

    private static FileNotFoundException CreateGameNotFoundException(BridgeConfiguration configuration)
    {
        return new FileNotFoundException(
            $"在安装范围内找不到游戏本体。已搜索名称：{string.Join(", ", configuration.ExecutableNames)}。" +
            $"如果游戏更新后修改了本体名称，请编辑 {ConfigPath}。");
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string launchArguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = true,
            Verb = "runas",
        };

        startInfo.Arguments = launchArguments;
        return startInfo;
    }

    private static void LogExecutableDetails(string role, string path)
    {
        try
        {
            var file = new FileInfo(path);
            Log($"{role} executable: path={file.FullName}, size={file.Length}, lastWriteUtc={file.LastWriteTimeUtc:O}");
        }
        catch (IOException ex) { LogError($"Could not inspect executable {path}: {ex}"); }
        catch (UnauthorizedAccessException ex) { LogError($"Could not inspect executable {path}: {ex}"); }
    }

    private static string FormatCommand(ProcessStartInfo info)
    {
        return $"{QuoteArgument(info.FileName)} {info.Arguments}";
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string LogDirectoryPath => Path.Combine(AppContext.BaseDirectory, $"{ApplicationName}-log");

    private static string LogPath => Path.Combine(LogDirectoryPath, "where-winds-meet-launch-bridge.log");

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, $"{ApplicationName}.json");

    private static void PruneOldLogEntries()
    {
        lock (LogSync)
        {
            if (!File.Exists(LogPath))
                return;

            var temporaryPath = LogPath + ".prune-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
                var retained = new List<string>();
                var keepEntry = true;
                var removedEntries = 0;
                foreach (var line in File.ReadLines(LogPath, Encoding.UTF8))
                {
                    if (line.StartsWith('['))
                    {
                        var closingBracket = line.IndexOf(']');
                        if (closingBracket > 1 && DateTimeOffset.TryParseExact(
                            line.AsSpan(1, closingBracket - 1), "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var timestamp))
                        {
                            keepEntry = timestamp >= cutoff;
                            if (!keepEntry)
                                removedEntries++;
                        }
                    }

                    if (keepEntry)
                        retained.Add(line);
                }

                if (removedEntries == 0)
                    return;

                File.WriteAllLines(temporaryPath, retained, Encoding.UTF8);
                File.Move(temporaryPath, LogPath, overwrite: true);
                Log($"Weekly retention: removed {removedEntries} entries older than {cutoff:O}; retained {retained.Count} lines.");
            }
            catch (Exception ex)
            {
                LogError($"Log cleanup failed: {ex}");
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void Log(string message)
    {
        lock (LogSync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectoryPath);
                var oneLineMessage = message.Replace("\r", "\\r").Replace("\n", "\\n");
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:O}] [session={SessionId} pid={Environment.ProcessId}] {oneLineMessage}{Environment.NewLine}",
                    Encoding.UTF8);
                _logWriteFailure = null;
            }
            catch (IOException ex) { _logWriteFailure = ex.Message; }
            catch (UnauthorizedAccessException ex) { _logWriteFailure = ex.Message; }
        }
    }

    private static void LogError(string message)
    {
        Log($"ERROR: {message}");
    }

    private static void ShowError(string message)
    {
        if (_logWriteFailure is not null)
            message += $"\n\n日志写入失败：{_logWriteFailure}";
        if (!_noDialog && Environment.UserInteractive)
            MessageBoxW(IntPtr.Zero, message, Title, 0x00000010);
        else
            Console.Error.WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed record GameProcesses(List<int> MatchingIds, int UnreadableCount);

    private sealed record BridgeConfiguration(
        string[] ExecutableNames,
        string LaunchArguments,
        string[] ExcludedDirectoryNames,
        string[] PreferredPathKeywords,
        int MaxSearchDepth)
    {
        public static BridgeConfiguration Load(string path)
        {
            if (!File.Exists(path))
                return CreateDefault();

            var json = File.ReadAllText(path, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<BridgeConfigurationDocument>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"配置文件为空：{path}");
            var executableNames = NormalizeExecutableNames(document.ExecutableNames);
            var excludedDirectories = NormalizeValues(
                document.ExcludedDirectoryNames, DefaultExcludedDirectoryNames, allowEmpty: true);
            var preferredKeywords = NormalizeValues(
                document.PreferredPathKeywords, DefaultPreferredPathKeywords, allowEmpty: true);
            var maxSearchDepth = document.MaxSearchDepth ?? 12;
            if (maxSearchDepth is < 1 or > 32)
                throw new InvalidDataException("maxSearchDepth 必须为 1 到 32 之间的整数。");

            return new BridgeConfiguration(
                executableNames,
                document.LaunchArguments ?? "--launch-type=launcher",
                excludedDirectories,
                preferredKeywords,
                maxSearchDepth);
        }

        private static BridgeConfiguration CreateDefault()
        {
            return new BridgeConfiguration(
                DefaultExecutableNames,
                "--launch-type=launcher",
                DefaultExcludedDirectoryNames,
                DefaultPreferredPathKeywords,
                12);
        }

        private static string[] NormalizeExecutableNames(string[]? values)
        {
            var result = NormalizeValues(values, DefaultExecutableNames, allowEmpty: false);
            foreach (var value in result)
            {
                if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                    throw new InvalidDataException($"executableNames 只能包含不带路径的 EXE 文件名：{value}");
            }

            return result;
        }

        private static string[] NormalizeValues(string[]? values, string[] defaults, bool allowEmpty)
        {
            var result = (values is null ? defaults : values)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return result.Length == 0 && !allowEmpty ? defaults : result;
        }
    }

    private sealed record BridgeConfigurationDocument(
        string[]? ExecutableNames,
        string? LaunchArguments,
        string[]? ExcludedDirectoryNames,
        string[]? PreferredPathKeywords,
        int? MaxSearchDepth);

    private sealed record Options(string? InstallRoot, string? Variant, bool DryRun, bool NoDialog,
        bool AttachOnly, int ExitDelaySeconds, int StartupTimeoutSeconds)
    {
        public static Options Parse(string[] args)
        {
            string? installRoot = null;
            string? variant = null;
            var dryRun = false;
            var noDialog = false;
            var attachOnly = false;
            var exitDelaySeconds = 10;
            var startupTimeoutSeconds = 180;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--install-root" when i + 1 < args.Length:
                        installRoot = args[++i];
                        break;
                    case "--variant" when i + 1 < args.Length:
                        variant = args[++i];
                        if (string.IsNullOrWhiteSpace(variant) || variant.Length > 64 ||
                            variant.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                            throw new ArgumentException("--variant 必须是不带路径分隔符的目录关键字，长度不超过 64 个字符。");
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--no-dialog":
                        noDialog = true;
                        break;
                    case "--attach-only":
                        attachOnly = true;
                        break;
                    case "--exit-delay-seconds" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], out exitDelaySeconds) || exitDelaySeconds is < 0 or > 300)
                            throw new ArgumentException("--exit-delay-seconds 必须为 0 到 300 之间的整数。");
                        break;
                    case "--startup-timeout-seconds" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], out startupTimeoutSeconds) || startupTimeoutSeconds is < 1 or > 1800)
                            throw new ArgumentException("--startup-timeout-seconds 必须为 1 到 1800 之间的整数。");
                        break;
                    default:
                        throw new ArgumentException($"未知参数：{args[i]}");
                }
            }

            return new(installRoot, variant, dryRun, noDialog, attachOnly,
                exitDelaySeconds, startupTimeoutSeconds);
        }
    }
}
