namespace InfinityNikki.LaunchBridge;

public enum GraphicsApi
{
    Default,
    DirectX11,
    DirectX12,
}

public static class GraphicsApiOverride
{
    private static readonly HashSet<string> RendererArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "-dx11",
        "-d3d11",
        "-dx12",
        "-d3d12",
    };

    public static ResolvedBridgeConfiguration Apply(
        ResolvedBridgeConfiguration configuration,
        GraphicsApi graphicsApi)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (graphicsApi == GraphicsApi.Default)
            return configuration;

        var arguments = configuration.Arguments
            .Where(argument => !RendererArguments.Contains(argument))
            .Append(graphicsApi == GraphicsApi.DirectX11 ? "-dx11" : "-dx12")
            .ToArray();
        return configuration with { Arguments = arguments };
    }
}
