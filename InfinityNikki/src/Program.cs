using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InfinityNikki.LaunchBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var logger = new BridgeLogger();
        var stopwatch = Stopwatch.StartNew();
        var exitCode = BridgeExitCode.UnexpectedError;
        logger.Info(
            "session.start",
            $"程序启动。version={typeof(Program).Assembly.GetName().Version}；baseDirectory={AppContext.BaseDirectory}；"
            + $"currentDirectory={Environment.CurrentDirectory}；os={RuntimeInformation.OSDescription}；"
            + $"processArchitecture={RuntimeInformation.ProcessArchitecture}；osArchitecture={RuntimeInformation.OSArchitecture}；runtime={Environment.Version}；"
            + $"arguments={FormatArguments(args)}；log={logger.LogPath}");
        try
        {
            var commandLine = CommandLine.Parse(args);
            logger.Info(
                "command.parsed",
                $"autoDiscover={commandLine.AutoDiscover}；validateOnly={commandLine.ValidateOnly}；configurationPath={commandLine.ConfigurationPath ?? "<none>"}");
            var resolved = commandLine.AutoDiscover
                ? InfinityNikkiAutoDiscovery.Discover(AppContext.BaseDirectory, logger: logger)
                : BridgeConfigurationResolver.Resolve(
                    BridgeConfigurationLoader.Load(commandLine.ConfigurationPath!));
            logger.Info(
                "configuration.resolved",
                $"mode={(commandLine.AutoDiscover ? "auto" : "config")}；source={resolved.SourcePath}；launcherRoot={resolved.LauncherRoot}；"
                + $"executable={resolved.ExecutablePath}；workingDirectory={resolved.WorkingDirectory}；runAsAdministrator={resolved.RunAsAdministrator}；"
                + $"arguments={FormatArguments(resolved.Arguments)}；watchProcesses={string.Join(',', resolved.WatchProcessNames)}；"
                + $"watchExecutablePaths={string.Join(';', resolved.WatchExecutablePaths)}；"
                + $"startupTimeout={resolved.StartupTimeout}；pollInterval={resolved.PollInterval}；exitGrace={resolved.ExitGrace}；"
                + $"refuseIfAlreadyRunning={resolved.RefuseIfAlreadyRunning}");

            using var instanceLock = BridgeInstanceLock.TryAcquire(resolved.SourcePath);
            if (!instanceLock.Acquired)
            {
                logger.Error("instance.lock_failed", "同一配置的桥接实例已经在运行。");
                exitCode = BridgeExitCode.AlreadyRunning;
            }
            else if (commandLine.ValidateOnly)
            {
                logger.Info("validation.success", $"配置验证通过。启动程序={resolved.ExecutablePath}；监测进程={string.Join(',', resolved.WatchProcessNames)}。");
                exitCode = BridgeExitCode.Success;
            }
            else
            {
                logger.Info("instance.lock_acquired", "已取得单实例锁，准备启动游戏。");
                var coordinator = new BridgeCoordinator(new ProcessSnapshotProvider(), logger);
                exitCode = await coordinator.RunAsync(resolved);
            }
        }
        catch (BridgeConfigurationException ex)
        {
            logger.Exception("configuration.error", ex, "配置或自动发现失败");
            FailureNotifier.Show(ex.Message, logger);
            exitCode = BridgeExitCode.InvalidConfiguration;
        }
        catch (Exception ex)
        {
            logger.Exception("session.unhandled_exception", ex, "未处理错误");
            FailureNotifier.Show("桥接器发生未处理错误，请查看运行日志。\n\n" + ex.Message, logger);
            exitCode = BridgeExitCode.UnexpectedError;
        }
        finally
        {
            stopwatch.Stop();
            logger.Info("session.end", $"程序结束。exitCode={exitCode}；elapsed={stopwatch.Elapsed}");
        }

        return exitCode;
    }

    private static string FormatArguments(IEnumerable<string> arguments) => string.Join(
        ' ',
        arguments.Select(argument => argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument));
}
