using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed class GitBranchService
{
    private readonly LocalizationService _localization;
    private readonly ILogger<GitBranchService> _logger;

    public GitBranchService(LocalizationService localization, ILogger<GitBranchService> logger)
    {
        _localization = localization;
        _logger = logger;
    }

    public async Task<GitRepositorySnapshot> GetRepositorySnapshotAsync(
        string path,
        bool fetch = false,
        Func<GitSnapshotProgress, Task>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return GitRepositorySnapshot.Invalid(_localization.T("Path does not exist"));

        await ReportProgressAsync(progress, fetch ? "Fetching remotes..." : "Checking repository...");
        var branchResult = await RunGitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        if (!branchResult.Success)
            return GitRepositorySnapshot.Invalid(branchResult.Message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? _localization.T("Not a Git repository")
                : branchResult.Message);

        string? warning = null;
        if (fetch)
        {
            var fetchResult = await RunGitAsync(path, "fetch", "--all", "--prune");
            if (!fetchResult.Success)
                warning = _localization.T("Unable to fetch remotes: {0}", fetchResult.Message);
        }

        await ReportProgressAsync(progress, "Loading working tree...");
        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        await ReportProgressAsync(progress, "Loading branches...");
        var branchesResult = await RunGitAsync(path, "for-each-ref", "--format=%(refname)%09%(HEAD)%09%(objectname)", "refs/heads", "refs/remotes");
        var branchCount = branchesResult.Success
            ? branchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => !line.TrimEnd('\r').Split('\t')[0].EndsWith("/HEAD", StringComparison.Ordinal))
            : 0;
        if (branchesResult.Success)
        await ReportProgressAsync(progress, "Loading recent commits...", branchCount);
        var historyResult = await RunGitAsync(path, "log", "--all", "--date-order", "-n", "100", "--date=iso-strict", "--format=%H%x1f%P%x1f%h%x1f%s%x1f%an%x1f%ad");

        if (!statusResult.Success || !branchesResult.Success || !historyResult.Success)
            return GitRepositorySnapshot.Invalid(_localization.T("Git status unavailable."));

        await ReportProgressAsync(progress, "Checking branch status...", branchCount);
        var (ahead, behind) = await GetAheadBehindAsync(path, branchResult.Output.Trim());

        var statusLines = statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        var conflicts = statusLines
            .Where(line => line.Length >= 3 && (line[0] == 'U' || line[1] == 'U' || line.StartsWith("AA", StringComparison.Ordinal) || line.StartsWith("DD", StringComparison.Ordinal)))
            .Select(line => line[3..].Trim().Trim('"'))
            .ToList();

        var branches = branchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length == 3 && !parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
            .Select(parts => new GitBranchEntry(
                parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? parts[0][11..] : parts[0][13..],
                parts[1] == "*",
                parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal),
                parts[2]))
            .ToList();

        var defaultBranchResult = await RunGitAsync(path, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        var defaultBranch = defaultBranchResult.Success ? defaultBranchResult.Output.Trim() : null;

        var commits = historyResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\u001f'))
            .Where(parts => parts.Length == 6)
            .Select(parts => new GitCommitEntry(
                parts[0], parts[1], parts[2], parts[3], parts[4], parts[5],
                parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .ToList();

        await ReportProgressAsync(progress, "Building commit graph...", branches.Count);
        var graphStartedAt = Stopwatch.GetTimestamp();
        var laidOutCommits = AssignGraphLayout(commits, branches, defaultBranch);
        _logger.LogInformation("Built commit graph for {CommitCount} commits and {BranchCount} branches in {ElapsedMilliseconds} ms",
            commits.Count, branches.Count, Stopwatch.GetElapsedTime(graphStartedAt).TotalMilliseconds);
        return new GitRepositorySnapshot(
            branchResult.Output.Trim(),
            statusLines.Length,
            statusLines.Count(line => line.Length >= 2 && line[0] != ' ' && line[0] != '?'),
            statusLines.Count(line => line.Length >= 2 && (line[1] != ' ' || line.StartsWith("??", StringComparison.Ordinal))),
            ahead,
            behind,
            branches,
            laidOutCommits,
            conflicts,
            null,
            warning);
    }

    private static Task ReportProgressAsync(Func<GitSnapshotProgress, Task>? progress, string stage, int? branchCount = null) =>
        progress?.Invoke(new GitSnapshotProgress(stage, branchCount)) ?? Task.CompletedTask;

    private async Task<(int Ahead, int Behind)> GetAheadBehindAsync(string path, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD")
            return (0, 0);

        var upstreamResult = await RunGitAsync(path, "for-each-ref", "--format=%(upstream)", $"refs/heads/{branch}");
        if (!upstreamResult.Success)
            return (0, 0);

        var upstream = upstreamResult.Output.Trim();
        if (!string.IsNullOrWhiteSpace(upstream))
        {
            if (await RefExistsAsync(path, upstream))
            {
                var countsResult = await RunGitAsync(path, "rev-list", "--left-right", "--count", $"refs/heads/{branch}...{upstream}");
                if (countsResult.Success)
                {
                    var counts = countsResult.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (counts.Length == 2 && int.TryParse(counts[0], out var ahead) && int.TryParse(counts[1], out var behind))
                        return (ahead, behind);
                }
            }
        }

        var defaultRemoteResult = await GetDefaultPushRemoteAsync(path, branch);
        if (!defaultRemoteResult.Success)
            return (0, 0);

        var defaultRemote = defaultRemoteResult.Output;
        var remoteHead = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", $"refs/remotes/{defaultRemote}/HEAD");
        string? baseRef = remoteHead.Success ? $"refs/remotes/{remoteHead.Output.Trim()}" : null;
        if (baseRef is not null && !await RefExistsAsync(path, baseRef))
            baseRef = null;

        if (baseRef is null)
        {
            foreach (var candidate in new[] { "main", "master" })
            {
                var candidateRef = $"refs/remotes/{defaultRemote}/{candidate}";
                if (await RefExistsAsync(path, candidateRef))
                {
                    baseRef = candidateRef;
                    break;
                }
            }
        }

        var aheadResult = baseRef is null
            ? await RunGitAsync(path, "rev-list", "--count", $"refs/heads/{branch}")
            : await RunGitAsync(path, "rev-list", "--count", $"{baseRef}..refs/heads/{branch}");
        return aheadResult.Success && int.TryParse(aheadResult.Output.Trim(), out var localAhead)
            ? (localAhead, 0)
            : (0, 0);
    }

    private async Task<bool> RefExistsAsync(string path, string reference)
    {
        var result = await RunGitAllowExitOneAsync(path, "show-ref", "--verify", "--quiet", reference);
        return result.Success;
    }

    public async Task<IReadOnlyList<GitFileChange>> GetFileChangesAsync(string path)
    {
        var result = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!result.Success)
            return [];

        return ParseFileChanges(result.Output);
    }

    public Task<GitOperationResult> GetWorkingTreeStatusAsync(string path) =>
        RunGitAsync(path, "status", "--porcelain=v1", "-uall");

    private static IReadOnlyList<GitFileChange> ParseFileChanges(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length >= 4)
            .Select(line =>
            {
                var indexStatus = line[0];
                var workTreeStatus = line[1];
                var filePath = line[3..].Trim().Trim('"');
                var conflicted = indexStatus == 'U' || workTreeStatus == 'U' ||
                                 line.StartsWith("AA", StringComparison.Ordinal) ||
                                 line.StartsWith("DD", StringComparison.Ordinal);
                return new GitFileChange(
                    filePath,
                    !conflicted && indexStatus != ' ' && indexStatus != '?',
                    conflicted || workTreeStatus != ' ' || line.StartsWith("??", StringComparison.Ordinal),
                    conflicted,
                    line[..2]);
            })
            .ToList();

    public async Task<IReadOnlyList<GitStashEntry>> GetStashesAsync(string path)
    {
        var result = await RunGitAsync(path, "stash", "list", "--format=%gd%x1f%gs%x1f%ci%x1f%H");
        if (!result.Success)
            return [];

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\u001f'))
            .Where(parts => parts.Length == 4)
            .Select(parts => new GitStashEntry(parts[0], parts[1], parts[2], parts[3]))
            .ToList();
    }

    public async Task<GitOperationResult> StashChangesAsync(string path)
    {
        var status = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!status.Success)
            return status;
        if (string.IsNullOrWhiteSpace(status.Output))
            return new GitOperationResult(false, _localization.T("No changes to stash."), string.Empty);

        return await RunGitAsync(path, "stash", "push", "--include-untracked", "--message", "CodeBadger stash");
    }

    public Task<GitOperationResult> PopStashAsync(string path, string stashReference) =>
        RunGitAsync(path, "stash", "pop", stashReference);

    public Task<GitOperationResult> ApplyStashAsync(string path, string stashReference) =>
        RunGitAsync(path, "stash", "apply", stashReference);

    public Task<GitOperationResult> DropStashAsync(string path, string stashReference) =>
        RunGitAsync(path, "stash", "drop", stashReference);

    public async Task<GitOperationResult> EditStashMessageAsync(string path, GitStashEntry stash, string message)
    {
        message = message.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return new GitOperationResult(false, _localization.T("Enter a stash message."), string.Empty);

        var currentStashes = await GetStashesAsync(path);
        var currentIndex = currentStashes.ToList().FindIndex(entry => string.Equals(entry.Reference, stash.Reference, StringComparison.Ordinal));
        if (currentIndex < 0)
            currentIndex = currentStashes.ToList().FindIndex(entry => string.Equals(entry.CommitHash, stash.CommitHash, StringComparison.Ordinal));
        if (currentIndex < 0)
            return new GitOperationResult(false, _localization.T("The selected stash is no longer available."), string.Empty);

        // Git has no in-place rename operation: store a replacement, then remove the original reflog entry.
        // The renamed entry moves to the top of the stash list.
        var storeResult = await RunGitAsync(path, "stash", "store", "--message", message, stash.CommitHash);
        if (!storeResult.Success)
            return storeResult;

        var removeOriginal = await RunGitAsync(path, "stash", "drop", $"stash@{{{currentIndex + 1}}}");
        if (!removeOriginal.Success)
            return new GitOperationResult(false, _localization.T("The new stash message was stored, but the old stash entry could not be removed: {0}", removeOriginal.Message), string.Empty);
        return new GitOperationResult(true, string.Empty, string.Empty);
    }

    public async Task<GitRepositoryStatus> GetRepositoryStatusAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return GitRepositoryStatus.Invalid(_localization.T("Path does not exist"));

        var branchResult = await RunGitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        if (!branchResult.Success)
            return GitRepositoryStatus.Invalid(branchResult.Message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? _localization.T("Not a Git repository")
                : branchResult.Message);

        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!statusResult.Success)
            return GitRepositoryStatus.Invalid(_localization.T("Git status unavailable."));

        var statusLines = statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        var branchName = branchResult.Output.Trim();
        var (ahead, behind) = await GetAheadBehindAsync(path, branchName);
        var branch = branchName == "HEAD"
            ? new GitBranchInfo(null, _localization.T("Detached HEAD"))
            : new GitBranchInfo(branchName, null);
        return new GitRepositoryStatus(
            branch,
            statusLines.Length,
            statusLines.Count(line => line.Length >= 2 && line[0] != ' ' && line[0] != '?'),
            statusLines.Count(line => line.Length >= 2 && (line[1] != ' ' || line.StartsWith("??", StringComparison.Ordinal))),
            ahead,
            behind,
            null);
    }

    public async Task<GitOperationResult> GetFileDiffAsync(string path, GitFileChange file, bool staged)
    {
        if (file.IsUntracked)
            return await RunGitAllowExitOneAsync(path, "diff", "--no-index", "--", "/dev/null", file.Path);

        return staged
            ? await RunGitAsync(path, "diff", "--cached", "--", file.Path)
            : await RunGitAsync(path, "diff", "--", file.Path);
    }

    public async Task<GitOperationResult> GetStagedDiffAsync(string path) =>
        await RunGitAsync(path, "diff", "--cached", "--no-ext-diff", "--", ".");

    public async Task<GitOperationResult> GetCommitFilesAsync(string path, string commitHash) =>
        await RunGitAsync(path, "diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-M", commitHash);

    public async Task<GitOperationResult> GetCommitFileDiffAsync(string path, string commitHash, string filePath) =>
        await RunGitAsync(path, "show", "--format=", "--no-ext-diff", "--find-renames", $"{commitHash}", "--", filePath);

    public async Task<GitOperationResult> CheckoutCommitAsync(string path, string commitHash, bool autoSyncSubmodules = false)
    {
        if (autoSyncSubmodules)
        {
            var submoduleSafety = await CheckSubmoduleTreesCleanAsync(path);
            if (!submoduleSafety.Success)
                return submoduleSafety;
        }
        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!statusResult.Success)
            return new GitOperationResult(false, _localization.T("Could not check the working tree before checkout: {0}", statusResult.Message), string.Empty);
        if (!string.IsNullOrWhiteSpace(statusResult.Output))
            return new GitOperationResult(false, _localization.T("Commit or stash your changes before checking out a commit."), string.Empty);

        var commitResult = await RunGitAsync(path, "rev-parse", "--verify", "--quiet", $"{commitHash}^{{commit}}");
        if (!commitResult.Success)
            return new GitOperationResult(false, _localization.T("The selected commit is no longer available."), string.Empty);

        var checkout = await RunGitAsync(path, "-c", "submodule.recurse=false", "switch", "--detach", commitResult.Output.Trim());
        if (!checkout.Success || !autoSyncSubmodules) return checkout;
        var postCheckoutSafety = await CheckSubmoduleTreesCleanAsync(path);
        if (!postCheckoutSafety.Success)
            return new GitOperationResult(false, _localization.T("Commit checked out, but submodules were not updated to preserve local changes: {0}", postCheckoutSafety.Message), checkout.Output);
        var update = await UpdateSubmodulesWithoutPreflightAsync(path);
        return update.Success ? checkout : new GitOperationResult(false, _localization.T("Commit checked out, but submodules were not fully updated: {0}", update.Message), update.Output, update.ExitCode);
    }

    public async Task<GitCommitBranchCheck> IsCommitOnCurrentBranchAsync(string path, string commitHash)
    {
        var result = await RunGitAllowExitOneAsync(path, "merge-base", "--is-ancestor", commitHash, "HEAD");
        if (!result.Success && result.ExitCode != 1)
            return new GitCommitBranchCheck(null, result.Message);

        return new GitCommitBranchCheck(result.ExitCode == 0, null);
    }

    public async Task<IReadOnlyList<string>> GetBranchesContainingCommitAsync(string path, string commitHash)
    {
        var result = await RunGitAsync(path, "branch", "--all", "--contains", commitHash, "--format=%(refname:short)");
        return result.Success
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(branch => branch.TrimEnd('\r'))
                .ToArray()
            : [];
    }

    public async Task<GitOperationResult> CherryPickCommitAsync(string path, string commitHash) =>
        await RunGitAsync(path, "cherry-pick", commitHash);

    public async Task<GitOperationResult> RevertCommitAsync(string path, string commitHash)
    {
        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!statusResult.Success)
            return new GitOperationResult(false,
                _localization.T("Could not check the working tree before reverting: {0}", statusResult.Message), string.Empty);
        if (!string.IsNullOrWhiteSpace(statusResult.Output))
            return new GitOperationResult(false, _localization.T("Commit or stash your changes before reverting a commit."), string.Empty);

        var commitResult = await RunGitAsync(path, "rev-parse", "--verify", "--quiet", $"{commitHash}^{{commit}}");
        if (!commitResult.Success)
            return new GitOperationResult(false, _localization.T("The selected commit is no longer available."), string.Empty);

        return await RunGitAsync(path, "revert", "--no-edit", commitResult.Output.Trim());
    }

    public async Task<GitOperationResult> ResetToCommitAsync(string path, string commitHash, string mode)
    {
        var resetMode = mode switch
        {
            "soft" => "--soft",
            "mixed" => "--mixed",
            "hard" => "--hard",
            _ => null
        };
        if (resetMode is null)
            return new GitOperationResult(false, _localization.T("Invalid reset mode."), string.Empty);

        var commitResult = await RunGitAsync(path, "rev-parse", "--verify", "--quiet", $"{commitHash}^{{commit}}");
        if (!commitResult.Success)
            return new GitOperationResult(false, _localization.T("The selected commit is no longer available."), string.Empty);

        return await RunGitAsync(path, "reset", resetMode, commitResult.Output.Trim());
    }

    public async Task<GitOperationResult> CreateAndCheckoutBranchAsync(string path, string branchName, string startPoint, bool autoSyncSubmodules = false)
    {
        branchName = branchName.Trim();
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, _localization.T("Enter a branch name."), string.Empty);

        var branchNameResult = await RunGitAsync(path, "check-ref-format", $"refs/heads/{branchName}");
        if (!branchNameResult.Success)
            return new GitOperationResult(false, _localization.T("Invalid branch name: {0}", branchName), string.Empty);

        var startPointResult = await RunGitAsync(path, "rev-parse", "--verify", "--quiet", $"{startPoint}^{{commit}}");
        if (!startPointResult.Success)
            return new GitOperationResult(false, _localization.T("The selected starting point is no longer available."), string.Empty);

        if (autoSyncSubmodules)
        {
            var safety = await CheckSubmoduleTreesCleanAsync(path);
            if (!safety.Success) return safety;
        }
        var checkout = await RunGitAsync(path, "-c", "submodule.recurse=false", "switch", "--create", branchName, startPointResult.Output.Trim());
        if (!checkout.Success || !autoSyncSubmodules) return checkout;
        var postCheckoutSafety = await CheckSubmoduleTreesCleanAsync(path);
        if (!postCheckoutSafety.Success)
            return new GitOperationResult(false, _localization.T("Branch switched, but submodules were not updated to preserve local changes: {0}", postCheckoutSafety.Message), checkout.Output);
        var update = await UpdateSubmodulesWithoutPreflightAsync(path);
        return update.Success ? checkout : new GitOperationResult(false, _localization.T("Branch switched, but submodules were not fully updated: {0}", update.Message), update.Output, update.ExitCode);
    }

    public async Task<IReadOnlyList<string>> GetLocalBranchNamesAsync(string path)
    {
        var result = await RunGitAsync(path, "for-each-ref", "--format=%(refname:short)", "refs/heads");
        return result.Success
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.TrimEnd('\r')).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    public async Task<string?> GetDevelopmentStartPointAsync(string path)
    {
        foreach (var reference in new[] { "refs/heads/development", "refs/remotes/origin/development" })
        {
            var result = await RunGitAllowExitOneAsync(path, "show-ref", "--verify", "--quiet", reference);
            if (result.Success)
                return reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? "development" : "origin/development";
        }
        return null;
    }

    public async Task<GitBranchMergeCheck> CheckBranchCanMergeAsync(string path, string sourceBranch)
    {
        var sourceResult = await RunGitAllowExitOneAsync(path, "show-ref", "--verify", "--quiet", $"refs/heads/{sourceBranch}");
        if (!sourceResult.Success)
            return new GitBranchMergeCheck(false, _localization.T("Local branch '{0}' is no longer available.", sourceBranch));

        var fetchResult = await FetchAsync(path);
        if (!fetchResult.Success)
            return new GitBranchMergeCheck(false, _localization.T("Merge aborted: failed to fetch remote status. {0}", fetchResult.Message));

        var currentResult = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!currentResult.Success)
            return new GitBranchMergeCheck(false, _localization.T("Cannot merge while HEAD is detached."));

        var currentBranch = currentResult.Output.Trim();
        if (string.Equals(currentBranch, sourceBranch, StringComparison.Ordinal))
            return new GitBranchMergeCheck(false, _localization.T("Cannot merge the checked-out branch into itself."));

        var upstreamResult = await RunGitAsync(path, "for-each-ref", "--format=%(upstream)", $"refs/heads/{sourceBranch}");
        if (!upstreamResult.Success)
            return new GitBranchMergeCheck(false, upstreamResult.Message);

        var upstream = upstreamResult.Output.Trim();
        if (string.IsNullOrWhiteSpace(upstream))
            return new GitBranchMergeCheck(false, _localization.T("Branch '{0}' has no remote tracking branch.", sourceBranch));

        var countsResult = await RunGitAsync(path, "rev-list", "--left-right", "--count", $"refs/heads/{sourceBranch}...{upstream}");
        if (!countsResult.Success)
            return new GitBranchMergeCheck(false, _localization.T("Unable to check whether branch '{0}' is up to date: {1}", sourceBranch, countsResult.Message));

        var counts = countsResult.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length != 2 || !int.TryParse(counts[1], out var behind))
            return new GitBranchMergeCheck(false, _localization.T("Unable to check whether branch '{0}' is up to date.", sourceBranch));

        return behind > 0
            ? new GitBranchMergeCheck(false, _localization.T("Branch '{0}' is {1} commit(s) behind its remote. Update it before merging.", sourceBranch, behind))
            : new GitBranchMergeCheck(true, null);
    }

    public async Task<GitOperationResult> MergeBranchIntoCurrentAsync(string path, string sourceBranch, string expectedTargetBranch, bool autoSyncSubmodules = false)
    {
        if (autoSyncSubmodules)
        {
            var submoduleSafety = await CheckSubmoduleTreesCleanAsync(path);
            if (!submoduleSafety.Success)
                return submoduleSafety;
        }
        var check = await CheckBranchCanMergeAsync(path, sourceBranch);
        if (!check.CanMerge)
            return new GitOperationResult(false, check.Error ?? _localization.T("Branch status unavailable."), string.Empty);

        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!statusResult.Success)
            return new GitOperationResult(false, _localization.T("Unable to check the working tree before merging: {0}", statusResult.Message), string.Empty);
        if (!string.IsNullOrWhiteSpace(statusResult.Output))
            return new GitOperationResult(false, _localization.T("Commit or stash your working-tree changes before merging."), string.Empty);

        var currentResult = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!currentResult.Success || !string.Equals(currentResult.Output.Trim(), expectedTargetBranch, StringComparison.Ordinal))
            return new GitOperationResult(false, _localization.T("The checked-out branch changed. Refresh and try again."), string.Empty);

        var merge = await RunGitAsync(path, "-c", "submodule.recurse=false", "merge", "--no-edit", $"refs/heads/{sourceBranch}");
        if (!merge.Success || !autoSyncSubmodules) return merge;
        var postMergeSafety = await CheckSubmoduleTreesCleanAsync(path);
        if (!postMergeSafety.Success)
            return new GitOperationResult(false, _localization.T("Merge completed, but submodules were not updated to preserve local changes: {0}", postMergeSafety.Message), merge.Output);
        var update = await UpdateSubmodulesWithoutPreflightAsync(path);
        return update.Success ? merge : new GitOperationResult(false, _localization.T("Merge completed, but submodules were not fully updated: {0}", update.Message), update.Output, update.ExitCode);
    }

    public async Task<GitOperationResult> StageFileAsync(string path, string filePath)
    {
        var unmerged = await RunGitAsync(path, "--literal-pathspecs", "ls-files", "-u", "--", filePath);
        if (!unmerged.Success)
            return unmerged;
        if (!string.IsNullOrWhiteSpace(unmerged.Output))
            return new GitOperationResult(false, _localization.T("Resolve the conflict before staging this file."), string.Empty);

        return await RunGitAsync(path, "add", "--", filePath);
    }

    public async Task<GitOperationResult> StageAllChangesAsync(string path)
    {
        var status = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!status.Success)
            return status;

        var files = ParseFileChanges(status.Output);
        var failed = new List<string>();
        foreach (var file in files.Where(file => (file.IsUnstaged || file.IsUntracked) && !file.IsConflicted))
        {
            var result = await StageFileAsync(path, file.Path);
            if (!result.Success)
                failed.Add(file.Path);
        }

        if (files.Any(file => file.IsConflicted))
            return new GitOperationResult(false, _localization.T("Resolve conflicts before staging all changes."), string.Empty);
        if (failed.Count > 0)
            return new GitOperationResult(false, _localization.T("Stage failed for {0} file(s).", failed.Count), string.Join(Environment.NewLine, failed));

        return new GitOperationResult(true, string.Empty, string.Empty);
    }

    public async Task<GitOperationResult> UnstageFileAsync(string path, string filePath) =>
        await RunGitAsync(path, "restore", "--staged", "--", filePath);

    public async Task<GitOperationResult> DiscardUnstagedFileAsync(string path, GitFileChange file)
    {
        if (file.IsConflicted)
            return await RunGitAsync(path, "--literal-pathspecs", "restore", "--source=HEAD", "--staged", "--worktree", "--", file.Path);

        return file.IsUntracked
            ? await RunGitAsync(path, "--literal-pathspecs", "clean", "-f", "--", file.Path)
            : await RunGitAsync(path, "--literal-pathspecs", "restore", "--worktree", "--", file.Path);
    }

    public async Task<GitOperationResult> CommitAsync(string path, string message) =>
        await RunGitAsync(path, "commit", "-m", message);

    private static IReadOnlyList<GitCommitEntry> AssignGraphLayout(
        IReadOnlyList<GitCommitEntry> commits,
        IReadOnlyList<GitBranchEntry> branches,
        string? defaultBranch)
    {
        var colors = new[] { 0xB38CFF, 0x55A6FF, 0xFFB454, 0x42D6B5, 0xFF6B81, 0xD6D95A, 0x53C7D9, 0xF080D0 };
        var branchColors = branches
            .OrderByDescending(branch => IsDefaultBranch(branch.Name, defaultBranch))
            .ThenByDescending(branch => branch.IsCurrent)
            .ThenBy(branch => branch.IsRemote)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .Select((branch, index) => (branch.TipHash, Color: index % colors.Length))
            .GroupBy(item => item.TipHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Color, StringComparer.Ordinal);
        var branchesByTip = branches
            .GroupBy(branch => branch.TipHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var visibleCommitHashes = commits.Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);

        // Keep lane indices stable while a branch is active. Removing a lane from a
        // compact list shifts every lane to its right and creates misleading bends.
        var lanes = new List<GraphLane?>();
        var laidOut = new List<GitCommitEntry>(commits.Count);
        var baseColor = branchColors.Values.Distinct().Count() <= 1
            ? branchColors.Values.FirstOrDefault(0)
            : 0;
        var nextColor = branchColors.Values.DefaultIfEmpty(-1).Max() + 1;

        foreach (var commit in commits)
        {
            var column = FindLaneColumn(lanes, commit.Hash);
            var laneWasActive = column >= 0;
            if (column < 0)
            {
                column = FindFreeLaneColumn(lanes, 0);
                var color = branchColors.TryGetValue(commit.Hash, out var branchColor)
                    ? branchColor
                    : branchColors.Count <= 1 ? baseColor : nextColor++ % colors.Length;
                SetLane(lanes, column, new GraphLane(commit.Hash, SelectLaneColor(color, column, lanes, colors.Length)));
            }
            var lanesBefore = lanes.ToArray();
            var currentLane = lanes[column]!;
            lanes[column] = null;
            var parentColors = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var parentIndex = 0; parentIndex < commit.Parents.Count; parentIndex++)
            {
                var parentHash = commit.Parents[parentIndex];
                if (!visibleCommitHashes.Contains(parentHash))
                    continue;

                var existingParentColumn = FindLaneColumn(lanes, parentHash);
                if (existingParentColumn >= 0)
                {
                    parentColors[parentHash] = lanes[existingParentColumn]!.Color;
                    continue;
                }

                // The first parent continues the current lane. Side parents use a free
                // lane to the right where possible, without moving any active path.
                var parentColumn = parentIndex == 0 ? column : FindFreeLaneColumn(lanes, column + 1);
                // The first parent continues this lane even when it is also a branch tip.
                // Coloring it from the ref would make the new branch appear to own all
                // shared history below its tip.
                var color = parentIndex == 0
                    ? currentLane.Color
                    : branchColors.TryGetValue(parentHash, out var parentColor)
                        ? parentColor
                        : branchColors.Count <= 1 ? baseColor : nextColor++ % colors.Length;
                var laneColor = SelectLaneColor(color, parentColumn, lanes, colors.Length);
                SetLane(lanes, parentColumn, new GraphLane(parentHash, laneColor));
                parentColors[parentHash] = laneColor;
            }

            var edges = new List<GitGraphEdge>();
            for (var oldColumn = 0; oldColumn < lanesBefore.Length; oldColumn++)
            {
                var lane = lanesBefore[oldColumn];
                if (lane?.Hash == commit.Hash)
                {
                    // Existing lanes enter from the previous row and stop just before
                    // the commit node. A newly-started lane begins at its own node.
                    if (laneWasActive)
                        edges.Add(new GitGraphEdge(oldColumn, 0, oldColumn, 16, lane.Color, commit.Hash));
                    for (var parentIndex = 0; parentIndex < commit.Parents.Count; parentIndex++)
                    {
                        var parentHash = commit.Parents[parentIndex];
                        if (!visibleCommitHashes.Contains(parentHash))
                            continue;

                        var parentColumn = FindLaneColumn(lanes, parentHash);
                        if (parentColumn >= 0)
                        {
                            // The first parent continues the current branch; merge-side parents keep
                            // the color of their own branch lane into the merge.
                            var edgeColor = parentIndex == 0 ? lane.Color : parentColors[parentHash];
                            edges.Add(new GitGraphEdge(oldColumn, 36, parentColumn, 52, edgeColor, parentHash));
                        }
                    }
                }
                else if (lane is not null)
                {
                    var nextColumn = FindLaneColumn(lanes, lane.Hash);
                    if (nextColumn >= 0 && visibleCommitHashes.Contains(lane.Hash))
                        edges.Add(new GitGraphEdge(oldColumn, 0, nextColumn, 52, lane.Color, lane.Hash));
                }
            }

            while (lanes.Count > 0 && lanes[^1] is null)
                lanes.RemoveAt(lanes.Count - 1);

            laidOut.Add(commit with
            {
                GraphColumn = column,
                GraphColor = currentLane.Color,
                GraphEdges = edges,
                GraphWidth = Math.Max(lanesBefore.Length, lanes.Count),
                BranchLabels = branchesByTip.TryGetValue(commit.Hash, out var tipBranches)
                    ? tipBranches
                    .GroupBy(branch => branch.IsRemote ? GetLocalBranchName(branch.Name) : branch.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new GitCommitBranchLabel(
                        group.Key,
                        group.Any(branch => !branch.IsRemote),
                        group.Any(branch => branch.IsRemote),
                        branchColors.TryGetValue(group.First().TipHash, out var labelColor) ? labelColor : currentLane.Color,
                        group.First().TipHash))
                    .ToList()
                    : []
            });
        }

        return laidOut;
    }

    private static int FindLaneColumn(IReadOnlyList<GraphLane?> lanes, string hash)
    {
        for (var index = 0; index < lanes.Count; index++)
        {
            if (lanes[index]?.Hash == hash)
                return index;
        }

        return -1;
    }

    private static int FindFreeLaneColumn(IReadOnlyList<GraphLane?> lanes, int preferredColumn)
    {
        var start = Math.Max(0, preferredColumn);
        for (var index = start; index < lanes.Count; index++)
        {
            if (lanes[index] is null)
                return index;
        }

        for (var index = Math.Min(start - 1, lanes.Count - 1); index >= 0; index--)
        {
            if (lanes[index] is null)
                return index;
        }

        return lanes.Count;
    }

    private static void SetLane(List<GraphLane?> lanes, int column, GraphLane lane)
    {
        while (lanes.Count <= column)
            lanes.Add(null);

        lanes[column] = lane;
    }

    private static int SelectLaneColor(int preferredColor, int laneIndex, IReadOnlyList<GraphLane?> lanes, int colorCount)
    {
        var leftColor = laneIndex > 0 ? lanes[laneIndex - 1]?.Color ?? -1 : -1;
        var rightColor = laneIndex < lanes.Count ? lanes[laneIndex]?.Color ?? -1 : -1;
        var preferred = (preferredColor % colorCount + colorCount) % colorCount;
        if (preferred != leftColor && preferred != rightColor)
            return preferred;

        for (var offset = 1; offset < colorCount; offset++)
        {
            var candidate = (preferred + offset) % colorCount;
            if (candidate != leftColor && candidate != rightColor)
                return candidate;
        }

        return preferred;
    }

    private static bool IsDefaultBranch(string branchName, string? defaultBranch)
    {
        if (!string.IsNullOrWhiteSpace(defaultBranch) &&
            (branchName.Equals(defaultBranch, StringComparison.OrdinalIgnoreCase) ||
             GetLocalBranchName(branchName).Equals(GetLocalBranchName(defaultBranch), StringComparison.OrdinalIgnoreCase)))
            return true;

        return string.IsNullOrWhiteSpace(defaultBranch) &&
               (branchName.Equals("main", StringComparison.OrdinalIgnoreCase) ||
                branchName.Equals("master", StringComparison.OrdinalIgnoreCase) ||
                branchName.Equals("origin/main", StringComparison.OrdinalIgnoreCase) ||
                branchName.Equals("origin/master", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetLocalBranchName(string remoteBranchName)
    {
        var separator = remoteBranchName.IndexOf('/');
        return separator >= 0 && separator < remoteBranchName.Length - 1
            ? remoteBranchName[(separator + 1)..]
            : remoteBranchName;
    }

    private sealed record GraphLane(string Hash, int Color);

    public async Task<GitOperationResult> FetchAsync(string path) => await RunGitAsync(path, "fetch", "--all", "--prune");

    public async Task<GitMaintenanceSnapshot?> GetMaintenanceSnapshotAsync(string path)
    {
        var versionResult = await RunGitAsync(path, "--version");
        if (!versionResult.Success)
            return null;

        var countsResult = await RunGitAsync(path, "count-objects", "-v");
        if (!countsResult.Success)
            return null;

        var fsMonitorResult = await RunGitAllowExitOneAsync(path, "config", "--bool", "--local", "--get", "core.fsmonitor");
        var untrackedCacheResult = await RunGitAllowExitOneAsync(path, "config", "--bool", "--local", "--get", "core.untrackedCache");
        var gitVersion = versionResult.Output.Trim();

        var counts = countsResult.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.Ordinal);

        static long ReadCount(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && long.TryParse(value, out var count) ? count : 0;

        var mergeTarget = await GetMaintenanceMergeTargetAsync(path);
        var branchResult = await GetOldMergedBranchesAsync(path, mergeTarget);
        return new GitMaintenanceSnapshot(
            gitVersion,
            ReadCount(counts, "count"),
            ReadCount(counts, "size"),
            ReadCount(counts, "packs"),
            ReadCount(counts, "size-pack"),
            SupportsBuiltInFileSystemMonitor(gitVersion),
            fsMonitorResult.Success && fsMonitorResult.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            untrackedCacheResult.Success && untrackedCacheResult.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            mergeTarget?.BranchName,
            branchResult);
    }

    public Task<GitOperationResult> RunMaintenanceAsync(string path) =>
        RunGitAsync(path, "maintenance", "run");

    public Task<GitOperationResult> PruneRemotesAsync(string path) =>
        RunGitAsync(path, "fetch", "--all", "--prune");

    public async Task<GitOperationResult> SetStatusOptimizationAsync(string path, bool fileSystemMonitor, bool enabled)
    {
        var configKey = fileSystemMonitor ? "core.fsmonitor" : "core.untrackedCache";
        return await RunGitAsync(path, "config", "--local", configKey, enabled ? "true" : "false");
    }

    public async Task<GitOperationResult> DeleteMergedBranchAsync(string path, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || branchName.StartsWith('-') ||
            await IsProtectedOrCurrentBranchAsync(path, branchName))
            return new GitOperationResult(false, _localization.T("This branch is protected and cannot be removed."), string.Empty);

        var mergeTarget = await GetMaintenanceMergeTargetAsync(path);
        if (mergeTarget is null)
            return new GitOperationResult(false, _localization.T("The default branch could not be detected; the branch was not removed."), string.Empty);

        var mergedBranches = await RunGitAsync(path, "branch", "--merged", mergeTarget.Value.Reference, "--format=%(refname:short)");
        if (!mergedBranches.Success || !mergedBranches.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(branch => branch.TrimEnd('\r').TrimStart('*', ' '))
                .Contains(branchName, StringComparer.Ordinal))
            return new GitOperationResult(false, _localization.T("Branch is no longer merged into the default branch; it was not removed."), string.Empty);

        return await RunGitAsync(path, "branch", "-d", "--", branchName);
    }

    private async Task<IReadOnlyList<GitMaintenanceBranch>> GetOldMergedBranchesAsync(
        string path,
        (string Reference, string BranchName)? mergeTarget)
    {
        const int oldBranchDays = 90;
        var currentResult = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", "HEAD");
        var currentBranch = currentResult.Success ? currentResult.Output.Trim() : null;
        if (mergeTarget is null)
            return [];

        var branchesResult = await RunGitAsync(path, "for-each-ref", "--format=%(refname:short)%09%(committerdate:unix)", "refs/heads");
        var mergedResult = await RunGitAsync(path, "branch", "--merged", mergeTarget.Value.Reference, "--format=%(refname:short)");
        if (!branchesResult.Success || !mergedResult.Success)
            return [];

        var merged = mergedResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(branch => branch.TrimEnd('\r').TrimStart('*', ' '))
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return branchesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length == 2 && long.TryParse(parts[1], out _))
            .Select(parts => (Name: parts[0], CommitTime: long.Parse(parts[1])))
            .Where(branch => merged.Contains(branch.Name) &&
                !string.Equals(branch.Name, currentBranch, StringComparison.Ordinal) &&
                !IsProtectedBranchName(branch.Name, mergeTarget.Value.BranchName) &&
                now - branch.CommitTime >= oldBranchDays * 24L * 60 * 60)
            .Select(branch => new GitMaintenanceBranch(branch.Name,
                (int)((now - branch.CommitTime) / (24L * 60 * 60))))
            .OrderByDescending(branch => branch.AgeDays)
            .ToList();
    }

    private async Task<(string Reference, string BranchName)?> GetMaintenanceMergeTargetAsync(string path)
    {
        var defaultResult = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        var configuredDefault = defaultResult.Success ? defaultResult.Output.Trim() : null;
        if (!string.IsNullOrWhiteSpace(configuredDefault))
        {
            var separator = configuredDefault.IndexOf('/');
            var branchName = separator >= 0 ? configuredDefault[(separator + 1)..] : configuredDefault;
            var remoteReference = $"refs/remotes/{configuredDefault}";
            if (await RefExistsAsync(path, remoteReference))
                return (remoteReference, branchName);

            var localReference = $"refs/heads/{branchName}";
            if (await RefExistsAsync(path, localReference))
                return (localReference, branchName);
        }

        var localBranches = await RunGitAsync(path, "for-each-ref", "--format=%(refname:short)", "refs/heads");
        var localBranchNames = localBranches.Success
            ? localBranches.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(branch => branch.TrimEnd('\r')).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteBranches = await RunGitAsync(path, "for-each-ref", "--format=%(refname:short)", "refs/remotes/origin");
        var remoteBranchNames = remoteBranches.Success
            ? remoteBranches.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(branch => branch.TrimEnd('\r')).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[] { "main", "master", "develop", "development" })
        {
            var localReference = $"refs/heads/{candidate}";
            if (localBranchNames.Contains(candidate))
                return (localReference, candidate);

            var remoteReference = $"refs/remotes/origin/{candidate}";
            if (remoteBranchNames.Contains($"origin/{candidate}"))
                return (remoteReference, candidate);
        }

        return null;
    }

    private async Task<bool> IsProtectedOrCurrentBranchAsync(string path, string branchName)
    {
        var currentResult = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", "HEAD");
        var mergeTarget = await GetMaintenanceMergeTargetAsync(path);

        return string.Equals(branchName, currentResult.Output.Trim(), StringComparison.Ordinal) ||
               IsProtectedBranchName(branchName, mergeTarget?.BranchName);
    }

    private static bool IsProtectedBranchName(string branchName, string? defaultBranch) =>
        string.Equals(branchName, GetLocalBranchName(defaultBranch ?? string.Empty), StringComparison.OrdinalIgnoreCase) ||
        branchName.Equals("main", StringComparison.OrdinalIgnoreCase) ||
        branchName.Equals("master", StringComparison.OrdinalIgnoreCase) ||
        branchName.Equals("develop", StringComparison.OrdinalIgnoreCase) ||
        branchName.StartsWith("release/", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsBuiltInFileSystemMonitor(string gitVersion)
    {
        var versionText = gitVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var versionParts = versionText.Split('-', 2)[0].Split('.');
        return versionParts.Length >= 2 && int.TryParse(versionParts[0], out var major) &&
               int.TryParse(versionParts[1], out var minor) && (major > 2 || major == 2 && minor >= 37);
    }

    public async Task<GitOperationResult> PullAsync(string path, bool autoSyncSubmodules = false)
    {
        if (autoSyncSubmodules)
        {
            var beforePull = await CheckSubmoduleTreesCleanAsync(path);
            if (!beforePull.Success) return beforePull;
        }
        var pull = await RunGitAsync(path, "-c", "submodule.recurse=false", "pull", "--no-rebase");
        if (!pull.Success || !autoSyncSubmodules) return pull;
        var postPullSafety = await CheckSubmoduleTreesCleanAsync(path);
        if (!postPullSafety.Success)
            return new GitOperationResult(false, _localization.T("Pull completed, but submodules were not updated to preserve local changes: {0}", postPullSafety.Message), pull.Output);
        var update = await UpdateSubmodulesWithoutPreflightAsync(path);
        return update.Success ? pull : new GitOperationResult(false, _localization.T("Pull completed, but submodules were not fully updated: {0}", update.Message), update.Output, update.ExitCode);
    }

    public async Task<GitOperationResult> PushAsync(string path) => await RunGitAsync(path, "push");

    public async Task<GitOperationResult> PushWhenUpToDateAsync(string path, bool autoSyncSubmodules = false)
    {
        if (autoSyncSubmodules)
        {
            var beforePush = await CheckSubmoduleTreesCleanAsync(path);
            if (!beforePush.Success) return beforePush;
        }
        var fetchResult = await FetchAsync(path);
        if (!fetchResult.Success)
            return new GitOperationResult(false, _localization.T("Push aborted: failed to fetch remote status. {0}", fetchResult.Message), string.Empty);

        var currentResult = await RunGitAsync(path, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!currentResult.Success)
            return new GitOperationResult(false, _localization.T("Push aborted: cannot push while HEAD is detached."), string.Empty);

        var branch = currentResult.Output.Trim();
        var upstreamResult = await RunGitAsync(path, "for-each-ref", "--format=%(upstream)", $"refs/heads/{branch}");
        if (!upstreamResult.Success)
            return new GitOperationResult(false, _localization.T("Push aborted: failed to check remote status. {0}", upstreamResult.Message), string.Empty);

        var upstream = upstreamResult.Output.Trim();
        var hasUpstream = !string.IsNullOrWhiteSpace(upstream);
        string? remote = null;
        string? remoteBranch = null;

        if (hasUpstream)
        {
            var split = upstream.Split('/', 3);
            if (split.Length < 3 || !split[0].Equals("refs", StringComparison.Ordinal) || !split[1].Equals("remotes", StringComparison.Ordinal))
                return new GitOperationResult(false, _localization.T("Push aborted: unsupported upstream '{0}'.", upstream), string.Empty);
            remote = split[2].Split('/', 2)[0];
            remoteBranch = split[2][(remote.Length + 1)..];
        }
        else
        {
            var remoteResult = await GetDefaultPushRemoteAsync(path, branch);
            if (!remoteResult.Success)
                return remoteResult;
            remote = remoteResult.Output;
            remoteBranch = branch;
        }

        var remoteRef = $"refs/remotes/{remote}/{remoteBranch}";
        var remoteBranchExistsResult = await RunGitAllowExitOneAsync(path, "show-ref", "--verify", "--quiet", remoteRef);
        var remoteBranchExists = remoteBranchExistsResult.Success;
        if (!remoteBranchExists && remoteBranchExistsResult.ExitCode != 1)
            return new GitOperationResult(false, _localization.T("Push aborted: failed to check remote branch. {0}", remoteBranchExistsResult.Message), string.Empty);

        if (remoteBranchExists)
        {
            var countsResult = await RunGitAsync(path, "rev-list", "--left-right", "--count", $"refs/heads/{branch}...{remoteRef}");
            if (!countsResult.Success)
                return new GitOperationResult(false, _localization.T("Push aborted: failed to check remote status. {0}", countsResult.Message), string.Empty);

            var counts = countsResult.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length != 2 || !int.TryParse(counts[1], out var behind))
                return new GitOperationResult(false, _localization.T("Push aborted: failed to parse remote status."), string.Empty);

            if (behind > 0)
            {
                var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
                if (!statusResult.Success)
                    return new GitOperationResult(false, _localization.T("Push aborted: unable to check working tree. {0}", statusResult.Message), string.Empty);
                if (!string.IsNullOrWhiteSpace(statusResult.Output))
                    return new GitOperationResult(false, _localization.T("Push aborted: commit or stash your changes before pulling remote updates."), string.Empty);

                var pullResult = hasUpstream
                    ? await PullAsync(path, autoSyncSubmodules)
                    : await RunGitAsync(path, "-c", "submodule.recurse=false", "pull", "--no-rebase", remote!, remoteBranch!);
                if (!pullResult.Success)
                    return new GitOperationResult(false, _localization.T("Push aborted: pull failed. {0}", pullResult.Message), pullResult.Output, pullResult.ExitCode);
                if (autoSyncSubmodules)
                {
                    var postPullSafety = await CheckSubmoduleTreesCleanAsync(path);
                    if (!postPullSafety.Success)
                        return new GitOperationResult(false, _localization.T("Pull completed, but submodules were not updated to preserve local changes: {0}", postPullSafety.Message), pullResult.Output);
                    var submoduleUpdate = await UpdateSubmodulesWithoutPreflightAsync(path);
                    if (!submoduleUpdate.Success)
                        return new GitOperationResult(false, _localization.T("Pull completed, but submodules were not fully updated: {0}", submoduleUpdate.Message), submoduleUpdate.Output, submoduleUpdate.ExitCode);
                }
            }
        }

        return hasUpstream
            ? await PushAsync(path)
            : await RunGitAsync(path, "push", "--set-upstream", remote!, branch);
    }

    private async Task<GitOperationResult> GetDefaultPushRemoteAsync(string path, string branch)
    {
        var configuredRemote = await RunGitAllowExitOneAsync(path, "config", "--get", "remote.pushDefault");
        if (configuredRemote.Success && !string.IsNullOrWhiteSpace(configuredRemote.Output))
            return new GitOperationResult(true, string.Empty, configuredRemote.Output.Trim());

        var branchRemote = await RunGitAllowExitOneAsync(path, "config", "--get", $"branch.{branch}.remote");
        if (branchRemote.Success && !string.IsNullOrWhiteSpace(branchRemote.Output) && branchRemote.Output.Trim() != ".")
            return new GitOperationResult(true, string.Empty, branchRemote.Output.Trim());

        var remotesResult = await RunGitAsync(path, "remote");
        if (!remotesResult.Success)
            return new GitOperationResult(false, _localization.T("Push aborted: unable to list remotes. {0}", remotesResult.Message), string.Empty);

        var remotes = remotesResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(remoteName => remoteName.Trim())
            .Where(remoteName => remoteName.Length > 0)
            .ToArray();
        if (remotes.Contains("origin", StringComparer.Ordinal))
            return new GitOperationResult(true, string.Empty, "origin");
        if (remotes.Length == 1)
            return new GitOperationResult(true, string.Empty, remotes[0]);

        return new GitOperationResult(false, _localization.T("Push aborted: no unambiguous default remote is configured."), string.Empty);
    }

    public async Task<GitOperationResult> CheckoutBranchAsync(string path, string branch, bool autoSyncSubmodules = false)
    {
        if (autoSyncSubmodules)
        {
            var safety = await CheckSubmoduleTreesCleanAsync(path);
            if (!safety.Success)
                return safety;
        }
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            branch = branch[11..];
        else if (branch.StartsWith("refs/remotes/", StringComparison.Ordinal))
            branch = branch[13..];

            var localBranchResult = await RunGitAllowExitOneAsync(path, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}");
        var operation = localBranchResult.Success
            ? await RunGitAsync(path, "-c", "submodule.recurse=false", "switch", branch)
            : await RunGitAsync(path, "-c", "submodule.recurse=false", "switch", "--track", branch);
        if (!operation.Success || !autoSyncSubmodules) return operation;
        var postCheckoutSafety = await CheckSubmoduleTreesCleanAsync(path);
        if (!postCheckoutSafety.Success)
            return new GitOperationResult(false, _localization.T("Branch switched, but submodules were not updated to preserve local changes: {0}", postCheckoutSafety.Message), operation.Output);
        var update = await UpdateSubmodulesWithoutPreflightAsync(path);
        return update.Success ? operation : new GitOperationResult(false, _localization.T("Branch switched, but submodules were not fully updated: {0}", update.Message), update.Output, update.ExitCode);
    }

    private async Task<GitOperationResult> UpdateSubmodulesWithoutPreflightAsync(string path) =>
        await RunGitAsync(path, "-c", "submodule.recurse=false", "submodule", "update", "--checkout", "--init", "--recursive");

    public async Task<GitSubmoduleSnapshot> GetSubmoduleSnapshotAsync(string repositoryPath)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var concurrency = new SemaphoreSlim(4);
        var entries = await ReadSubmodulesRecursiveAsync(repositoryPath, string.Empty, concurrency);
        _logger.LogInformation("Loaded {SubmoduleCount} submodules in {ElapsedMilliseconds} ms", entries.Count,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return new GitSubmoduleSnapshot(entries);
    }

    public async Task<GitOperationResult> SyncSubmoduleUrlsAsync(string repositoryPath) =>
        await RunGitAsync(repositoryPath, "submodule", "sync", "--recursive");

    public async Task<GitOperationResult> UpdateSubmodulesAsync(string repositoryPath, string? submodulePath = null)
    {
        var safety = await CheckSubmoduleTreesCleanAsync(repositoryPath);
        if (!safety.Success)
            return safety;

        if (!string.IsNullOrWhiteSpace(submodulePath))
        {
            var targetState = await GetSubmoduleSnapshotAsync(repositoryPath);
            var target = targetState.Entries.FirstOrDefault(entry => string.Equals(entry.Path, submodulePath, StringComparison.Ordinal));
            if (target is null)
                return new GitOperationResult(false, _localization.T("Selected submodule is no longer configured."), string.Empty);
            var selectedUpdate = await RunGitAsync(repositoryPath, "-c", "submodule.recurse=false", "submodule", "update", "--checkout", "--init", "--", submodulePath);
            if (!selectedUpdate.Success) return selectedUpdate;
            var selectedPath = Path.Combine(repositoryPath, submodulePath.Replace('/', Path.DirectorySeparatorChar));
            var nestedUpdate = await RunGitAsync(selectedPath,
                "-c", "submodule.recurse=false", "submodule", "update", "--checkout", "--init", "--recursive");
            if (!nestedUpdate.Success) return nestedUpdate;
            var nestedSafety = await CheckSubmoduleTreesCleanAsync(repositoryPath);
            return nestedSafety.Success ? selectedUpdate : new GitOperationResult(false,
                _localization.T("Submodule synchronization was blocked to preserve local changes: {0}", nestedSafety.Message), nestedUpdate.Output, nestedUpdate.ExitCode);
        }

        var update = string.IsNullOrWhiteSpace(submodulePath)
            ? await RunGitAsync(repositoryPath, "-c", "submodule.recurse=false", "submodule", "update", "--checkout", "--init", "--recursive")
            : await RunGitAsync(repositoryPath, "-c", "submodule.recurse=false", "submodule", "update", "--checkout", "--init", "--recursive", "--", submodulePath);
        if (!update.Success) return update;
        var postUpdateSafety = await CheckSubmoduleTreesCleanAsync(repositoryPath);
        return postUpdateSafety.Success
            ? update
            : new GitOperationResult(false, _localization.T("Submodule synchronization was blocked to preserve local changes: {0}", postUpdateSafety.Message), update.Output, update.ExitCode);
    }

    public async Task<GitOperationResult> SyncAndUpdateSubmodulesAsync(string repositoryPath, string? submodulePath = null)
    {
        var safety = await CheckSubmoduleTreesCleanAsync(repositoryPath);
        if (!safety.Success) return safety;
        var sync = await SyncSubmoduleUrlsAsync(repositoryPath);
        if (!sync.Success)
            return sync;
        return await UpdateSubmodulesAsync(repositoryPath, submodulePath);
    }

    public async Task<GitOperationResult> CheckSubmoduleTreesCleanAsync(string repositoryPath)
    {
        var dirty = new List<string>();
        var conflicts = new List<string>();
        await InspectSubmoduleTreeAsync(repositoryPath, string.Empty, dirty, conflicts);
        if (conflicts.Count > 0)
            return new GitOperationResult(false, _localization.T("Submodule synchronization was blocked by conflicts: {0}", string.Join(", ", conflicts)), string.Empty);
        if (dirty.Count > 0)
            return new GitOperationResult(false, _localization.T("Submodule synchronization was blocked to preserve local changes: {0}", string.Join(", ", dirty)), string.Empty);
        return new GitOperationResult(true, string.Empty, string.Empty);
    }

    private async Task<List<GitSubmoduleEntry>> ReadSubmodulesRecursiveAsync(string repositoryPath, string prefix, SemaphoreSlim concurrency)
    {
        GitOperationResult status;
        GitOperationResult? configuredPaths = null;
        await concurrency.WaitAsync();
        try
        {
            status = await RunGitAsync(repositoryPath, "submodule", "status");
            if (status.Success && !string.IsNullOrWhiteSpace(status.Output))
                configuredPaths = await RunGitAllowExitOneAsync(repositoryPath, "config", "--file", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$");
        }
        finally
        {
            concurrency.Release();
        }

        if (!status.Success)
            return string.IsNullOrWhiteSpace(status.Message) ? [] :
                [new GitSubmoduleEntry(prefix, prefix, string.Empty, string.Empty, null, null, false, false, false, false, false, status.Message)];
        if (string.IsNullOrWhiteSpace(status.Output))
            return [];

        var namesByPath = configuredPaths?.Success == true
            ? configuredPaths.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(item => item.TrimEnd('\r'))
                .Select(item => item.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[1], parts => parts[0].StartsWith("submodule.", StringComparison.Ordinal) && parts[0].EndsWith(".path", StringComparison.Ordinal)
                    ? parts[0][10..^5] : parts[0], StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var tasks = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => ReadSubmoduleAsync(repositoryPath, prefix, line, namesByPath, concurrency));
        var children = await Task.WhenAll(tasks);
        return children.SelectMany(child => child).ToList();
    }

    private async Task<List<GitSubmoduleEntry>> ReadSubmoduleAsync(
        string repositoryPath, string prefix, string line, IReadOnlyDictionary<string, string> namesByPath, SemaphoreSlim concurrency)
    {
        var trimmed = line.TrimEnd('\r');
        if (trimmed.Length < 42)
            return [];
        var marker = trimmed[0];
        var hash = trimmed.Substring(1, 40);
        var remainder = trimmed[41..].TrimStart();
        var path = ParseSubmoduleStatusPath(remainder, namesByPath.Keys);
        var fullPath = string.IsNullOrEmpty(prefix) ? path : Path.Combine(prefix, path).Replace('\\', '/');
        var absolutePath = Path.Combine(repositoryPath, path.Replace('/', Path.DirectorySeparatorChar));
        bool initialized;
        GitSubmoduleEntry entry;
        await concurrency.WaitAsync();
        try
        {
            var expectedResult = await RunGitAsync(repositoryPath, "rev-parse", $"HEAD:{path}");
            var expected = expectedResult.Success ? expectedResult.Output.Trim() : hash;
            initialized = Directory.Exists(absolutePath) && await IsRepositoryRootAsync(absolutePath);
            string? actual = null;
            string? branch = null;
            var changed = false;
            var untracked = false;
            var conflicted = marker == 'U';
            string? error = marker == 'U' ? _localization.T("Submodule commit conflict") : null;
            if (initialized)
            {
                var actualResult = await RunGitAsync(absolutePath, "rev-parse", "HEAD");
                actual = actualResult.Success ? actualResult.Output.Trim() : null;
                var branchResult = await RunGitAllowExitOneAsync(absolutePath, "symbolic-ref", "--quiet", "--short", "HEAD");
                branch = branchResult.Success && branchResult.ExitCode == 0 ? branchResult.Output.Trim() : null;
                var working = await RunGitAsync(absolutePath, "status", "--porcelain=v1", "-uall", "--ignore-submodules=all");
                if (working.Success)
                {
                    var lines = working.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimEnd('\r')).ToArray();
                    changed = lines.Any(value => value.Length >= 2 && value[..2] != "??");
                    untracked = lines.Any(value => value.StartsWith("??", StringComparison.Ordinal));
                    conflicted |= lines.Any(value => value.Length >= 2 && (value[0] == 'U' || value[1] == 'U' || value.StartsWith("AA", StringComparison.Ordinal) || value.StartsWith("DD", StringComparison.Ordinal)));
                }
                else
                    error = working.Message;
            }
            else if (marker != '-')
                error = _localization.T("Submodule directory is unavailable");

            entry = new GitSubmoduleEntry(fullPath, prefix, namesByPath.GetValueOrDefault(path, Path.GetFileName(path)), expected, actual, branch, initialized,
                string.Equals(expected, actual, StringComparison.Ordinal), changed, untracked, conflicted, error);
        }
        finally
        {
            concurrency.Release();
        }
        var entries = new List<GitSubmoduleEntry> { entry };
        if (initialized)
            entries.AddRange(await ReadSubmodulesRecursiveAsync(absolutePath, fullPath, concurrency));
        return entries;
    }

    private async Task InspectSubmoduleTreeAsync(string repositoryPath, string prefix, List<string> dirty, List<string> conflicts)
    {
        var status = await RunGitAsync(repositoryPath, "submodule", "status");
        if (!status.Success)
            return;
        if (string.IsNullOrWhiteSpace(status.Output))
            return;
        var configured = await RunGitAllowExitOneAsync(repositoryPath, "config", "--file", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$");
        var paths = configured.Success
            ? configured.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(item => item.TrimEnd('\r'))
                .Select(item => item.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 2).Select(parts => parts[1]).ToArray()
            : [];
        foreach (var line in status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimEnd('\r');
            if (value.Length < 42)
                continue;
            var path = ParseSubmoduleStatusPath(value[41..].TrimStart(), paths);
            var displayPath = string.IsNullOrEmpty(prefix) ? path : $"{prefix}/{path}";
            if (value[0] == 'U') conflicts.Add(displayPath);
            var unmergedGitlink = await RunGitAsync(repositoryPath, "ls-files", "-u", "--", path);
            if (!unmergedGitlink.Success || !string.IsNullOrWhiteSpace(unmergedGitlink.Output))
                conflicts.Add(displayPath);
            var stagedGitlink = await RunGitAllowExitOneAsync(repositoryPath, "diff", "--cached", "--quiet", "--", path);
            if (!stagedGitlink.Success)
                conflicts.Add(displayPath);
            else if (stagedGitlink.ExitCode == 1)
                dirty.Add(displayPath);
            var fullPath = Path.Combine(repositoryPath, path.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullPath) || !await IsRepositoryRootAsync(fullPath))
                continue;
            var working = await RunGitAsync(fullPath, "status", "--porcelain=v1", "-uall");
            if (!working.Success) { conflicts.Add(displayPath); continue; }
            if (!string.IsNullOrWhiteSpace(working.Output))
            {
                if (working.Output.Split('\n').Any(item => item.Length >= 2 && (item[0] == 'U' || item[1] == 'U' || item.StartsWith("AA", StringComparison.Ordinal) || item.StartsWith("DD", StringComparison.Ordinal)))) conflicts.Add(displayPath);
                else dirty.Add(displayPath);
            }
            await InspectSubmoduleTreeAsync(fullPath, displayPath, dirty, conflicts);
        }
    }

    private static string ParseSubmoduleStatusPath(string remainder, IEnumerable<string> configuredPaths)
    {
        foreach (var candidate in configuredPaths.OrderByDescending(value => value.Length))
        {
            if (remainder.StartsWith(candidate, StringComparison.Ordinal) &&
                (remainder.Length == candidate.Length || char.IsWhiteSpace(remainder[candidate.Length])))
                return candidate;
        }
        if (remainder.EndsWith(')'))
        {
            var descriptionStart = remainder.LastIndexOf(" (", StringComparison.Ordinal);
            if (descriptionStart > 0)
                return remainder[..descriptionStart];
        }
        return remainder;
    }

    private async Task<bool> IsRepositoryRootAsync(string path)
    {
        var result = await RunGitAsync(path, "rev-parse", "--show-toplevel");
        if (!result.Success) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.Output.Trim())),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<GitOperationResult> OpenConflictInVsCodeAsync(string repositoryPath, string relativePath)
    {
        if (OperatingSystem.IsIOS())
            return new GitOperationResult(false, _localization.T("VS Code could not be started."), string.Empty);

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(repositoryPath, relativePath));
            var root = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return new GitOperationResult(false, _localization.T("Invalid conflict file."), string.Empty);

            var baseContent = await RunGitAsync(repositoryPath, "show", $":1:{relativePath}");
            var oursContent = await RunGitAsync(repositoryPath, "show", $":2:{relativePath}");
            var theirsContent = await RunGitAsync(repositoryPath, "show", $":3:{relativePath}");
            if (!baseContent.Success || !oursContent.Success || !theirsContent.Success)
                return new GitOperationResult(false, _localization.T("Conflict versions could not be read from Git (possibly a new file)."), string.Empty);

            var gitDirectoryResult = await RunGitAsync(repositoryPath, "rev-parse", "--git-path", "codebadger-conflicts");
            if (!gitDirectoryResult.Success)
                return new GitOperationResult(false, _localization.T("Unable to determine the Git directory."), string.Empty);

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
                return new GitOperationResult(false, _localization.T("VS Code could not be started."), string.Empty);
            return new GitOperationResult(true, _localization.T("VS Code merge editor opened."), string.Empty);
        }
        catch (Win32Exception)
        {
            return new GitOperationResult(false, _localization.T("VS Code CLI 'code' is not available in PATH."), string.Empty);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return new GitOperationResult(false, _localization.T("VS Code could not be opened: {0}", exception.Message), string.Empty);
        }
    }

    public async Task<(GitConflictVersions? Versions, GitOperationResult Result)> GetConflictVersionsAsync(string repositoryPath, string relativePath)
    {
        if (!TryGetConflictFilePath(repositoryPath, relativePath, out var fullPath))
            return (null, new GitOperationResult(false, _localization.T("Invalid conflict file."), string.Empty));

        var unmerged = await RunGitAsync(repositoryPath, "ls-files", "-u", "--", relativePath);
        if (!unmerged.Success || string.IsNullOrWhiteSpace(unmerged.Output))
            return (null, new GitOperationResult(false, _localization.T("This file is no longer marked as conflicted."), string.Empty));

        var ours = await RunGitAsync(repositoryPath, "show", $":2:{relativePath}");
        var theirs = await RunGitAsync(repositoryPath, "show", $":3:{relativePath}");
        if (!ours.Success || !theirs.Success)
            return (null, new GitOperationResult(false, _localization.T("Conflict versions could not be read from Git (possibly a new file)."), string.Empty));

        try
        {
            var workingBytes = await File.ReadAllBytesAsync(fullPath);
            var workingContent = Encoding.UTF8.GetString(workingBytes);
            if (ours.Output.Contains('\0') || theirs.Output.Contains('\0') || workingContent.Contains('\0'))
                return (null, new GitOperationResult(false, _localization.T("Binary conflict files cannot be edited here."), string.Empty));

            var fingerprint = Convert.ToHexString(SHA256.HashData(workingBytes));
            var lineEnding = workingContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var hasUtf8Bom = workingBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
            if (hasUtf8Bom && workingContent.Length > 0 && workingContent[0] == '\uFEFF')
                workingContent = workingContent[1..];
            return (new GitConflictVersions(ours.Output, theirs.Output, workingContent, fingerprint, lineEnding, hasUtf8Bom), new GitOperationResult(true, string.Empty, string.Empty));
        }
        catch (IOException exception)
        {
            return (null, new GitOperationResult(false, exception.Message, string.Empty));
        }
    }

    public async Task<GitOperationResult> SaveResolvedConflictAsync(string repositoryPath, string relativePath, string content, string expectedFingerprint, string lineEnding, bool hasUtf8Bom)
    {
        if (!TryGetConflictFilePath(repositoryPath, relativePath, out var fullPath))
            return new GitOperationResult(false, _localization.T("Invalid conflict file."), string.Empty);

        try
        {
            var unmerged = await RunGitAsync(repositoryPath, "ls-files", "-u", "--", relativePath);
            if (!unmerged.Success || string.IsNullOrWhiteSpace(unmerged.Output))
                return new GitOperationResult(false, _localization.T("This file is no longer marked as conflicted."), string.Empty);

            var currentBytes = await File.ReadAllBytesAsync(fullPath);
            var currentFingerprint = Convert.ToHexString(SHA256.HashData(currentBytes));
            if (!string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
                return new GitOperationResult(false, _localization.T("The conflict file changed while the editor was open. Reload it before saving."), string.Empty);

            var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", lineEnding, StringComparison.Ordinal);
            var encodedContent = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(normalizedContent);
            if (hasUtf8Bom)
                encodedContent = Encoding.UTF8.GetPreamble().Concat(encodedContent).ToArray();
            await File.WriteAllBytesAsync(fullPath, encodedContent);
            return await RunGitAsync(repositoryPath, "add", "--", relativePath);
        }
        catch (IOException exception)
        {
            return new GitOperationResult(false, exception.Message, string.Empty);
        }
    }

    private static bool TryGetConflictFilePath(string repositoryPath, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        var root = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath);
    }

    public async Task<GitBranchInfo> GetCurrentBranchAsync(string path)
    {
        var result = await RunGitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        if (result.Success)
            return result.Output.Trim() == "HEAD"
                ? new GitBranchInfo(null, _localization.T("Detached HEAD"))
                : new GitBranchInfo(result.Output.Trim(), null);

        return new GitBranchInfo(null, result.Message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            ? _localization.T("Not a Git repository")
            : result.Message.Contains("Path does not exist", StringComparison.OrdinalIgnoreCase) || result.Message.Contains("Pad bestaat niet", StringComparison.OrdinalIgnoreCase)
                ? _localization.T("Path does not exist") : _localization.T("Branch unavailable"));
    }

    private async Task<GitOperationResult> RunGitAsync(string path, params string[] arguments) =>
        await RunGitCoreAsync(path, false, arguments);

    private async Task<GitOperationResult> RunGitAllowExitOneAsync(string path, params string[] arguments) =>
        await RunGitCoreAsync(path, true, arguments);

    private async Task<GitOperationResult> RunGitCoreAsync(string path, bool allowExitCodeOne, params string[] arguments)
    {
        var command = FormatArguments(arguments);
        using var repositoryScope = _logger.BeginScope(new Dictionary<string, object?> { ["RepositoryPath"] = path });
        if (OperatingSystem.IsIOS())
            return new GitOperationResult(false, _localization.T("Git is not available in PATH."), string.Empty);

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _logger.LogInformation("Running git command in {RepositoryPath}: git {GitArguments}", path, command);
            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitOperationResult(false, _localization.T("Git could not be started."), string.Empty);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var success = process.ExitCode == 0 || (allowExitCodeOne && process.ExitCode == 1);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            if (success)
            {
                _logger.LogInformation("Git command completed with exit code {ExitCode} in {ElapsedMilliseconds} ms: git {GitArguments}. Response: {GitOutput}",
                    process.ExitCode, elapsed.TotalMilliseconds, command, TruncateLogOutput(output));
            }
            else
            {
                var errorOutput = TruncateLogOutput(error);
                if (string.IsNullOrWhiteSpace(errorOutput))
                    _logger.LogWarning("Git command failed with exit code {ExitCode} in {ElapsedMilliseconds} ms: git {GitArguments}. Git did not provide error output.",
                        process.ExitCode, elapsed.TotalMilliseconds, command);
                else
                    _logger.LogWarning("Git command failed with exit code {ExitCode} in {ElapsedMilliseconds} ms: git {GitArguments}. Error: {GitError}",
                        process.ExitCode, elapsed.TotalMilliseconds, command, errorOutput);
            }
            return new GitOperationResult(success, success ? output.Trim() : error.Trim(), output, process.ExitCode);
        }
        catch (Win32Exception exception)
        {
            _logger.LogError(exception, "Could not start git command: git {GitArguments}", command);
            return new GitOperationResult(false, _localization.T("Git is not available in PATH."), string.Empty);
        }
        catch (DirectoryNotFoundException exception)
        {
            _logger.LogWarning(exception, "Git working directory does not exist: {RepositoryPath}", path);
            return new GitOperationResult(false, _localization.T("Path does not exist"), string.Empty);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(exception, "Could not execute git command: git {GitArguments}", command);
            return new GitOperationResult(false, exception.Message, string.Empty);
        }
    }

    private static string FormatArgument(string argument)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(argument,
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        safe = System.Text.RegularExpressions.Regex.Replace(safe,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return safe.Any(char.IsWhiteSpace)
            ? "\"" + safe.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : safe;
    }

    private static string FormatArguments(IReadOnlyList<string> arguments)
    {
        var safeArguments = new List<string>(arguments.Count);
        var redactNext = false;
        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                safeArguments.Add("[redacted]");
                redactNext = false;
                continue;
            }

            safeArguments.Add(FormatArgument(argument));
            redactNext = argument is "-m" or "--message";
        }

        return string.Join(" ", safeArguments);
    }

    private static string TruncateLogOutput(string value)
    {
        const int maximumLength = 2000;
        var trimmed = value.Trim();
        trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed,
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength] + "…";
    }
}

