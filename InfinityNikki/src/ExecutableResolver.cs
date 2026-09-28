namespace InfinityNikki.LaunchBridge;

public static class ExecutableResolver
{
    public static string Resolve(string launcherRoot, ExecutableDiscoveryConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.RelativePath))
            throw new BridgeConfigurationException("executable.relativePath 不能为空。");

        var relativePath = configuration.RelativePath.Trim();
        if (Path.IsPathRooted(relativePath))
            throw new BridgeConfigurationException("executable.relativePath 必须是相对路径。");

        if (!configuration.SearchVersionDirectories)
        {
            var directPath = SafeCombine(launcherRoot, relativePath);
            if (!File.Exists(directPath))
                throw new BridgeConfigurationException($"启动程序不存在：{directPath}");
            return directPath;
        }

        var candidates = Directory.EnumerateDirectories(launcherRoot)
            .Select(path => new { Path = path, Version = ParseVersionDirectory(Path.GetFileName(path)) })
            .Where(candidate => candidate.Version is not null)
            .Select(candidate => new
            {
                candidate.Path,
                Version = candidate.Version!,
                ExecutablePath = SafeCombine(candidate.Path, relativePath),
            })
            .Where(candidate => File.Exists(candidate.ExecutablePath))
            .OrderByDescending(candidate => candidate.Version, NumericVersionComparer.Instance)
            .ThenByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            throw new BridgeConfigurationException(
                $"在 {launcherRoot} 的版本目录中未找到 {relativePath}。版本目录必须采用点分数字格式，例如 x.y.z。");

        return candidates[0].ExecutablePath;
    }

    internal static bool IsVersionDirectoryName(string directoryName) =>
        ParseVersionDirectory(directoryName) is not null;

    private static IReadOnlyList<int>? ParseVersionDirectory(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)) return null;
        var parts = directoryName.Split('.');
        if (parts.Length < 2) return null;
        var values = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 || !int.TryParse(parts[index], out values[index]) || values[index] < 0)
                return null;
        }
        return values;
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var relative = Path.GetRelativePath(normalizedRoot, combined);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new BridgeConfigurationException($"启动程序路径不能离开 launcherRoot：{relativePath}");
        }

        return combined;
    }

    private sealed class NumericVersionComparer : IComparer<IReadOnlyList<int>>
    {
        public static NumericVersionComparer Instance { get; } = new();

        public int Compare(IReadOnlyList<int>? left, IReadOnlyList<int>? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var length = Math.Max(left.Count, right.Count);
            for (var index = 0; index < length; index++)
            {
                var leftValue = index < left.Count ? left[index] : 0;
                var rightValue = index < right.Count ? right[index] : 0;
                var comparison = leftValue.CompareTo(rightValue);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
