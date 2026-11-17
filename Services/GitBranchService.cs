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
            return GitRepositorySnapshot.Invalid(branchResult.Message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? "Geen Git-repository"
                : branchResult.Message);

        string? warning = null;
        if (fetch)
        {
            var fetchResult = await RunGitAsync(path, "fetch", "--all", "--prune");
            if (!fetchResult.Success)
                warning = $"Ophalen van remotes mislukt: {fetchResult.Message}";
        }

        var statusResult = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        var branchesResult = await RunGitAsync(path, "for-each-ref", "--format=%(refname)%09%(HEAD)%09%(objectname)", "refs/heads", "refs/remotes");
        var historyResult = await RunGitAsync(path, "log", "--all", "--date-order", "-n", "100", "--date=iso-strict", "--format=%H%x1f%P%x1f%h%x1f%s%x1f%an%x1f%ad");

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

        return new GitRepositorySnapshot(
            branchResult.Output.Trim(),
            statusLines.Length,
            statusLines.Count(line => line.Length >= 2 && line[0] != ' ' && line[0] != '?'),
            statusLines.Count(line => line.Length >= 2 && (line[1] != ' ' || line.StartsWith("??", StringComparison.Ordinal))),
            ahead,
            behind,
            branches,
            AssignGraphLayout(commits, branches, defaultBranch),
            conflicts,
            null,
            warning);
    }

    public async Task<IReadOnlyList<GitFileChange>> GetFileChangesAsync(string path)
    {
        var result = await RunGitAsync(path, "status", "--porcelain=v1", "-uall");
        if (!result.Success)
            return [];

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
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
                    indexStatus != ' ' && indexStatus != '?',
                    workTreeStatus != ' ' || line.StartsWith("??", StringComparison.Ordinal),
                    conflicted,
                    line[..2]);
            })
            .ToList();
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

    public async Task<GitOperationResult> StageFileAsync(string path, string filePath) =>
        await RunGitAsync(path, "add", "--", filePath);

    public async Task<GitOperationResult> UnstageFileAsync(string path, string filePath) =>
        await RunGitAsync(path, "restore", "--staged", "--", filePath);

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
        var visibleCommitHashes = commits.Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);

        var lanes = new List<GraphLane>();
        var laidOut = new List<GitCommitEntry>(commits.Count);
        var baseColor = branchColors.Values.Distinct().Count() <= 1
            ? branchColors.Values.FirstOrDefault(0)
            : 0;
        var nextColor = branchColors.Values.DefaultIfEmpty(-1).Max() + 1;

        foreach (var commit in commits)
        {
            var column = lanes.FindIndex(lane => lane.Hash == commit.Hash);
            var laneWasActive = column >= 0;
            if (column < 0)
            {
                // A commit encountered outside the active lanes starts a new lane to
                // their right. Prepending at column zero shifts existing branch lanes
                // and makes unrelated commits appear on the same path.
                column = lanes.Count;
                var color = branchColors.TryGetValue(commit.Hash, out var branchColor)
                    ? branchColor
                    : branchColors.Count <= 1 ? baseColor : nextColor++ % colors.Length;
                lanes.Insert(column, new GraphLane(commit.Hash, SelectLaneColor(color, column, lanes, colors.Length)));
            }
            else if (branchColors.TryGetValue(commit.Hash, out var tipColor))
            {
                // A branch tip must keep the same color as its label, even when its lane
                // was introduced earlier through another commit's parent edge.
                lanes[column] = lanes[column] with { Color = SelectLaneColor(tipColor, column, lanes, colors.Length) };
            }

            var lanesBefore = lanes.ToList();
            var currentLane = lanes[column];
            lanes.RemoveAt(column);
            var parentColors = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var parentIndex = 0; parentIndex < commit.Parents.Count; parentIndex++)
            {
                var parentHash = commit.Parents[parentIndex];
                if (!visibleCommitHashes.Contains(parentHash))
                    continue;

                var existingParentColumn = lanes.FindIndex(lane => lane.Hash == parentHash);
                if (existingParentColumn >= 0)
                {
                    if (branchColors.TryGetValue(parentHash, out var existingParentColor))
                        lanes[existingParentColumn] = lanes[existingParentColumn] with
                        {
                            Color = SelectLaneColor(existingParentColor, existingParentColumn, lanes, colors.Length)
                        };
                    parentColors[parentHash] = lanes[existingParentColumn].Color;
                    continue;
                }

                var parentColumn = Math.Min(column + parentIndex, lanes.Count);
                var color = branchColors.TryGetValue(parentHash, out var parentColor)
                    ? parentColor
                    : parentIndex == 0
                        ? currentLane.Color
                        : branchColors.Count <= 1 ? baseColor : nextColor++ % colors.Length;
                var laneColor = SelectLaneColor(color, parentColumn, lanes, colors.Length);
                lanes.Insert(parentColumn, new GraphLane(parentHash, laneColor));
                parentColors[parentHash] = laneColor;
            }

            var edges = new List<GitGraphEdge>();
            for (var oldColumn = 0; oldColumn < lanesBefore.Count; oldColumn++)
            {
                var lane = lanesBefore[oldColumn];
                if (lane.Hash == commit.Hash)
                {
                    // Existing lanes enter from the previous row and stop just before
                    // the commit node. A newly-started lane begins at its own node.
                    if (laneWasActive)
                        edges.Add(new GitGraphEdge(oldColumn, 0, oldColumn, 19, lane.Color));
                    for (var parentIndex = 0; parentIndex < commit.Parents.Count; parentIndex++)
                    {
                        var parentHash = commit.Parents[parentIndex];
                        if (!visibleCommitHashes.Contains(parentHash))
                            continue;

                        var parentColumn = lanes.FindIndex(nextLane => nextLane.Hash == parentHash);
                        if (parentColumn >= 0)
                        {
                            // The first parent continues the current branch; merge-side parents keep
                            // the color of their own branch lane into the merge.
                            var edgeColor = parentIndex == 0 ? lane.Color : parentColors[parentHash];
                            edges.Add(new GitGraphEdge(oldColumn, 33, parentColumn, 52, edgeColor));
                        }
                    }
                }
                else
                {
                    var nextColumn = lanes.FindIndex(nextLane => nextLane.Hash == lane.Hash);
                    if (nextColumn >= 0 && visibleCommitHashes.Contains(lane.Hash))
                        edges.Add(new GitGraphEdge(oldColumn, 0, nextColumn, 52, lane.Color));
                }
            }

            laidOut.Add(commit with
            {
                GraphColumn = column,
                GraphColor = currentLane.Color,
                GraphEdges = edges,
                GraphWidth = Math.Max(lanesBefore.Count, lanes.Count),
                BranchLabels = branches
                    .Where(branch => branch.TipHash == commit.Hash)
                    .GroupBy(branch => branch.IsRemote ? GetLocalBranchName(branch.Name) : branch.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new GitCommitBranchLabel(
                        group.Key,
                        group.Any(branch => !branch.IsRemote),
                        group.Any(branch => branch.IsRemote),
                        currentLane.Color))
                    .ToList()
            });
        }

        return laidOut;
    }

    private static int SelectLaneColor(int preferredColor, int laneIndex, IReadOnlyList<GraphLane> lanes, int colorCount)
    {
        var leftColor = laneIndex > 0 ? lanes[laneIndex - 1].Color : -1;
        var rightColor = laneIndex < lanes.Count ? lanes[laneIndex].Color : -1;
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

    public async Task<GitOperationResult> PullAsync(string path) => await RunGitAsync(path, "pull", "--no-rebase");

    public async Task<GitOperationResult> PushAsync(string path) => await RunGitAsync(path, "push");

    public async Task<GitOperationResult> PushWhenUpToDateAsync(string path)
    {
        var fetchResult = await FetchAsync(path);
        if (!fetchResult.Success)
            return new GitOperationResult(false, $"Push afgebroken: remote-status ophalen is mislukt. {fetchResult.Message}", string.Empty);

        var snapshot = await GetRepositorySnapshotAsync(path);
        if (!snapshot.IsValid)
            return new GitOperationResult(false, $"Push afgebroken: remote-status controleren is mislukt. {snapshot.Error}", string.Empty);

        if (snapshot.Behind > 0)
            return new GitOperationResult(false,
                $"Push geblokkeerd: deze branch loopt {snapshot.Behind} commit(s) achter. Pull eerst de remote wijzigingen en probeer daarna opnieuw.",
                string.Empty);

        return await PushAsync(path);
    }

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

        return new GitBranchInfo(null, result.Message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            ? "Geen Git-repository"
            : result.Message.Contains("Pad bestaat niet", StringComparison.OrdinalIgnoreCase) ? "Pad bestaat niet" : "Branch niet beschikbaar");
    }

    private static async Task<GitOperationResult> RunGitAsync(string path, params string[] arguments) =>
        await RunGitCoreAsync(path, false, arguments);

    private static async Task<GitOperationResult> RunGitAllowExitOneAsync(string path, params string[] arguments) =>
        await RunGitCoreAsync(path, true, arguments);

    private static async Task<GitOperationResult> RunGitCoreAsync(string path, bool allowExitCodeOne, params string[] arguments)
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
            var success = process.ExitCode == 0 || (allowExitCodeOne && process.ExitCode == 1);
            return new GitOperationResult(success, success ? output.Trim() : error.Trim(), output);
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
public sealed record GitFileChange(string Path, bool IsStaged, bool IsUnstaged, bool IsConflicted, string Status)
{
    public bool IsUntracked => Status == "??";
}
public sealed record GitOperationResult(bool Success, string Message, string Output);
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
public sealed record GitGraphEdge(int FromColumn, double FromY, int ToColumn, double ToY, int Color);
public sealed record GitCommitBranchLabel(string Name, bool IsLocal, bool IsRemote, int Color);
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
