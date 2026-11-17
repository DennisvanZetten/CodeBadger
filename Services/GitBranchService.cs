using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RepoLauncher.Models;

namespace RepoLauncher.Services;

public sealed class GitBranchService
{
    public async Task<GitRepositorySnapshot> GetRepositorySnapshotAsync(string path, bool fetch = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return GitRepositorySnapshot.Invalid("Pad bestaat niet");

        var branchResult = await RunGitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        if (!branchResult.Success)
            return GitRepositorySnapshot.Invalid(branchResult.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? "Geen Git-repository"
                : branchResult.Error);

        string? warning = null;
        if (fetch)
        {
            var fetchResult = await RunGitAsync(path, "fetch", "--all", "--prune");
            if (!fetchResult.Success)
                warning = $"Ophalen van remotes mislukt: {fetchResult.Error}";
        }

        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        var branchesResult = await RunGitAsync(path, "for-each-ref", "--format=%(refname)%09%(HEAD)", "refs/heads", "refs/remotes");
        var historyResult = await RunGitAsync(path, "log", "--all", "-n", "100", "--date=iso-strict", "--format=%H%x1f%h%x1f%s%x1f%an%x1f%ad");

        if (!statusResult.Success || !branchesResult.Success || !historyResult.Success)
            return GitRepositorySnapshot.Invalid("Git-status kon niet worden opgehaald.");

        var upstreamResult = await RunGitAsync(path, "rev-list", "--left-right", "--count", "HEAD...@{upstream}");
        var ahead = 0;
        var behind = 0;
        if (upstreamResult.Success)
        {
            var counts = upstreamResult.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length == 2)
            {
                int.TryParse(counts[0], out ahead);
                int.TryParse(counts[1], out behind);
            }
        }

        var statusLines = statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        var conflicts = statusLines
            .Where(line => line.Length >= 3 && (line[0] == 'U' || line[1] == 'U' || line.StartsWith("AA", StringComparison.Ordinal) || line.StartsWith("DD", StringComparison.Ordinal)))
            .Select(line => line[3..].Trim().Trim('"'))
            .ToList();