public sealed record GitBranchInfo(string? Branch, string? Status);
public sealed record GitRepositoryStatus(
    GitBranchInfo Branch,
    int ChangedFiles,
    int StagedFiles,
    int UnstagedFiles,
    int Ahead,
    int Behind,
    string? Error)
{
    public bool IsValid => Error is null;

    public static GitRepositoryStatus Invalid(string error) => new(new GitBranchInfo(null, error), 0, 0, 0, 0, 0, error);
}
public sealed record GitSnapshotProgress(string Stage, int? BranchCount = null);
public sealed record GitFileChange(string Path, bool IsStaged, bool IsUnstaged, bool IsConflicted, string Status)
{
    public bool IsUntracked => Status == "??";
}
public sealed record GitStashEntry(string Reference, string Message, string Date, string CommitHash);
public sealed record GitOperationResult(bool Success, string Message, string Output, int ExitCode = 0);
public sealed record GitMaintenanceSnapshot(
    string GitVersion,
    long LooseObjectCount,
    long LooseObjectSizeKb,
    long PackCount,
    long PackedObjectSizeKb,
    bool SupportsBuiltInFileSystemMonitor,
    bool IsFileSystemMonitorEnabled,
    bool IsUntrackedCacheEnabled,
    string? DefaultBranchName,
    IReadOnlyList<GitMaintenanceBranch> OldMergedBranches);
