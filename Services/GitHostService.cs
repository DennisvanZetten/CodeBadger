using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed record GitHostConnection(string Provider, bool IsAvailable, string Status);
public sealed record GitHostRepository(string Name, string Url, string FullName, bool IsPrivate = false);
public sealed record GitHostProject(string Name, string Id);

public sealed class GitHostService(LocalizationService localization, ILogger<GitHostService> logger)
{
    public async Task<GitHostConnection> CheckGitHubAsync()
    {
        var result = await RunCliCheckAsync("gh", ["auth", "status", "--hostname", "github.com"]);
        return new GitHostConnection("GitHub", result.Success,
            result.Success ? localization.T("Connected to GitHub.") :
                !string.IsNullOrWhiteSpace(result.Message) ? result.Message : localization.T("GitHub CLI is not authenticated. Run gh auth login."));
    }

    public async Task<GitHostConnection> CheckAzureDevOpsAsync(string organization)
    {
        var extension = await RunCliCheckAsync("az", ["extension", "show", "--name", "azure-devops", "--output", "none"]);
        if (!extension.Success)
            return new GitHostConnection("Azure DevOps", false, localization.T("Azure DevOps CLI extension is unavailable. Install it with: az extension add --name azure-devops"));
        var account = await RunCliCheckAsync("az", ["account", "show", "--output", "none"]);
        if (!account.Success)
            return new GitHostConnection("Azure DevOps", false, localization.T("Sign in to Azure CLI with az login before using Azure DevOps."));
        if (string.IsNullOrWhiteSpace(organization))
            return new GitHostConnection("Azure DevOps", true, localization.T("Azure CLI is signed in. Azure DevOps organization access will be checked when selected."));
        var result = await RunAsync("az", "devops", "project", "list", "--org", organization, "--output", "json");
        return new GitHostConnection("Azure DevOps", result.Success,
            result.Success ? localization.T("Connected to Azure DevOps.") :
                !string.IsNullOrWhiteSpace(result.Message) ? result.Message : localization.T("Azure DevOps access could not be verified for this organization."));
    }

    public async Task<GitHostConnection> CheckAzureDevOpsCliAsync()
    {
        var extension = await RunCliCheckAsync("az", ["extension", "show", "--name", "azure-devops", "--output", "none"]);
        if (!extension.Success)
            return new GitHostConnection("Azure DevOps", false, localization.T("Azure DevOps CLI extension is unavailable. Install it with: az extension add --name azure-devops"));
        var account = await RunCliCheckAsync("az", ["account", "show", "--output", "none"]);
        return account.Success
            ? new GitHostConnection("Azure DevOps", true, localization.T("Azure CLI is signed in. Azure DevOps organization access will be checked when selected."))
            : new GitHostConnection("Azure DevOps", false, localization.T("Sign in to Azure CLI with az login before using Azure DevOps."));
    }

    public async Task<IReadOnlyList<GitHostRepository>> ListGitHubRepositoriesAsync()
    {
        var result = await RunAsync("gh", "repo", "list", "--limit", "1000", "--json", "name,nameWithOwner,url,isPrivate");
        EnsureSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.EnumerateArray().Select(item => new GitHostRepository(
            item.GetProperty("name").GetString() ?? string.Empty,
            item.GetProperty("url").GetString() ?? string.Empty,
            item.GetProperty("nameWithOwner").GetString() ?? string.Empty,
            item.TryGetProperty("isPrivate", out var isPrivate) && isPrivate.GetBoolean())).ToList();
    }

