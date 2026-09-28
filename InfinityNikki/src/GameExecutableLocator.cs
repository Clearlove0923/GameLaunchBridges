namespace InfinityNikki.LaunchBridge;

public static class GameExecutableLocator
{
    private static readonly string RelativeBinaryDirectory = Path.Combine(
        "X6Game", "Binaries", "Win64");

    public static IReadOnlyList<string> Resolve(string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(launcherRoot));
        var candidates = new List<string>();

        AddFromGameRoot(candidates, root);
        AddFromGameRoot(candidates, Path.Combine(root, "InfinityNikki"));

        try
        {
            foreach (var child in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(child);
                if (name.Contains("Nikki", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("暖暖", StringComparison.OrdinalIgnoreCase)
                    || Directory.Exists(Path.Combine(child, "X6Game")))
                {
                    AddFromGameRoot(candidates, child);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Known paths above remain usable when optional child enumeration is denied.
        }

        if (candidates.Count == 0)
        {
            throw new BridgeConfigurationException(
                $"在安装根目录 {root} 下未找到 X6Game 的 Shipping 可执行文件。请先通过官方启动器完成更新和资源准备。");
        }

        return candidates;
    }

    private static void AddFromGameRoot(List<string> candidates, string gameRoot)
    {
        var binaryDirectory = Path.Combine(gameRoot, RelativeBinaryDirectory);
        if (!Directory.Exists(binaryDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(
                     binaryDirectory,
                     "*-Win64-Shipping.exe",
                     SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                candidates.Add(fullPath);
        }
    }
}
