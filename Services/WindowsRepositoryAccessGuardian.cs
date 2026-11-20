#nullable enable annotations
namespace CodeBadger.Services
{
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

/// <summary>Trusted out-of-process ACL lifetime owner, with durable restart recovery.
/// Requires a system-installed PowerShell 7; never falls back to the host PATH.</summary>
public sealed class WindowsRepositoryAccessGuardian : IDisposable
{
    private readonly Process _process;
    private bool _revoked;
    private WindowsRepositoryAccessGuardian(Process process) => _process = process;
    public Task Completion => _process.WaitForExitAsync();

    public static async Task RecoverPendingAsync(string privateDirectory, Func<string, Task<Stream>> openAsset)
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(privateDirectory, "sandbox-repository-recovery");
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Linked repository recovery directory is unsupported.");
        if (Directory.EnumerateFiles(directory, "lease-*.json").Any()) await PrepareAsync(privateDirectory, openAsset);
    }

    public static WindowsRepositoryAccessGuardian Grant(string root, IReadOnlyList<string> files, string sid,
        string recoveryDirectory, string jobName)
    {
        string journal = Path.Combine(recoveryDirectory, "lease-" + Guid.NewGuid().ToString("N") + ".json");
        var process = Launch(recoveryDirectory);
        var guardian = new WindowsRepositoryAccessGuardian(process);
        try
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { operation = "grant", root, files, sid, journal, jobName }));
            process.StandardInput.Flush();
            guardian.Expect("READY");
            return guardian;
        }
        catch
        {
            // Never kill the guardian: EOF requests rollback, including partial preparation.
            process.StandardInput.Close();
            if (!process.WaitForExit(30000)) throw new IOException("Repository guardian recovery is still pending. Start refused.");
            process.Dispose();
            throw;
        }
    }

    public static async Task<string> PrepareAsync(string privateDirectory, Func<string, Task<Stream>> openAsset)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string directory = Path.Combine(privateDirectory, "sandbox-repository-recovery");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Recovery directory must not be a link.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Host identity unavailable.");
        security.SetOwner(user);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        new DirectoryInfo(directory).SetAccessControl(security);
        // Lock against concurrent recovery/start by another CodeBadger instance.
        using var gate = new FileStream(Path.Combine(directory, "guardian.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var asset in new[] { "repository-guardian.ps1", "WindowsRepositoryAccessLease.cs" })
        {
            using var input = await openAsset("sandbox/" + asset);
            string destination = Path.Combine(directory, asset);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Linked guardian asset is unsupported.");
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);
            output.Flush(true);
        }
        foreach (string journal in Directory.EnumerateFiles(directory, "lease-*.json"))
        {
            if ((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Linked recovery journal is unsupported.");
            using var process = Launch(directory);
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { operation = "recover", journal }));
            process.StandardInput.Close();
            new WindowsRepositoryAccessGuardian(process).Expect("RECOVERED");
            if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new IOException("Repository recovery failed; direct mode is blocked.");
        }
        return directory;
    }

    private static Process Launch(string directory)
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(executable)) throw new PlatformNotSupportedException("Direct repository mode requires system-installed PowerShell 7 for independent ACL recovery. OpenCode was not started.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory
        };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(directory, "repository-guardian.ps1"));
        // Guardian does not need provider keys, user startup hooks, or host module paths.
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["TEMP"] = directory; start.Environment["TMP"] = directory;
        return Process.Start(start) ?? throw new IOException("Windows could not start the repository guardian.");
    }

    private void Expect(string expected)
    {
        string? response = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (response != expected)
            throw new IOException("Repository guardian failed: " + _process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        if (_revoked) return;
        _process.StandardInput.Close();
        Expect("REVOKED");
        if (!_process.WaitForExit(30000) || _process.ExitCode != 0) throw new IOException("Repository access revocation was not confirmed.");
        _revoked = true;
        _process.Dispose();
    }
}
}
