using Microsoft.Win32;

namespace InfinityNikki.LaunchBridge;

public static class InfinityNikkiAutoDiscovery
{
    private const string StarterFileName = "xstarter.exe";

    public static ResolvedBridgeConfiguration Discover(
        string bridgeDirectory,
        bool includeSystemLocations = true,
        BridgeLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeDirectory);

        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bridgeDirectory));
        var candidates = BuildCandidateRoots(baseDirectory, includeSystemLocations);
        var attempted = new List<string>();
        logger?.Info("discovery.start", $"开始自动发现。bridgeDirectory={baseDirectory}；candidateCount={candidates.Count}；includeSystemLocations={includeSystemLocations}");

        foreach (var candidate in candidates)
        {
            attempted.Add(candidate);
            logger?.Debug("discovery.probe", $"检查候选目录：{candidate}");
            if (TryResolveStarter(candidate, out var starterPath))
            {
                logger?.Info("discovery.success", $"发现启动程序。candidate={candidate}；starter={starterPath}");
                return CreateConfiguration(baseDirectory, candidate, starterPath);
            }
        }

        var attemptedSummary = string.Join("；", attempted.Take(12));
        throw new BridgeConfigurationException(
            $"自动发现失败：未找到版本目录中的 {StarterFileName}。已检查：{attemptedSummary}。"
            + "请把桥接器放在包含 launcher.exe 与版本目录的安装根目录，或改用 --config。 ");
    }

    private static IReadOnlyList<string> BuildCandidateRoots(string baseDirectory, bool includeSystemLocations)
    {
        var candidates = new List<string>();
        AddWithImmediateChildren(candidates, baseDirectory);

        var parent = Directory.GetParent(baseDirectory);
        for (var depth = 0; depth < 3 && parent is not null; depth++, parent = parent.Parent)
            AddCandidate(candidates, parent.FullName);

        if (includeSystemLocations)
        {
            foreach (var registryPath in EnumerateLauncherPathsFromRegistry())
                AddWithImmediateChildren(candidates, registryPath);

            foreach (var commonRoot in EnumerateCommonRoots())
                AddWithImmediateChildren(candidates, commonRoot);
        }

        return candidates;
    }

    private static void AddWithImmediateChildren(List<string> candidates, string path)
    {
        AddCandidate(candidates, path);
        try
        {
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(child);
                if (name.Contains("Nikki", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("暖暖", StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(child, "launcher.exe")))
                {
                    AddCandidate(candidates, child);
                }
            }
        }
        catch
        {
            // An inaccessible optional search location must not block other candidates.
        }
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));
            if (Directory.Exists(fullPath) && !candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                candidates.Add(fullPath);
        }
        catch
        {
            // Ignore malformed values obtained from optional discovery sources.
        }
    }

    private static bool TryResolveStarter(string candidateRoot, out string starterPath)
    {
        var directStarter = Path.Combine(candidateRoot, StarterFileName);
        if (File.Exists(directStarter))
        {
            var directoryName = Path.GetFileName(candidateRoot);
            if (ExecutableResolver.IsVersionDirectoryName(directoryName))
            {
                starterPath = directStarter;
                return true;
            }
        }

        try
        {
            starterPath = ExecutableResolver.Resolve(
                candidateRoot,
                new ExecutableDiscoveryConfiguration
                {
                    RelativePath = StarterFileName,
                    SearchVersionDirectories = true,
                });
            return true;
        }
        catch (BridgeConfigurationException)
        {
            starterPath = "";
            return false;
        }
        catch
        {
            starterPath = "";
            return false;
        }
    }

    private static ResolvedBridgeConfiguration CreateConfiguration(
        string baseDirectory,
        string discoveredRoot,
        string starterPath)
    {
        var versionDirectory = Path.GetDirectoryName(starterPath)!;
        var launcherRoot = ExecutableResolver.IsVersionDirectoryName(Path.GetFileName(versionDirectory))
            ? Path.GetDirectoryName(versionDirectory)!
            : discoveredRoot;
        var watchExecutablePaths = GameExecutableLocator.Resolve(launcherRoot);
        var watchProcessNames = watchExecutablePaths
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ResolvedBridgeConfiguration(
            Environment.ProcessPath ?? Path.Combine(baseDirectory, "InfinityNikkiLaunchBridge.exe"),
            launcherRoot,
            starterPath,
            ["-skiplauncher"],
            launcherRoot,
            true,
            watchProcessNames,
            watchExecutablePaths,
            TimeSpan.FromSeconds(180),
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromSeconds(8),
            true,
            BridgeLogger.DefaultLogPath);
    }

    private static IEnumerable<string> EnumerateCommonRoots()
    {
        var baseFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var baseFolder in baseFolders.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            yield return Path.Combine(baseFolder, "InfinityNikkiGlobal Launcher");
            yield return Path.Combine(baseFolder, "InfinityNikkiOversea");
            yield return Path.Combine(baseFolder, "Infinity Nikki");
        }
    }

    private static IEnumerable<string> EnumerateLauncherPathsFromRegistry()
    {
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        foreach (var hive in hives)
        foreach (var view in views)
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstallKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || (!displayName.Contains("Infinity Nikki", StringComparison.OrdinalIgnoreCase)
                            && !displayName.Contains("无限暖暖", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    foreach (var valueName in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
                    {
                        var value = subKey?.GetValue(valueName) as string;
                        var path = ExtractDirectory(value);
                        if (!string.IsNullOrWhiteSpace(path)) yield return path;
                    }
                }
            }
            finally
            {
                uninstallKey?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    private static string? ExtractDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = Environment.ExpandEnvironmentVariables(value.Trim());
        string candidate;
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            candidate = closingQuote > 1 ? trimmed[1..closingQuote] : trimmed.Trim('"');
        }
        else
        {
            var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = executableEnd >= 0 ? trimmed[..(executableEnd + 4)] : trimmed;
        }

        try
        {
            if (Directory.Exists(candidate)) return candidate;
            return Path.GetDirectoryName(candidate);
        }
        catch
        {
            return null;
        }
    }
}