    public async Task<IReadOnlyList<GitHostProject>> ListAzureProjectsAsync(string organization)
    {
        var result = await RunAsync("az", "devops", "project", "list", "--org", organization, "--output", "json");
        EnsureSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        var projects = document.RootElement.TryGetProperty("value", out var value) ? value : document.RootElement;
        if (projects.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(localization.T("No Azure DevOps projects were returned."));
        return projects.EnumerateArray().Select(item => new GitHostProject(
            item.GetProperty("name").GetString() ?? string.Empty,
            item.GetProperty("id").GetString() ?? string.Empty)).ToList();
    }

    public async Task<IReadOnlyList<GitHostRepository>> ListAzureRepositoriesAsync(string organization, string project)
    {
        var result = await RunAsync("az", "repos", "list", "--org", organization, "--project", project, "--output", "json");
        EnsureSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(localization.T("No repositories were found."));
        return document.RootElement.EnumerateArray().Select(item => new GitHostRepository(
            item.GetProperty("name").GetString() ?? string.Empty,
            item.TryGetProperty("remoteUrl", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            item.GetProperty("name").GetString() ?? string.Empty)).ToList();
    }

    public async Task<string> CreateGitHubRepositoryAsync(string name, string description, bool isPrivate)
    {
        var args = new List<string>
        {
            "api", "user/repos", "--method", "POST", "-f", $"name={name}",
            "-F", $"private={isPrivate.ToString().ToLowerInvariant()}", "--jq", ".clone_url"
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            args.Add("-f");
            args.Add($"description={description}");
        }
        var result = await RunAsync("gh", args.ToArray());
        EnsureSuccess(result);
        return result.Output.Trim();
    }

    public async Task<string> GetGitHubRepositoryUrlAsync(string name)
    {
        var result = await RunAsync("gh", "repo", "view", name, "--json", "url", "--jq", ".url");
        EnsureSuccess(result);
        return result.Output.Trim();
    }

    public async Task<string> CreateAzureRepositoryAsync(string organization, string project, string name)
    {
        var result = await RunAsync("az", "repos", "create", "--name", name, "--org", organization, "--project", project, "--output", "json");
        EnsureSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        if (!document.RootElement.TryGetProperty("remoteUrl", out var remoteUrl))
            throw new InvalidOperationException(localization.T("Azure DevOps did not return the new repository URL."));
        return remoteUrl.GetString()
            ?? throw new InvalidOperationException(localization.T("Azure DevOps did not return the new repository URL."));
    }

    public async Task<string> GetAzureRepositoryUrlAsync(string organization, string project, string name)
    {
        var result = await RunAsync("az", "repos", "show", "--repository", name, "--org", organization, "--project", project, "--output", "json");
        EnsureSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.TryGetProperty("remoteUrl", out var remoteUrl) && remoteUrl.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(localization.T("Azure DevOps did not return the new repository URL."));
    }

    public async Task CloneAsync(string url, string targetPath)
    {
        if (Directory.Exists(targetPath) || File.Exists(targetPath))
            throw new InvalidOperationException(localization.T("The destination path already exists."));
        var parent = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(parent);
        var result = await RunAsync("git", "clone", url, targetPath);
        EnsureSuccess(result);
    }

    public async Task<string> ValidateGitRepositoryAsync(string path)
    {
        var result = await RunGitAsync(path, "rev-parse", "--show-toplevel");
        EnsureSuccess(result);
        return result.Output.Trim();
    }

    public async Task<bool> ValidateRepositoryAsync(string path)
    {
        var result = await RunGitAsync(path, "rev-parse", "--show-toplevel");
        return result.Success;
    }

    public async Task ValidateGitIdentityAsync()
    {
        foreach (var setting in new[] { "user.name", "user.email" })
        {
            var result = await RunAsync("git", "config", "--global", "--get", setting);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException(localization.T("Configure your Git {0} before creating a repository.", setting == "user.name" ? "name" : "email"));
        }
    }

    public async Task ValidateGitHubAuthenticationAsync()
    {
        var result = await RunAsync("gh", ["auth", "status", "--hostname", "github.com"], null, suppressStandardError: true);
        if (!result.Success)
            throw new InvalidOperationException(localization.T("GitHub CLI is not authenticated. Run gh auth login."));
    }

    public async Task PrepareLocalRepositoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        var init = await RunGitAsync(path, "init", "--initial-branch=main");
        EnsureSuccess(init);

        if (!Directory.EnumerateFileSystemEntries(path).Any(entry =>
                !Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase)))
            await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# " + Path.GetFileName(path) + Environment.NewLine);

        EnsureSuccess(await RunGitAsync(path, "add", "--all"));
        var hasCommit = await RunGitAsync(path, "rev-parse", "--verify", "HEAD");
        if (!hasCommit.Success)
            EnsureSuccess(await RunGitAsync(path, "commit", "-m", "Initial commit"));
        else
        {
            var status = await RunGitAsync(path, "status", "--porcelain");
            EnsureSuccess(status);
            if (!string.IsNullOrWhiteSpace(status.Output))
                EnsureSuccess(await RunGitAsync(path, "commit", "-m", "Initial commit"));
        }
    }

    public async Task<string?> GetOriginUrlAsync(string path)
    {
        var result = await RunGitAsync(path, "remote", "get-url", "origin");
        return result.Success ? result.Output.Trim() : null;
    }

    public async Task ValidateEmptyOrGitRepositoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;
        var isGit = await RunGitAsync(path, "rev-parse", "--show-toplevel");
        if (isGit.Success)
            return;
        if (Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException(localization.T("The destination folder must be empty or a Git repository."));
    }

    public async Task PushToRemoteAsync(string path, string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new InvalidOperationException(localization.T("The local repository does not have an origin remote."));
        var currentRemote = await GetOriginUrlAsync(path);
        if (string.IsNullOrWhiteSpace(currentRemote))
            EnsureSuccess(await RunGitAsync(path, "remote", "add", "origin", remoteUrl));
        else if (!string.Equals(currentRemote, remoteUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(localization.T("The local repository already has a different origin remote: {0}", currentRemote));
        EnsureSuccess(await RunGitAsync(path, "push", "--set-upstream", "origin", "main"));
    }

    private async Task<CommandResult> RunGitAsync(string path, params string[] arguments) =>
        await RunAsync("git", arguments, path);

    private async Task<CommandResult> RunCliCheckAsync(string executable, string[] arguments)
    {
        // Azure CLI is commonly installed as az.cmd; shims for gh may also be batch files.
        // Windows cannot launch these wrappers directly with UseShellExecute=false.
        if (!OperatingSystem.IsWindows())
            return await RunAsync(executable, arguments, null, suppressStandardError: true);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteCommandArgument)));

