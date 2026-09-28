namespace InfinityNikki.LaunchBridge;

public static class BridgeConfigurationResolver
{
    public static ResolvedBridgeConfiguration Resolve(BridgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.SchemaVersion != 1)
            throw new BridgeConfigurationException($"不支持 schemaVersion={configuration.SchemaVersion}，当前仅支持 1。");

        var launcherRoot = ResolveRequiredDirectory(configuration.LauncherRoot, "launcherRoot");
        var executablePath = ExecutableResolver.Resolve(launcherRoot, configuration.Executable);
        var workingDirectory = ResolveWorkingDirectory(launcherRoot, configuration.WorkingDirectory);
        var processPaths = GameExecutableLocator.Resolve(launcherRoot);
        var processNames = ResolveProcessNames(
            configuration.WatchProcessNames
                .Concat(processPaths.Select(Path.GetFileNameWithoutExtension))
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .ToArray());

        if (configuration.StartupTimeoutSeconds is < 5 or > 1800)
            throw new BridgeConfigurationException("startupTimeoutSeconds 必须介于 5 到 1800 秒之间。");
        if (configuration.PollIntervalMilliseconds is < 200 or > 10000)
            throw new BridgeConfigurationException("pollIntervalMilliseconds 必须介于 200 到 10000 毫秒之间。");
        if (configuration.ExitGraceSeconds is < 0 or > 120)
            throw new BridgeConfigurationException("exitGraceSeconds 必须介于 0 到 120 秒之间。");

        var logPath = BridgeLogger.DefaultLogPath;

        return new ResolvedBridgeConfiguration(
            configuration.SourcePath,
            launcherRoot,
            executablePath,
            configuration.Arguments.Where(value => value is not null).ToArray(),
            workingDirectory,
            configuration.RunAsAdministrator,
            processNames,
            processPaths,
            TimeSpan.FromSeconds(configuration.StartupTimeoutSeconds),
            TimeSpan.FromMilliseconds(configuration.PollIntervalMilliseconds),
            TimeSpan.FromSeconds(configuration.ExitGraceSeconds),
            configuration.RefuseIfAlreadyRunning,
            logPath);
    }

    private static string ResolveRequiredDirectory(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BridgeConfigurationException($"{propertyName} 不能为空。");

        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        if (!Directory.Exists(path))
            throw new BridgeConfigurationException($"{propertyName} 目录不存在：{path}");
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string ResolveWorkingDirectory(string launcherRoot, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || configuredPath.Trim() == ".")
            return launcherRoot;

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        var path = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(launcherRoot, expanded));

        if (!Directory.Exists(path))
            throw new BridgeConfigurationException($"workingDirectory 目录不存在：{path}");
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static IReadOnlyList<string> ResolveProcessNames(IReadOnlyList<string> configuredNames)
    {
        if (configuredNames is null || configuredNames.Count == 0)
            throw new BridgeConfigurationException("watchProcessNames 至少需要一个进程名。");

        var result = new List<string>();
        foreach (var configuredName in configuredNames)
        {
            if (string.IsNullOrWhiteSpace(configuredName))
                throw new BridgeConfigurationException("watchProcessNames 不能包含空值。");

            var name = configuredName.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.Length == 0 || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                throw new BridgeConfigurationException($"无效的进程名：{configuredName}");
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }

        return result;
    }
}
