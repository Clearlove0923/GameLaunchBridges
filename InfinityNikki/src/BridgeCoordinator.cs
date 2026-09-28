using System.ComponentModel;
using System.Diagnostics;

namespace InfinityNikki.LaunchBridge;

public sealed class BridgeCoordinator
{
    private readonly IProcessSnapshotProvider _processes;
    private readonly BridgeLogger _logger;

    public BridgeCoordinator(IProcessSnapshotProvider processes, BridgeLogger logger)
    {
        _processes = processes;
        _logger = logger;
    }

    public async Task<int> RunAsync(ResolvedBridgeConfiguration configuration)
    {
        var lifecycle = Stopwatch.StartNew();
        var baselineSnapshot = _processes.GetSnapshot(
            configuration.WatchProcessNames,
            configuration.WatchExecutablePaths);
        var baseline = baselineSnapshot.MatchingProcessIds;
        _logger.Info(
            "process.baseline",
            baseline.Count == 0
                ? $"启动前未发现目标进程。names={string.Join(',', configuration.WatchProcessNames)}；unreadable={baselineSnapshot.UnreadableCount}；ignoredPaths={string.Join(';', baselineSnapshot.IgnoredPaths)}"
                : $"启动前已存在目标进程。names={string.Join(',', configuration.WatchProcessNames)}；pids={string.Join(',', baseline.Order())}；paths={string.Join(';', baselineSnapshot.MatchingPaths.Values)}");
        if (baselineSnapshot.UnreadableCount > 0)
        {
            _logger.Error("process.path_unreadable", $"检测到 {baselineSnapshot.UnreadableCount} 个同名进程，但无法读取完整路径；为避免附着到错误进程，本次拒绝启动。");
            return BridgeExitCode.GameAlreadyRunning;
        }
        if (configuration.RefuseIfAlreadyRunning && baseline.Count > 0)
        {
            _logger.Error("process.already_running", $"拒绝启动：目标游戏进程已经存在，PID={string.Join(',', baseline.Order())}。");
            return BridgeExitCode.GameAlreadyRunning;
        }

        _logger.Info(
            "launcher.start_request",
            $"启动后端。executable={configuration.ExecutablePath}；workingDirectory={configuration.WorkingDirectory}；"
            + $"arguments={string.Join(' ', configuration.Arguments)}；elevated={configuration.RunAsAdministrator}");
        try
        {
            using var starter = Process.Start(CreateStartInfo(configuration));
            if (starter is null)
            {
                _logger.Error("launcher.no_process", "Process.Start 未返回进程对象。");
                return BridgeExitCode.LaunchFailed;
            }
            _logger.Info("launcher.started", $"后端启动请求成功，PID={starter.Id}。桥接器不会以该进程退出作为游戏结束信号。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.Exception("launcher.elevation_cancelled", ex, "用户取消了 UAC 提权请求");
            return BridgeExitCode.ElevationCancelled;
        }
        catch (Exception ex)
        {
            _logger.Exception("launcher.start_failed", ex, "启动后端失败");
            return BridgeExitCode.LaunchFailed;
        }

        var sessionIds = await WaitForGameStartAsync(configuration, baseline);
        if (sessionIds.Count == 0)
        {
            _logger.Error("game.start_timeout", $"等待 {configuration.StartupTimeout.TotalSeconds:0} 秒后仍未发现新的目标游戏进程。");
            return BridgeExitCode.GameStartTimedOut;
        }

        _logger.Info("game.detected", $"检测到游戏进程，PID={string.Join(',', sessionIds.Order())}。开始为 Steam 保活。");
        var exitCodes = await WaitForGameExitAsync(configuration, baseline, sessionIds);
        lifecycle.Stop();
        _logger.Info(
            "game.session_complete",
            $"目标游戏进程已退出，桥接器正常结束。gameExitCodes={FormatExitCodes(exitCodes)}；"
            + $"bridgeElapsed={lifecycle.Elapsed}；exitGrace={configuration.ExitGrace}");
        return BridgeExitCode.Success;
    }

    private async Task<IReadOnlySet<int>> WaitForGameStartAsync(
        ResolvedBridgeConfiguration configuration,
        IReadOnlySet<int> baseline)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt + configuration.StartupTimeout;
        var nextProgress = startedAt;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = GetSessionSnapshot(configuration, baseline);
            var sessionIds = snapshot.MatchingProcessIds;
            if (sessionIds.Count > 0) return sessionIds;
            if (DateTimeOffset.UtcNow >= nextProgress)
            {
                _logger.Debug(
                    "game.waiting_for_start",
                    $"等待目标进程。elapsed={(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0}s；"
                    + $"remaining={(deadline - DateTimeOffset.UtcNow).TotalSeconds:0.0}s；names={string.Join(',', configuration.WatchProcessNames)}；"
                    + $"unreadable={snapshot.UnreadableCount}；ignoredPaths={string.Join(';', snapshot.IgnoredPaths)}");
                nextProgress = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            }
            await Task.Delay(configuration.PollInterval);
        }

