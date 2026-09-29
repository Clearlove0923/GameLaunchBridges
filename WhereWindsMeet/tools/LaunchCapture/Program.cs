using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WhereWindsMeetLaunchCapture;

internal static class Program
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private static readonly string[] ExcludedDirectories =
        ["Patch", "BinPatch", "Backup", "Backups", "Download", "Downloads", "Temp", "Tmp", "Cache", "Caches"];
    private static readonly HashSet<string> ProcessNames =
        new(StringComparer.OrdinalIgnoreCase) { "launcher", "yysls", "wwm" };

    private static string _logPath = string.Empty;
    private static string _session = Guid.NewGuid().ToString("N")[..8];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var options = Options.Parse(args);
            var executableDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var installRoot = Path.GetFullPath(options.InstallRoot ?? executableDirectory);
            var logDirectory = Path.Combine(executableDirectory, "WhereWindsMeetLaunchCapture-log");
            Directory.CreateDirectory(logDirectory);
            _logPath = Path.Combine(logDirectory, "where-winds-meet-launch-capture.log");
            TrimOldEntries(_logPath);

            Write("============================================================");
            Write($"捕获开始：版本={typeof(Program).Assembly.GetName().Version}，安装范围={installRoot}");
            Write($"捕获程序={Environment.ProcessPath}，系统={Environment.OSVersion}，架构={RuntimeInformation.ProcessArchitecture}");
            Write("只读监控已启动。现在请打开官方启动器，选择 DX11 并启动游戏。请勿再次运行捕获程序。");
            Console.WriteLine();
            Console.WriteLine("现在请用官方启动器选择 DX11 并启动游戏……");
            Console.WriteLine($"日志：{_logPath}");
            Console.WriteLine("检测到游戏后将继续采集 45 秒并自动退出；按 Ctrl+C 可提前结束。\n");

            var cancelled = false;
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancelled = true;
            };

            var settingsBefore = CaptureDxSettings(installRoot, "启动前");
            var observed = new Dictionary<int, ProcessObservation>();
            DateTimeOffset? gameDetectedAt = null;
            var timeoutAt = DateTimeOffset.Now.AddMinutes(options.TimeoutMinutes);

            while (!cancelled && DateTimeOffset.Now < timeoutAt)
            {
                var activePids = new HashSet<int>();
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (!ProcessNames.Contains(process.ProcessName))
                            continue;

                        var path = TryGetProcessPath(process);
                        if (!IsRelevant(process.ProcessName, path, installRoot))
                            continue;

                        activePids.Add(process.Id);
                        if (!observed.TryGetValue(process.Id, out var observation))
                        {
                            observation = CaptureProcess(process, path);
                            observed[process.Id] = observation;
                            Write($"发现进程：name={process.ProcessName}.exe, pid={process.Id}, parentPid={observation.ParentPid?.ToString() ?? "<不可读取>"}, path={path ?? "<不可读取>"}");
                            Write($"命令行：pid={process.Id}, commandLine={observation.CommandLine ?? "<不可读取>"}");
                            Console.WriteLine($"已发现 {process.ProcessName}.exe (PID {process.Id})");

                            if (process.ProcessName.Equals("yysls", StringComparison.OrdinalIgnoreCase) ||
                                process.ProcessName.Equals("wwm", StringComparison.OrdinalIgnoreCase))
                            {
                                gameDetectedAt ??= DateTimeOffset.Now;
                                Console.WriteLine("已检测到游戏本体，正在继续识别图形模块……");
                            }
                        }

                        CaptureGraphicsModules(process, observation);
                    }
                }

                foreach (var observation in observed.Values.Where(item => !item.ExitLogged && !activePids.Contains(item.ProcessId)))
                {
                    observation.ExitLogged = true;
                    Write($"进程已退出：name={observation.Name}.exe, pid={observation.ProcessId}");
                }

                if (gameDetectedAt is not null && DateTimeOffset.Now - gameDetectedAt >= TimeSpan.FromSeconds(options.CaptureSeconds))
                    break;

                Thread.Sleep(500);
            }

            var settingsAfter = CaptureDxSettings(installRoot, "启动后");
            LogSettingChanges(settingsBefore, settingsAfter);

            if (gameDetectedAt is null)
            {
                Write(cancelled ? "用户提前结束，未检测到游戏本体。" : "捕获超时，未检测到游戏本体。", "WARN");
                Console.WriteLine("未检测到游戏本体。请确认 EXE 位于安装根目录，或使用 --install-root 指定目录。");
                return 2;
            }

            Write("捕获完成。请将整个日志文件发回分析；程序未修改任何官方文件。");
            Console.WriteLine();
            Console.WriteLine("捕获完成。请把下面这个日志文件发给我：");
            Console.WriteLine(_logPath);
            Console.WriteLine("5 秒后自动关闭……");
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return 0;
        }
        catch (Exception exception)
        {
            var message = $"捕获失败：{exception}";
            Console.Error.WriteLine(message);
            TryWrite(message, "ERROR");
            Console.Error.WriteLine("按任意键退出……");
            Console.ReadKey(intercept: true);
            return 1;
        }
    }

    private static bool IsRelevant(string name, string? path, string installRoot)
    {
        if (name.Equals("yysls", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("wwm", StringComparison.OrdinalIgnoreCase))
            return path is null || IsWithinRoot(path, installRoot);
        return path is not null && IsWithinRoot(path, installRoot);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ProcessObservation CaptureProcess(Process process, string? path) => new()
    {
        ProcessId = process.Id,
        Name = process.ProcessName,
        Path = path,
        ParentPid = TryGetParentPid(process),
        CommandLine = RedactCommandLine(TryGetCommandLine(process))
    };

    private static string? RedactCommandLine(string? commandLine)
    {
        if (commandLine is null)
            return null;

        const string sensitiveName =
            "access[-_]?token|auth[-_]?token|token|password|passwd|pwd|cookie|session(?:id)?|ticket|secret|api[-_]?key|authorization";
        var redacted = Regex.Replace(
            commandLine,
            $"(?<prefix>(?:^|\\s)(?:--?|/)(?:{sensitiveName})(?:\\s*=\\s*|\\s+))(?:\"(?:\\\\.|[^\"])*\"|\\S+)",
            "${prefix}<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
            redacted,
            $"(?<prefix>[?&](?:{sensitiveName})=)[^&\\s\"]+",
            "${prefix}<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? TryGetProcessPath(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var capacity = 32768;
            var builder = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, builder, ref capacity) ? builder.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int? TryGetParentPid(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var size = Marshal.SizeOf<ProcessBasicInfo>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessBasicInformation, buffer, size, out _);
                return status == 0 ? Marshal.PtrToStructure<ProcessBasicInfo>(buffer).InheritedFromUniqueProcessId.ToInt32() : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryGetCommandLine(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            const int bufferSize = 64 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, bufferSize, out _);
                if (status != 0)
                    return null;
                var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                return value.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void CaptureGraphicsModules(Process process, ProcessObservation observation)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName;
                if (!name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (observation.GraphicsModules.Add(name))
                    Write($"图形模块：pid={process.Id}, module={name}, path={module.FileName}");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (!observation.ModuleReadFailureLogged)
            {
                observation.ModuleReadFailureLogged = true;
                Write($"图形模块读取失败：pid={process.Id}, error={exception.GetType().Name}: {exception.Message}", "WARN");
            }
        }
    }

    private static Dictionary<string, SettingSnapshot> CaptureDxSettings(string root, string phase)
    {
        var snapshots = new Dictionary<string, SettingSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in FindSettingFiles(root, maxDepth: 8))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024)
                    continue;
                var lines = File.ReadLines(path)
                    .Where(line => line.Contains("DX11", StringComparison.OrdinalIgnoreCase) ||
                                   line.Contains("DX12", StringComparison.OrdinalIgnoreCase) ||
                                   line.Contains("DirectX", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line.Trim())
                    .ToArray();
                if (lines.Length == 0)
                    continue;
                using var stream = File.OpenRead(path);
                var snapshot = new SettingSnapshot(Convert.ToHexString(SHA256.HashData(stream)), info.LastWriteTimeUtc, lines);
                snapshots[path] = snapshot;
                Write($"DX 配置({phase})：path={path}, sha256={snapshot.Hash}, lastWriteUtc={snapshot.LastWriteUtc:O}, values={string.Join(" | ", lines)}");
            }
            catch (Exception exception)
            {
                Write($"DX 配置读取失败({phase})：path={path}, error={exception.GetType().Name}: {exception.Message}", "WARN");
            }
        }
        if (snapshots.Count == 0)
            Write($"DX 配置({phase})：未在安装范围内发现含 DX11/DX12 的小型 setting.ini。", "WARN");
        return snapshots;
    }

    private static IEnumerable<string> FindSettingFiles(string root, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current.Path, "setting.ini", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { files = []; }
            foreach (var file in files)
                yield return file;
            if (current.Depth >= maxDepth)
                continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current.Path).ToArray(); }
            catch { continue; }
            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                        ExcludedDirectories.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                        continue;
                    queue.Enqueue((directory, current.Depth + 1));
                }
                catch { }
            }
        }
    }

    private static void LogSettingChanges(Dictionary<string, SettingSnapshot> before, Dictionary<string, SettingSnapshot> after)
    {
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(path, out var oldValue);
            after.TryGetValue(path, out var newValue);
            if (oldValue?.Hash == newValue?.Hash)
                Write($"DX 配置变化：path={path}, changed=False");
            else
                Write($"DX 配置变化：path={path}, changed=True, before={oldValue?.Hash ?? "<不存在>"}, after={newValue?.Hash ?? "<不存在>"}");
        }
    }

    private static void TrimOldEntries(string path)
    {
        if (!File.Exists(path))
            return;
        var cutoff = DateTimeOffset.Now.AddDays(-7);
        var kept = File.ReadLines(path).Where(line =>
        {
            if (!line.StartsWith("[", StringComparison.Ordinal))
                return true;
            var closing = line.IndexOf(']');
            return closing <= 1 || !DateTimeOffset.TryParse(line.AsSpan(1, closing - 1), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var timestamp) || timestamp >= cutoff;
        }).ToArray();
        File.WriteAllLines(path, kept, new UTF8Encoding(false));
    }

    private static void Write(string message, string level = "INFO") =>
        File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] [session={_session} pid={Environment.ProcessId}] [{level}] {message}{Environment.NewLine}", new UTF8Encoding(false));

    private static void TryWrite(string message, string level)
    {
        try { if (!string.IsNullOrEmpty(_logPath)) Write(message, level); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInfo
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private sealed class ProcessObservation
    {
        public required int ProcessId { get; init; }
        public required string Name { get; init; }
        public string? Path { get; init; }
        public int? ParentPid { get; init; }
        public string? CommandLine { get; init; }
        public HashSet<string> GraphicsModules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ModuleReadFailureLogged { get; set; }
        public bool ExitLogged { get; set; }
    }

    private sealed record SettingSnapshot(string Hash, DateTime LastWriteUtc, string[] Lines);

    private sealed record Options(string? InstallRoot, int TimeoutMinutes, int CaptureSeconds)
    {
        public static Options Parse(string[] args)
        {
            string? installRoot = null;
            var timeoutMinutes = 10;
            var captureSeconds = 45;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--install-root" when index + 1 < args.Length:
                        installRoot = args[++index];
                        break;
                    case "--timeout-minutes" when index + 1 < args.Length && int.TryParse(args[++index], out var timeout):
                        timeoutMinutes = Math.Clamp(timeout, 1, 60);
                        break;
                    case "--capture-seconds" when index + 1 < args.Length && int.TryParse(args[++index], out var seconds):
                        captureSeconds = Math.Clamp(seconds, 1, 300);
                        break;
                    default:
                        throw new ArgumentException($"未知或不完整参数：{args[index]}");
                }
            }
            return new(installRoot, timeoutMinutes, captureSeconds);
        }
    }
}
