namespace InfinityNikki.LaunchBridge;

public sealed record BridgeCommandLine(string? ConfigurationPath, bool AutoDiscover, bool ValidateOnly);

public static class CommandLine
{
    public static BridgeCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? configPath = null;
        var autoDiscover = args.Length == 0;
        var validateOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new BridgeConfigurationException("--config 后必须提供 JSON 配置文件路径。");
                configPath = args[index];
            }
            else if (args[index].Equals("--auto", StringComparison.OrdinalIgnoreCase))
            {
                autoDiscover = true;
            }
            else if (args[index].Equals("--validate", StringComparison.OrdinalIgnoreCase))
            {
                validateOnly = true;
            }
            else if (configPath is null && !autoDiscover)
            {
                if (args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new BridgeConfigurationException($"未知参数：{args[index]}");

                // A Steam shortcut may append its original command without an explicit
                // bridge mode. Treat the first positional token as the start of that
                // command and use zero-configuration discovery.
                autoDiscover = true;
            }
            // Steam expands %command% after our own arguments. Once a mode has been
            // parsed, trailing original-command tokens are deliberately ignored.
        }

        if (autoDiscover && !string.IsNullOrWhiteSpace(configPath))
            throw new BridgeConfigurationException("--auto 和 --config 不能同时使用。");
        if (!autoDiscover && string.IsNullOrWhiteSpace(configPath))
            autoDiscover = true;

        if (autoDiscover)
            return new BridgeCommandLine(null, true, validateOnly);

        var expanded = Environment.ExpandEnvironmentVariables(configPath!);
        var fullPath = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
        return new BridgeCommandLine(fullPath, false, validateOnly);
    }
}
