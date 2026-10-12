using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NevernessToEvernessLaunchCapture;

internal static class Program
{
    private const int QueryLimitedInformation = 0x1000;
    private const int VmRead = 0x0010;
    private const int CommandLineInformation = 60;
    private const int BasicInformation = 0;
    private static readonly string[] ProcessNames =
        ["NTELauncher", "NTEGame", "NTEUpdate", "NTEBrowser", "NTEWebBooster", "HTGame"];
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "Backup", "Backups", "Download", "Downloads", "Temp", "Tmp", "Cache", "Caches", "Patch", "BinPatch", "$RECYCLE.BIN" };
    private static readonly string[] LauncherEvents =
        ["onBeginCheckGameResVersion", "_setLocalResChecked", "onGameElementUpdateFinished",
         "GameResUpdaterAgent::SetState", "GameClientAgent::launchGame", "GameLifecycleMgr::startGame",
         "GameLifecycleMgr::createProcess", "GameLifecycleMgr::processGameWnd", "GameActivitySetUp",
         "GameLifecycleMgr::onGameProcessFinished", "NTEGame graceful exit"];
    private static readonly string[] PatcherEvents =
        ["GameResCheckSuccess", "BaseCheckResult", "TagCheckCallback", "pakchunk101",
         "pakchunk102", "pakchunk103", "pakchunk104"];
    private static readonly string SessionId = Guid.NewGuid().ToString("N")[..12];
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static string? _logPath;
    private static DateTimeOffset _lastTrim;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var captureStartedAt = DateTimeOffset.Now;
            var options = CaptureOptions.Parse(args);
            var outputDirectory = Path.Combine(AppContext.BaseDirectory, "NevernessToEvernessLaunchCapture-log");
            Directory.CreateDirectory(outputDirectory);
            _logPath = Path.Combine(outputDirectory, "NevernessToEvernessLaunchCapture.log");
            TrimOldEntries();

            var root = ResolveInstallRoot(options.InstallRoot);
            var launchers = FindFiles(root, "NTEGame.exe", 8).Where(IsNonemptyFile).ToArray();
            var games = FindFiles(root, "HTGame.exe", 12).Where(IsNonemptyFile).ToArray();
            Log($"capture.start version={typeof(Program).Assembly.GetName().Version} os={Environment.OSVersion} arch={RuntimeInformation.ProcessArchitecture} root={root} timeoutMinutes={options.TimeoutMinutes} validate={options.Validate}");
            Log($"discovery.launchers={string.Join(';', launchers)}");
            Log($"discovery.games={string.Join(';', games)}");
            if (launchers.Length == 0 || games.Length == 0)
                throw new FileNotFoundException("安装范围内未同时找到非空的 NTEGame.exe 和 HTGame.exe；请用 --install-root 指定异环官方安装根目录。");

            foreach (var pak in FindFiles(root, "pakchunk*.pak", 12).Where(path => Path.GetFileName(path).StartsWith("pakchunk10", StringComparison.OrdinalIgnoreCase)))
            {
                var file = new FileInfo(pak);
                Log($"resource.pak path={pak} bytes={file.Length} modifiedUtc={file.LastWriteTimeUtc:O}");
            }

            if (options.Validate)
            {
                Console.WriteLine($"路径验证成功。安装根目录：{root}");
                Console.WriteLine($"启动器：{string.Join("；", launchers)}");
                Console.WriteLine($"游戏本体：{string.Join("；", games)}");
                Console.WriteLine($"日志：{_logPath}");
                Log("capture.validate_success");
                return 0;
            }

            Console.WriteLine("异环官方启动链采集已开始。现在请手动打开官方启动器，等待检查完成，再点击“开始游戏”。");
            Console.WriteLine("进入有台词的场景后，确认角色语音，再正常退出游戏。采集器会在游戏退出后自动结束。");
            Console.WriteLine($"日志：{_logPath}");
            Console.WriteLine("按 Ctrl+C 可提前结束。\n");
            Log("capture.waiting_for_manual_launch; no process will be started by capture");

            var cancelled = false;
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancelled = true; };
            var observed = new Dictionary<int, ObservedProcess>();
            var launcherWatcher = new TextLogWatcher(FindFiles(root, "NTEGame.log", 9).FirstOrDefault(), "launcher", LauncherEvents);
            var patcherWatcher = new TextLogWatcher(FindFiles(root, "PC_140.log", 14)
                .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Game{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)),
                "game-patcher", PatcherEvents);
            launcherWatcher.Prime();
            patcherWatcher.Prime();
            DateTimeOffset? gameSeenAt = null;
            DateTimeOffset? gameGoneAt = null;
            var deadline = DateTimeOffset.Now.AddMinutes(options.TimeoutMinutes);

            while (!cancelled && DateTimeOffset.Now < deadline)
            {
                var active = new HashSet<int>();
                foreach (var name in ProcessNames)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        var retained = false;
                        try
                        {
                            var path = TryGetProcessPath(process.Id);
                            if (path is null || !IsWithinRoot(path, root)) continue;
                            active.Add(process.Id);
                            if (observed.ContainsKey(process.Id)) continue;
                            var item = CaptureProcess(process, path);
                            observed.Add(process.Id, item);
                            retained = true;
                            Log($"process.appeared name={item.Name} pid={item.Id} parentPid={item.ParentPid?.ToString() ?? "unavailable"} path={item.Path} cwd={item.WorkingDirectory ?? "unavailable"} command={item.CommandLine ?? "unavailable"}");
                            Console.WriteLine($"发现 {item.Name} (PID {item.Id})");
                            if (name.Equals("HTGame", StringComparison.OrdinalIgnoreCase))
                            {
                                gameSeenAt ??= DateTimeOffset.Now;
                                gameGoneAt = null;
                            }
                        }
                        finally { if (!retained) process.Dispose(); }
                    }
                }

                foreach (var item in observed.Values.Where(item => !active.Contains(item.Id)).ToArray())
                {
                    var exitCode = TryGetExitCode(item.Process);
                    Log($"process.disappeared name={item.Name} pid={item.Id} observedFor={(DateTimeOffset.Now - item.FirstSeen):c} exitCode={exitCode?.ToString() ?? "unavailable"}");
                    item.Process.Dispose();
                    observed.Remove(item.Id);
                }
                if (gameSeenAt is not null && !active.Any(id => observed[id].Name.Equals("HTGame", StringComparison.OrdinalIgnoreCase)))
                    gameGoneAt ??= DateTimeOffset.Now;

                launcherWatcher.Poll();
                patcherWatcher.Poll();
                if (DateTimeOffset.Now - _lastTrim > TimeSpan.FromHours(1)) TrimOldEntries();
                if (gameGoneAt is not null && DateTimeOffset.Now - gameGoneAt > TimeSpan.FromSeconds(10)) break;
                Thread.Sleep(250);
            }

            launcherWatcher.Poll();
            patcherWatcher.Poll();
            foreach (var item in observed.Values) item.Process.Dispose();
            Log($"capture.end reason={(cancelled ? "cancelled" : gameGoneAt is not null ? "game_exited" : "timeout")} gameSeen={gameSeenAt is not null} duration={(DateTimeOffset.Now - captureStartedAt):c}");
            Console.WriteLine($"采集结束。日志：{_logPath}");
            return gameSeenAt is null ? 2 : 0;
        }
        catch (Exception ex)
        {
            var logFailure = TryLogFailure(ex);
            var message = $"异环采集失败：{ex.GetType().Name}: {ex.Message}";
            if (logFailure is not null) message += $"\n日志写入失败：{logFailure.GetType().Name}: {logFailure.Message}";
            Console.Error.WriteLine(message);
            if (!CaptureOptions.HasNoDialog(args)) MessageBox(IntPtr.Zero, message, "NevernessToEvernessLaunchCapture", 0x10);
            return 1;
        }
    }

    internal static string ResolveInstallRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
        {
            var root = Path.GetFullPath(explicitRoot);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"安装根目录不存在：{root}");
            return root;
        }
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; candidate is not null && i < 4; i++, candidate = candidate.Parent)
            if (FindFiles(candidate.FullName, "NTEGame.exe", 3).Any(IsNonemptyFile)) return candidate.FullName;
        throw new DirectoryNotFoundException("未从采集 EXE 所在目录找到异环安装位置；请使用 --install-root 指定官方安装根目录。");
    }

    internal static IEnumerable<string> FindFiles(string root, string pattern, int maxDepth)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            string[] files;
            try { files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly); }
            catch { continue; }
            foreach (var file in files) yield return file;
            if (depth >= maxDepth) continue;
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch { continue; }
            foreach (var child in children)
            {
                try
                {
                    if (ExcludedDirectories.Contains(Path.GetFileName(child)) ||
                        (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    queue.Enqueue((child, depth + 1));
                }
                catch { /* A removed or protected directory is not a candidate. */ }
            }
        }
    }

    private static bool IsNonemptyFile(string path) { try { return new FileInfo(path).Length > 0; } catch { return false; } }
    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ObservedProcess CaptureProcess(Process process, string path)
    {
        var queryHandle = OpenProcess(QueryLimitedInformation, false, process.Id);
        var readHandle = OpenProcess(QueryLimitedInformation | VmRead, false, process.Id);
        try
        {
            return new ObservedProcess(process.Id, process.ProcessName, path, DateTimeOffset.Now,
                queryHandle == IntPtr.Zero ? null : TryGetParentPid(queryHandle),
                Redact(queryHandle == IntPtr.Zero ? null : TryGetCommandLine(queryHandle)),
                readHandle == IntPtr.Zero ? null : TryGetWorkingDirectory(readHandle), process);
        }
        finally
        {
            if (queryHandle != IntPtr.Zero) CloseHandle(queryHandle);
            if (readHandle != IntPtr.Zero) CloseHandle(readHandle);
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    internal static string? Redact(string? command)
    {
        if (command is null) return null;
        const string names = "access[-_]?token|auth[-_]?token|token|password|passwd|pwd|cookie|session(?:id)?|ticket|secret|api[-_]?key|authorization|sz_pers_id|sz_token|user[-_]?id|pers[-_]?id|uid";
        var result = Regex.Replace(command,
            $"(?<prefix>(?:^|\\s)(?:--?|/)?(?:{names})(?:\\s*[:=]\\s*|\\s+))(?:\"[^\"]*\"|\\S+)",
            "${prefix}<redacted>", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, $"(?<prefix>[?&](?:{names})=)[^&\\s\"]+", "${prefix}<redacted>", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, "https?://[^\\s\"]+", "<url-redacted>", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "<email-redacted>", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\b(?:[A-Fa-f0-9]{32,}|[A-Za-z0-9_-]{64,})\b", "<opaque-redacted>");
        return result;
    }

    private static string? TryGetProcessPath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var length = 32768;
            var buffer = new StringBuilder(length);
            return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static int? TryGetParentPid(IntPtr handle)
    {
        var size = Marshal.SizeOf<ProcessBasicInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try { return NtQueryInformationProcess(handle, BasicInformation, buffer, size, out _) == 0
            ? (int)Marshal.PtrToStructure<ProcessBasicInfo>(buffer).InheritedFromUniqueProcessId : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? TryGetCommandLine(IntPtr handle)
    {
        const int capacity = 65536;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            if (NtQueryInformationProcess(handle, CommandLineInformation, buffer, capacity, out _) != 0) return null;
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == IntPtr.Zero || value.Length % 2 != 0 ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? TryGetWorkingDirectory(IntPtr handle)
    {
        if (IntPtr.Size != 8) return null;
        var size = Marshal.SizeOf<ProcessBasicInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, BasicInformation, buffer, size, out _) != 0) return null;
            var peb = Marshal.PtrToStructure<ProcessBasicInfo>(buffer).PebBaseAddress;
            if (!ReadProcessMemory(handle, peb + 0x20, buffer, 8, out var read) || read != 8) return null;
            var parameters = Marshal.ReadIntPtr(buffer);
            if (parameters == IntPtr.Zero || !ReadProcessMemory(handle, parameters + 0x38, buffer, 16, out read) || read != 16) return null;
            var length = Marshal.ReadInt16(buffer);
            var stringAddress = Marshal.ReadIntPtr(buffer, 8);
            if (length <= 0 || length > 32766 || stringAddress == IntPtr.Zero) return null;
            var chars = Marshal.AllocHGlobal(length);
            try { return ReadProcessMemory(handle, stringAddress, chars, length, out read) && read == (nuint)length
                ? Marshal.PtrToStringUni(chars, length / 2) : null; }
            finally { Marshal.FreeHGlobal(chars); }
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void TrimOldEntries()
    {
        if (_logPath is null) return;
        if (File.Exists(_logPath))
        {
            var cutoff = DateTimeOffset.Now.AddDays(-7);
            var kept = File.ReadLines(_logPath).Where(line =>
            {
                var end = line.IndexOf(']');
                return end < 2 || !DateTimeOffset.TryParse(line.AsSpan(1, end - 1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp) || timestamp >= cutoff;
            }).ToArray();
            File.WriteAllLines(_logPath, kept, Utf8);
        }
        _lastTrim = DateTimeOffset.Now;
    }

    private static void Log(string message, string level = "INFO") =>
        File.AppendAllText(_logPath ?? throw new InvalidOperationException("日志路径未初始化"),
            $"[{DateTimeOffset.Now:O}] [run={SessionId} pid={Environment.ProcessId}] [{level}] {message}{Environment.NewLine}", Utf8);

    private static Exception? TryLogFailure(Exception ex)
    {
        try { Log($"capture.failure {ex}", "ERROR"); return null; }
        catch (Exception logEx) { return logEx; }
    }

    private sealed class TextLogWatcher(string? path, string source, string[] markers)
    {
        private int _lineCount;
        private string? _lastLine;
        public void Prime()
        {
            if (path is null || !File.Exists(path)) return;
            try { var lines = File.ReadAllLines(path); _lineCount = lines.Length; _lastLine = lines.LastOrDefault(); }
            catch (IOException) { }
        }
        public void Poll()
        {
            if (path is null || !File.Exists(path)) return;
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException) { return; }
            if (lines.Length < _lineCount ||
                (_lineCount > 0 && lines.Length >= _lineCount && lines[_lineCount - 1] != _lastLine))
                _lineCount = 0;
            foreach (var line in lines.Skip(_lineCount))
            {
                var marker = markers.FirstOrDefault(value => line.Contains(value, StringComparison.OrdinalIgnoreCase));
                if (marker is null) continue;
                var timestamp = Regex.Match(line, @"^(?:\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2}(?:\.\d+)?");
                Log($"official.event source={source} event={marker} sourceTime={(timestamp.Success ? timestamp.Value : "unavailable")}");
            }
            _lineCount = lines.Length;
            _lastLine = lines.LastOrDefault();
        }
    }

    private sealed record CaptureOptions(string? InstallRoot, int TimeoutMinutes, bool Validate, bool NoDialog)
    {
        public static bool HasNoDialog(string[] args) => args.Contains("--no-dialog", StringComparer.Ordinal);
        public static CaptureOptions Parse(string[] args)
        {
            string? root = null;
            var timeout = 20;
            var validate = false;
            var noDialog = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--install-root" when i + 1 < args.Length: root = args[++i]; break;
                    case "--timeout-minutes" when i + 1 < args.Length && int.TryParse(args[i + 1], out var minutes):
                        timeout = Math.Clamp(minutes, 1, 120); i++; break;
                    case "--validate": validate = true; break;
                    case "--no-dialog": noDialog = true; break;
                    default: throw new ArgumentException($"未知或不完整参数：{args[i]}");
                }
            }
            return new CaptureOptions(root, timeout, validate, noDialog);
        }
    }

    private sealed class ObservedProcess(int id, string name, string path, DateTimeOffset firstSeen,
        int? parentPid, string? commandLine, string? workingDirectory, Process process)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string Path { get; } = path;
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public int? ParentPid { get; } = parentPid;
        public string? CommandLine { get; } = commandLine;
        public string? WorkingDirectory { get; } = workingDirectory;
        public Process Process { get; } = process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInfo
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder buffer, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, IntPtr buffer, int length, out nuint read);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int informationClass, IntPtr buffer, int length, out int returned);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
