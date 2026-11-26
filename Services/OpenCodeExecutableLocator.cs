using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeBadger.Services;

/// <summary>Finds the standalone executable without executing shell wrappers.</summary>
public static class OpenCodeExecutableLocator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    private static string? _preferredExecutable;
    private static bool _healthCheckCompleted;

    public static void SetPreferredExecutable(string? executable) =>
        Interlocked.Exchange(ref _preferredExecutable, string.IsNullOrWhiteSpace(executable) ? null : Path.GetFullPath(executable));

    public static string? PreferredExecutable => Volatile.Read(ref _preferredExecutable);
    public static bool HealthCheckCompleted => Volatile.Read(ref _healthCheckCompleted);

    public static void SetHealthCheckResult(string? executable)
    {
        Interlocked.Exchange(ref _preferredExecutable, string.IsNullOrWhiteSpace(executable) ? null : Path.GetFullPath(executable));
        Volatile.Write(ref _healthCheckCompleted, true);
    }

    public static string Find()
    {
        if (PreferredExecutable is { } preferred && File.Exists(preferred))
            return preferred;
        if (HealthCheckCompleted)
            throw new FileNotFoundException("No working OpenCode executable passed CodeBadger's startup health check.");
        return FindInPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            ?? throw new FileNotFoundException("OpenCode executable could not be detected. Install OpenCode and ensure it is available in CodeBadger's PATH.");
    }

    public static IReadOnlyList<string> FindAllInPath(string searchPath)
    {
        var executables = new List<string>();
        foreach (var directory in searchPath.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var root = directory.Trim('"');
            if (!Path.IsPathFullyQualified(root))
                continue;

            var executableName = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
            var candidates = OperatingSystem.IsWindows()
                ? new[]
                {
                    Path.Combine(root, executableName),
                    Path.Combine(root, "node_modules", "@opencode", "cli", "bin", executableName),
                    Path.Combine(root, "node_modules", "opencode-ai", "bin", executableName),
                    Path.Combine(root, "node_modules", "opencode-ai", "node_modules", "opencode-windows-x64", "bin", executableName)
                }
                : new[]
                {
                    Path.Combine(root, executableName),
                    Path.Combine(root, "node_modules", "@opencode", "cli", "bin", executableName),
                    Path.Combine(root, "node_modules", "opencode-ai", "bin", executableName)
                };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    var fullPath = CanonicalizePath(candidate);
                    if (!executables.Any(existing => AreSameFile(existing, fullPath)))
                        executables.Add(fullPath);
                }
            }
        }
        return executables;
    }

    public static string CanonicalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var relative = fullPath[root.Length..];
        var current = root;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo info = index == segments.Length - 1 && File.Exists(current)
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            try
            {
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Path.GetFullPath(current);
    }

    public static bool AreSameFile(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!OperatingSystem.IsWindows())
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
        if (!File.Exists(first) || !File.Exists(second))
            return false;
        if (string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison))
            return true;
        try
        {
            using var firstHandle = File.OpenHandle(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var secondHandle = File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(firstHandle, out var firstInfo) || !GetFileInformationByHandle(secondHandle, out var secondInfo))
                return false;
            return firstInfo.VolumeSerialNumber == secondInfo.VolumeSerialNumber &&
                   firstInfo.FileIndexHigh == secondInfo.FileIndexHigh && firstInfo.FileIndexLow == secondInfo.FileIndexLow;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string? FindInPath(string searchPath)
    {
        return FindAllInPath(searchPath).FirstOrDefault();
    }
}
