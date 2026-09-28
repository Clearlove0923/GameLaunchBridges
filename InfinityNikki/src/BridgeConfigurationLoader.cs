using System.Text.Json;

namespace InfinityNikki.LaunchBridge;

public static class BridgeConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BridgeConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(fullPath))
            throw new BridgeConfigurationException($"配置文件不存在：{fullPath}");

        try
        {
            var json = File.ReadAllText(fullPath);
            var configuration = JsonSerializer.Deserialize<BridgeConfiguration>(json, JsonOptions)
                ?? throw new BridgeConfigurationException("配置文件内容为空。");
            configuration.SourcePath = fullPath;
            return configuration;
        }
        catch (JsonException ex)
        {
            throw new BridgeConfigurationException($"配置文件不是有效 JSON：{ex.Message}", ex);
        }
    }
}

public sealed class BridgeConfigurationException : Exception
{
    public BridgeConfigurationException(string message) : base(message) { }

    public BridgeConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}
