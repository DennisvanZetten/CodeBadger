namespace CodeBadger;

/// <summary>Ensures apps launched from Finder or an IDE can locate common developer CLIs.</summary>
internal static class MacCatalystEnvironment
{
    public static void ConfigureProcessPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".opencode", "bin"),
            Path.Combine(home, ".cargo", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".volta", "bin"),
            Path.Combine(home, ".local", "share", "pnpm"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".nvm", "current", "bin"),
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

        var paths = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths));
    }
}