        return new GitRepositorySnapshot(
            branchResult.Output.Trim(),
            statusLines.Length,
            statusLines.Count(line => line.Length >= 2 && line[0] != ' ' && line[0] != '?'),
            statusLines.Count(line => line.Length >= 2 && (line[1] != ' ' || line.StartsWith("??", StringComparison.Ordinal))),
            ahead,
            behind,
            branchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', 2))
                .Where(parts => parts.Length == 2 && !parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
                .Select(parts => new GitBranchEntry(
                    parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? parts[0][11..] : parts[0][13..],
                    parts[1] == "*",
                    parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal)))
                .ToList(),
            historyResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\u001f'))
                .Where(parts => parts.Length == 5)
                .Select(parts => new GitCommitEntry(parts[0], parts[1], parts[2], parts[3], parts[4]))
                .ToList(),
            conflicts,
            null,
            warning);
    }

    public async Task<GitOperationResult> FetchAsync(string path) => await RunGitAsync(path, "fetch", "--all", "--prune");

    public async Task<GitOperationResult> PullAsync(string path) => await RunGitAsync(path, "pull", "--no-rebase");

    public async Task<GitOperationResult> PushAsync(string path) => await RunGitAsync(path, "push");

    public async Task<GitOperationResult> CheckoutBranchAsync(string path, string branch)
    {
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            branch = branch[11..];
        else if (branch.StartsWith("refs/remotes/", StringComparison.Ordinal))
            branch = branch[13..];

        var localBranchResult = await RunGitAsync(path, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}");
        if (localBranchResult.Success)
            return await RunGitAsync(path, "switch", branch);

        return await RunGitAsync(path, "switch", "--track", branch);
    }

    public async Task<GitOperationResult> OpenConflictInVsCodeAsync(string repositoryPath, string relativePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(repositoryPath, relativePath));
            var root = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return new GitOperationResult(false, "Ongeldig conflictenbestand.", string.Empty);

            var baseContent = await RunGitAsync(repositoryPath, "show", $":1:{relativePath}");
            var oursContent = await RunGitAsync(repositoryPath, "show", $":2:{relativePath}");
            var theirsContent = await RunGitAsync(repositoryPath, "show", $":3:{relativePath}");
            if (!baseContent.Success || !oursContent.Success || !theirsContent.Success)
                return new GitOperationResult(false, "De conflictversies konden niet uit Git worden gelezen (mogelijk een nieuw bestand).", string.Empty);

            var gitDirectoryResult = await RunGitAsync(repositoryPath, "rev-parse", "--git-path", "repolauncher-conflicts");
            if (!gitDirectoryResult.Success)
                return new GitOperationResult(false, "De Git-map kon niet worden bepaald.", string.Empty);

            var tempDirectory = Path.IsPathRooted(gitDirectoryResult.Output)
                ? gitDirectoryResult.Output
                : Path.GetFullPath(Path.Combine(repositoryPath, gitDirectoryResult.Output));
            Directory.CreateDirectory(tempDirectory);
            var fileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..12];
            var basePath = Path.Combine(tempDirectory, $"{fileId}-base");
            var oursPath = Path.Combine(tempDirectory, $"{fileId}-ours");
            var theirsPath = Path.Combine(tempDirectory, $"{fileId}-theirs");
            await File.WriteAllTextAsync(basePath, baseContent.Output);
            await File.WriteAllTextAsync(oursPath, oursContent.Output);
            await File.WriteAllTextAsync(theirsPath, theirsContent.Output);

            var startInfo = new ProcessStartInfo { FileName = "code", UseShellExecute = false };
            startInfo.ArgumentList.Add("--merge");
            startInfo.ArgumentList.Add(oursPath);
            startInfo.ArgumentList.Add(theirsPath);
            startInfo.ArgumentList.Add(basePath);
            startInfo.ArgumentList.Add(fullPath);
            if (Process.Start(startInfo) is null)
                return new GitOperationResult(false, "VS Code kon niet worden gestart.", string.Empty);
            return new GitOperationResult(true, "VS Code merge-editor geopend.", string.Empty);
        }
        catch (Win32Exception)
        {
            return new GitOperationResult(false, "VS Code CLI 'code' is niet beschikbaar in PATH.", string.Empty);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return new GitOperationResult(false, $"Vergelijking kon niet worden geopend: {exception.Message}", string.Empty);
        }
    }

    public async Task<GitBranchInfo> GetCurrentBranchAsync(string path)
    {
        var result = await RunGitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        if (result.Success)
            return result.Output.Trim() == "HEAD"
                ? new GitBranchInfo(null, "Detached HEAD")
                : new GitBranchInfo(result.Output.Trim(), null);

        return new GitBranchInfo(null, result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            ? "Geen Git-repository"
            : result.Error.Contains("Pad bestaat niet", StringComparison.OrdinalIgnoreCase) ? "Pad bestaat niet" : "Branch niet beschikbaar");
    }

    private static async Task<GitOperationResult> RunGitAsync(string path, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitOperationResult(false, "Git kon niet worden gestart.", string.Empty);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            return new GitOperationResult(process.ExitCode == 0, process.ExitCode == 0 ? output.Trim() : error.Trim(), output);
        }
        catch (Win32Exception)
        {
            return new GitOperationResult(false, "Git is niet beschikbaar in PATH.", string.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            return new GitOperationResult(false, "Pad bestaat niet.", string.Empty);
        }
        catch (InvalidOperationException exception)
        {
            return new GitOperationResult(false, exception.Message, string.Empty);
        }
    }
}

public sealed record GitBranchInfo(string? Branch, string? Status);
public sealed record GitOperationResult(bool Success, string Message, string Output);
public sealed record GitBranchEntry(string Name, bool IsCurrent, bool IsRemote);
public sealed record GitCommitEntry(string Hash, string ShortHash, string Subject, string Author, string Date);
public sealed record GitRepositorySnapshot(
    string? Branch,
    int ChangedFiles,
    int StagedFiles,
    int UnstagedFiles,
    int Ahead,
    int Behind,
    IReadOnlyList<GitBranchEntry> Branches,
    IReadOnlyList<GitCommitEntry> Commits,
    IReadOnlyList<string> Conflicts,
    string? Error,
    string? Warning = null)
{
    public bool IsValid => Error is null;

    public static GitRepositorySnapshot Invalid(string error) => new(null, 0, 0, 0, 0, 0, [], [], [], error);
}
