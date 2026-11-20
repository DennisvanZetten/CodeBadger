using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodeBadger.Services;

/// <summary>Stages only a build-time pinned runtime from signed app assets.
/// No executable discovery through PATH, downloads, or repository code loading.</summary>
public static class SandboxSdkRuntime
{
    public static async Task<byte[]> LoadArchiveAsync()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Direct SDK mode currently requires x64 Windows.");
        using var manifestStream = await FileSystem.OpenAppPackageFileAsync("sandbox-sdk/runtime.json");
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        var root = manifest.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("sdk").GetString() != "2.3.2" ||
            root.GetProperty("node").GetString() != "24.11.0" || root.GetProperty("architecture").GetString() != "x64")
            throw new InvalidDataException("Unsupported packaged SDK runtime.");
        using var archive = await FileSystem.OpenAppPackageFileAsync("sandbox-sdk/runtime.zip");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await archive.ReadAsync(buffer)) > 0)
        {
            if (bytes.Length + count > 512L * 1024 * 1024) throw new InvalidDataException("SDK runtime archive exceeds its safety bound.");
            bytes.Write(buffer, 0, count);
        }
        byte[] result = bytes.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(result)).Equals(root.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SDK runtime integrity check failed. No repository access was granted.");
        return result;
    }

    public static string Stage(string profile, byte[] archiveBytes, string runnerSource, string transportSource, string chatPipe)
    {
        using var bytes = new MemoryStream(archiveBytes, writable: false);
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        string runtime = Path.Combine(profile, "sdk-runtime");
        Directory.CreateDirectory(runtime);
        long total = 0;
        if (zip.Entries.Count > 50000) throw new InvalidDataException("SDK archive has too many entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string relative = entry.FullName;
            if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) || !paths.Add(relative))
                throw new InvalidDataException("Unsafe SDK archive path.");
            if (entry.Length > 256L * 1024 * 1024 || (total = checked(total + entry.Length)) > 1536L * 1024 * 1024)
                throw new InvalidDataException("SDK runtime expansion exceeds its safety bound.");
            string destination = Path.Combine(runtime, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        string executable = Path.Combine(runtime, "node.exe");
        using (var node = File.OpenRead(executable))
            if (Convert.ToHexString(SHA256.HashData(node)) != "B7D912484D42E7A0D0CB5B26A86410EC973A79ECE7D61AD535E2D1A97A9026E1")
                throw new InvalidDataException("Staged Node runtime integrity check failed.");
        File.WriteAllText(Path.Combine(runtime, "sdk-runner.mjs"), runnerSource);
        File.WriteAllText(Path.Combine(runtime, "provider-transport.mjs"), transportSource);
        File.WriteAllText(Path.Combine(runtime, "chat.json"), JsonSerializer.Serialize(new { pipeName = chatPipe }));
        return executable;
    }
}
