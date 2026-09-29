namespace InfinityNikki.LaunchBridge;

public sealed record BridgeCommandLine(
    string? ConfigurationPath,
    bool AutoDiscover,
    bool ValidateOnly,
    GraphicsApi GraphicsApi);

public static class CommandLine
{
    public static BridgeCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? configPath = null;
        var autoDiscover = args.Length == 0;
        var validateOnly = false;
        var graphicsApi = GraphicsApi.Default;
        var trailingSteamCommand = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (trailingSteamCommand)
                continue;

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
            else if (args[index].Equals("--dx11", StringComparison.OrdinalIgnoreCase)
                || args[index].Equals("--d3d11", StringComparison.OrdinalIgnoreCase))
            {
                graphicsApi = SelectGraphicsApi(graphicsApi, GraphicsApi.DirectX11);
            }
            else if (args[index].Equals("--dx12", StringComparison.OrdinalIgnoreCase)
                || args[index].Equals("--d3d12", StringComparison.OrdinalIgnoreCase))
            {
                graphicsApi = SelectGraphicsApi(graphicsApi, GraphicsApi.DirectX12);
            }
            else if (configPath is null && !autoDiscover)
            {
                if (args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new BridgeConfigurationException($"未知参数：{args[index]}");

                // A Steam shortcut may append its original command without an explicit
                // bridge mode. Treat the first positional token as the start of that
                // command and use zero-configuration discovery.
                autoDiscover = true;
                trailingSteamCommand = true;
            }
            else
            {
                // Steam expands %command% after our own arguments. The first token
                // outside the bridge grammar starts that command; ignore its entire tail.
                trailingSteamCommand = true;
            }
        }

        if (autoDiscover && !string.IsNullOrWhiteSpace(configPath))
            throw new BridgeConfigurationException("--auto 和 --config 不能同时使用。");
        if (!autoDiscover && string.IsNullOrWhiteSpace(configPath))
            autoDiscover = true;

        if (autoDiscover)
            return new BridgeCommandLine(null, true, validateOnly, graphicsApi);

        var expanded = Environment.ExpandEnvironmentVariables(configPath!);
        var fullPath = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
        return new BridgeCommandLine(fullPath, false, validateOnly, graphicsApi);
    }

    private static GraphicsApi SelectGraphicsApi(GraphicsApi current, GraphicsApi requested)
    {
        if (current != GraphicsApi.Default && current != requested)
            throw new BridgeConfigurationException("--dx11 和 --dx12 不能同时使用。");
        return requested;
    }
}