        return new HashSet<int>();
    }

    private async Task<IReadOnlyDictionary<int, int?>> WaitForGameExitAsync(
        ResolvedBridgeConfiguration configuration,
        IReadOnlySet<int> baseline,
        IReadOnlySet<int> initialSessionIds)
    {
        DateTimeOffset? absentSince = null;
        var nextHeartbeat = DateTimeOffset.UtcNow;
        IReadOnlySet<int> previousIds = new HashSet<int>();
        var trackedProcesses = new Dictionary<int, Process>();
        TrackProcesses(initialSessionIds, trackedProcesses);
        try
        {
            while (true)
            {
                var snapshot = GetSessionSnapshot(configuration, baseline);
                var sessionIds = snapshot.MatchingProcessIds;
                TrackProcesses(sessionIds, trackedProcesses);
                if (sessionIds.Count > 0)
                {
                    if (!sessionIds.SetEquals(previousIds))
                        _logger.Info("game.process_set_changed", $"当前游戏进程 PID={string.Join(',', sessionIds.Order())}；paths={string.Join(';', snapshot.MatchingPaths.Values)}");
                    absentSince = null;
                }
                else
                {
                    if (!absentSince.HasValue)
                    {
                        absentSince = DateTimeOffset.UtcNow;
                        _logger.Warn("game.process_absent", $"目标进程暂时消失，开始等待退出宽限期 {configuration.ExitGrace.TotalSeconds:0.0}s。");
                    }
                    if (DateTimeOffset.UtcNow - absentSince >= configuration.ExitGrace)
                        return ReadExitCodes(trackedProcesses);
                }

                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    _logger.Debug(
                        "game.heartbeat",
                        sessionIds.Count > 0
                            ? $"桥接器保活中。pids={string.Join(',', sessionIds.Order())}；unreadable={snapshot.UnreadableCount}"
                            : $"桥接器处于退出宽限期。absentFor={(DateTimeOffset.UtcNow - absentSince!.Value).TotalSeconds:0.0}s；unreadable={snapshot.UnreadableCount}");
                    nextHeartbeat = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
                }

                previousIds = sessionIds;

                await Task.Delay(configuration.PollInterval);
            }
        }
        finally
        {
            foreach (var process in trackedProcesses.Values) process.Dispose();
        }
    }

    private ProcessSnapshot GetSessionSnapshot(
        ResolvedBridgeConfiguration configuration,
        IReadOnlySet<int> baseline)
    {
        var current = _processes.GetSnapshot(
            configuration.WatchProcessNames,
            configuration.WatchExecutablePaths);
        var ids = current.MatchingProcessIds.Where(processId => !baseline.Contains(processId)).ToHashSet();
        var paths = current.MatchingPaths
            .Where(pair => ids.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return new ProcessSnapshot(ids, paths, current.IgnoredPaths, current.UnreadableCount);
    }

    private void TrackProcesses(IReadOnlySet<int> processIds, Dictionary<int, Process> trackedProcesses)
    {
        foreach (var processId in processIds)
        {
            if (trackedProcesses.ContainsKey(processId)) continue;
            try
            {
                trackedProcesses[processId] = Process.GetProcessById(processId);
            }
            catch (Exception ex)
            {
                _logger.Exception("game.process_handle_failed", ex, $"无法保留游戏进程句柄。pid={processId}");
            }
        }
    }

    private static IReadOnlyDictionary<int, int?> ReadExitCodes(Dictionary<int, Process> trackedProcesses)
    {
        var result = new Dictionary<int, int?>();
        foreach (var (processId, process) in trackedProcesses)
        {
            try
            {
                result[processId] = process.HasExited ? process.ExitCode : null;
            }
            catch
            {
                result[processId] = null;
            }
        }
        return result;
    }

    private static string FormatExitCodes(IReadOnlyDictionary<int, int?> exitCodes) => exitCodes.Count == 0
        ? "unavailable"
        : string.Join(',', exitCodes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value?.ToString() ?? "unavailable"}"));

    private static ProcessStartInfo CreateStartInfo(ResolvedBridgeConfiguration configuration)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = configuration.ExecutablePath,
            WorkingDirectory = configuration.WorkingDirectory,
            UseShellExecute = configuration.RunAsAdministrator,
        };

        if (configuration.RunAsAdministrator)
            startInfo.Verb = "runas";
        foreach (var argument in configuration.Arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}

public static class BridgeExitCode
{
    public const int Success = 0;
    public const int InvalidConfiguration = 3;
    public const int AlreadyRunning = 4;
    public const int GameAlreadyRunning = 5;
    public const int ElevationCancelled = 6;
    public const int LaunchFailed = 7;
    public const int GameStartTimedOut = 8;
    public const int UnexpectedError = 20;
}
