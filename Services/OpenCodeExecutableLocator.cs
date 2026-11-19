namespace CodeBadger.Services;

/// <summary>Finds the standalone executable without executing shell wrappers.</summary>
public static class OpenCodeExecutableLocator
{
    public static string Find()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OpenCode sandbox requires Windows.");
        return FindInPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            ?? throw new FileNotFoundException("OpenCode executable could not be detected. Install OpenCode and ensure it is available in CodeBadger's PATH.");
    }

    public static string? FindInPath(string searchPath)
    {
        foreach (string directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string root = directory.Trim('"');
            if (!Path.IsPathFullyQualified(root)) continue;
            foreach (string candidate in new[]
            {
                Path.Combine(root, "opencode.exe"),
                Path.Combine(root, "node_modules", "@opencode", "cli", "bin", "opencode.exe"),
                Path.Combine(root, "node_modules", "opencode-ai", "bin", "opencode.exe"),
                Path.Combine(root, "node_modules", "opencode-ai", "node_modules", "opencode-windows-x64", "bin", "opencode.exe")
            })
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }
}
