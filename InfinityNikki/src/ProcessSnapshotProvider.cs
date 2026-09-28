using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace InfinityNikki.LaunchBridge;

public sealed record ProcessSnapshot(
    IReadOnlySet<int> MatchingProcessIds,
    IReadOnlyDictionary<int, string> MatchingPaths,
    IReadOnlyList<string> IgnoredPaths,
    int UnreadableCount);

public interface IProcessSnapshotProvider
{
    ProcessSnapshot GetSnapshot(
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> expectedExecutablePaths);
}

public sealed class ProcessSnapshotProvider : IProcessSnapshotProvider
{
    public ProcessSnapshot GetSnapshot(
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> expectedExecutablePaths)
    {
        var expected = expectedExecutablePaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        var paths = new Dictionary<int, string>();
        var ignoredPaths = new List<string>();
        var unreadableCount = 0;
        foreach (var processName in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        var path = TryGetProcessPath(process.Id);
                        if (path is null)
                        {
                            unreadableCount++;
                            continue;
                        }

                        var fullPath = Path.GetFullPath(path);
                        if (expected.Contains(fullPath))
                        {
                            ids.Add(process.Id);
                            paths[process.Id] = fullPath;
                        }
                        else
                        {
                            ignoredPaths.Add(fullPath);
                        }
                    }
                    catch
                    {
                        unreadableCount++;
                    }
                }
            }
        }

        return new ProcessSnapshot(ids, paths, ignoredPaths, unreadableCount);
    }

    private static string? TryGetProcessPath(int processId)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