        try
        {
            var startedAt = Stopwatch.GetTimestamp();
            logger.LogInformation("Running Git host CLI: {Executable} {Arguments}.", executable, FormatLogArguments(arguments));
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(localization.T("Could not start {0}.", executable));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode == 0)
                logger.LogInformation("Git host CLI {Executable} completed with exit code 0 in {ElapsedMilliseconds} ms.", executable, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            else
                logger.LogWarning("Git host CLI {Executable} failed with exit code {ExitCode} in {ElapsedMilliseconds} ms.", executable, process.ExitCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            var message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            if (process.ExitCode == 0)
                logger.LogInformation("Git host CLI {Executable} response: {CommandResponse}", executable, TruncateLogOutput(output));
            else
                logger.LogInformation("Git host CLI {Executable} error response: {CommandResponse}", executable, TruncateLogOutput(error));
            return new CommandResult(process.ExitCode == 0, output.Trim(), message);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Could not start Git host CLI {Executable}: {ErrorMessage}", executable, exception.Message);
            return new CommandResult(false, string.Empty, localization.T("{0} is not installed or is not available in PATH.", executable));
        }
    }

    private static string QuoteCommandArgument(string argument)
    {
        if (argument.Contains('"') || argument.Contains('\r') || argument.Contains('\n'))
            throw new ArgumentException("CLI arguments cannot contain quotes or line breaks.", nameof(argument));
        return "\"" + argument.Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }

    private async Task<CommandResult> RunAsync(string executable, params string[] arguments) =>
        await RunAsync(executable, arguments, null);

    private async Task<CommandResult> RunAsync(string executable, string[] arguments, string? workingDirectory, bool suppressStandardError = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var useGitDirectoryArgument = executable.Equals("git", StringComparison.OrdinalIgnoreCase)
            && OperatingSystem.IsMacCatalyst()
            && !string.IsNullOrWhiteSpace(workingDirectory);
        if (useGitDirectoryArgument)
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(workingDirectory!);
        }
        else if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            var startedAt = Stopwatch.GetTimestamp();
            logger.LogInformation("Running Git host CLI: {Executable} {Arguments}.", executable, FormatLogArguments(arguments));
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(localization.T("Could not start {0}.", executable));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode == 0)
                logger.LogInformation("Git host CLI {Executable} completed with exit code 0 in {ElapsedMilliseconds} ms.", executable, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            else
                logger.LogWarning("Git host CLI {Executable} failed with exit code {ExitCode} in {ElapsedMilliseconds} ms.", executable, process.ExitCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            var message = suppressStandardError ? string.Empty : string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            if (!suppressStandardError)
                logger.LogInformation("Git host CLI {Executable} response: {CommandResponse}", executable,
                    TruncateLogOutput(process.ExitCode == 0 || string.IsNullOrWhiteSpace(error) ? output : error));
            return new CommandResult(process.ExitCode == 0, output.Trim(), message);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Could not start Git host CLI {Executable}: {ErrorMessage}", executable, exception.Message);
            return new CommandResult(false, string.Empty,
                $"{localization.T("{0} is not installed or is not available in PATH.", executable)} ({exception.GetType().Name}: {exception.Message})");
        }
        catch (DirectoryNotFoundException exception)
        {
            logger.LogWarning("Git host CLI {Executable} working folder does not exist: {ErrorMessage}", executable, exception.Message);
            return new CommandResult(false, string.Empty,
                $"{localization.T("The selected destination folder does not exist.")} ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private static string FormatLogArguments(IEnumerable<string> arguments)
    {
        var result = new List<string>();
        var redactNext = false;
        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                result.Add("[redacted]");
                redactNext = false;
                continue;
            }

            var safe = System.Text.RegularExpressions.Regex.Replace(argument,
                @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
            safe = System.Text.RegularExpressions.Regex.Replace(safe,
                @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
            result.Add(safe.Contains(' ') ? "\"" + safe.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : safe);
            redactNext = argument is "-m" or "--message" or "--body" or "--body-file";
        }
        return string.Join(' ', result);
    }

    private static string TruncateLogOutput(string value)
    {
        const int maximumLength = 2000;
        var safe = System.Text.RegularExpressions.Regex.Replace(value.Trim(),
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        safe = System.Text.RegularExpressions.Regex.Replace(safe,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return safe.Length <= maximumLength ? safe : safe[..maximumLength] + "…";
    }

    private static void EnsureSuccess(CommandResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
    }

    private sealed record CommandResult(bool Success, string Output, string Message);
}
