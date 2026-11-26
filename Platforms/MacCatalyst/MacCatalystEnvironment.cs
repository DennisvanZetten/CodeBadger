namespace CodeBadger;

/// <summary>Ensures apps launched from Finder or an IDE can locate common developer CLIs.</summary>
internal static class MacCatalystEnvironment
{
    public static void ConfigureProcessPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("PATH"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".codex", "bin"),
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".opencode", "bin"),
            Path.Combine(home, ".cargo", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".volta", "bin"),
            Path.Combine(home, ".local", "share", "pnpm"),
            Path.Combine(home, ".local", "state", "mise", "shims"),
            Path.Combine(home, ".local", "share", "hermes", "bin"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".nvm", "current", "bin"),
            Path.Combine(home, ".asdf", "shims"),
            Path.Combine(home, ".local", "share", "mise", "shims"),
            Path.Combine(home, ".config", "hermes"),
            Path.Combine(home, "Library", "pnpm"),
            GetBinDirectory("NPM_CONFIG_PREFIX"),
            GetBinDirectory("PNPM_HOME"),
            GetBinDirectory("BUN_INSTALL"),
            Path.Combine(Environment.GetEnvironmentVariable("VOLTA_HOME") ?? Path.Combine(home, ".volta"), "bin"),
            Path.Combine(Environment.GetEnvironmentVariable("ASDF_DATA_DIR") ?? Path.Combine(home, ".asdf"), "shims"),
            Path.Combine(Environment.GetEnvironmentVariable("MISE_DATA_DIR") ?? Path.Combine(home, ".local", "share", "mise"), "shims"),
            Environment.GetEnvironmentVariable("NVM_SYMLINK") is { Length: > 0 } nvmSymlink
                ? Path.Combine(nvmSymlink, "bin") : null,
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/opt/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin"
        };

        candidates.Add(Environment.GetEnvironmentVariable("PNPM_HOME"));
        candidates.Add(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "bin"));

        var nvmVersions = Path.Combine(home, ".nvm", "versions", "node");
        try
        {
            if (Directory.Exists(nvmVersions))
                candidates.AddRange(Directory.EnumerateDirectories(nvmVersions)
                    .Select(versionDirectory => Path.Combine(versionDirectory, "bin")));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var paths = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths));
    }

    private static string? GetBinDirectory(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : Path.Combine(value, "bin");
    }
}