public sealed record GitMaintenanceBranch(string Name, int AgeDays);
public sealed record GitConflictVersions(string Ours, string Theirs, string WorkingContent, string Fingerprint, string LineEnding, bool HasUtf8Bom);
public sealed record GitCommitBranchCheck(bool? IsOnCurrentBranch, string? Error);
public sealed record GitSubmoduleSnapshot(IReadOnlyList<GitSubmoduleEntry> Entries)
{
    public int InSyncCount => Entries.Count(entry => entry.IsInitialized && entry.InSync);
    public int OutOfSyncCount => Entries.Count(entry => entry.IsInitialized && !entry.InSync);
    public int UninitializedCount => Entries.Count(entry => !entry.IsInitialized && entry.Error is null);
    public bool HasErrors => Entries.Any(entry => entry.Error is not null || entry.HasConflicts);
}
public sealed record GitSubmoduleEntry(
    string Path,
    string ParentPath,
    string Name,
    string ExpectedCommit,
    string? CheckedOutCommit,
    string? Branch,
    bool IsInitialized,
    bool InSync,
    bool HasLocalChanges,
    bool HasUntrackedFiles,
    bool HasConflicts,
    string? Error);
public sealed record GitBranchMergeCheck(bool CanMerge, string? Error);
public sealed record GitBranchEntry(string Name, bool IsCurrent, bool IsRemote, string TipHash);
public sealed record GitCommitEntry(
    string Hash,
    string ParentHashes,
    string ShortHash,
    string Subject,
    string Author,
    string Date,
    IReadOnlyList<string> Parents,
    int GraphColumn = 0,
    int GraphColor = 0,
    IReadOnlyList<GitGraphEdge>? GraphEdges = null,
    int GraphWidth = 1,
    IReadOnlyList<GitCommitBranchLabel>? BranchLabels = null);
public sealed record GitGraphEdge(int FromColumn, double FromY, int ToColumn, double ToY, int Color, string PathHash = "");
public sealed record GitCommitBranchLabel(string Name, bool IsLocal, bool IsRemote, int Color, string TipHash = "");
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
