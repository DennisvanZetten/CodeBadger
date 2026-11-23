using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed record AgentUpdateStatus(
    string Agent,
    string InstalledVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    bool IsCheckVerified,
    bool IsConfigImpactVerified,
    bool IsMajorUpgrade,
    bool ConfigChangeMentioned,
    string ReleaseNotesUrl,
    string ExecutablePath);

public sealed class AgentUpdateService(ILogger<AgentUpdateService> logger)
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly (string Agent, string Command, string Repository, string NotesUrl)[] Agents =
    [
            (AgentTools.OpenCode, "opencode", "anomalyco/opencode", "https://opencode.ai/changelog"),
        (AgentTools.ClaudeCode, "claude", "anthropics/claude-code", "https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md"),
        (AgentTools.Codex, "codex", "openai/codex", "https://github.com/openai/codex/releases"),
        (AgentTools.Hermes, "hermes", "NousResearch/hermes-agent", "https://github.com/NousResearch/hermes-agent/releases")
    ];
    private static readonly Regex VersionPattern = new(@"(?<!\d)(\d+(?:\.\d+){1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ConfigChangePattern = new(@"\b(breaking|migration|migrat(e|ion)|config(uration)?|deprecated|deprecat(e|ion)|removed|rename)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly object _sync = new();
    private IReadOnlyList<AgentUpdateStatus> _statuses = [];
    private bool _hasChecked;
    private bool _isChecking;
    private Task? _checkTask;

    public event Action? UpdatesChanged;

    public IReadOnlyList<AgentUpdateStatus> Statuses
    {
        get { lock (_sync) return _statuses; }
    }

    public bool HasUpdates => Statuses.Any(status => status.IsUpdateAvailable);
    public bool HasChecked { get { lock (_sync) return _hasChecked; } }
    public bool IsChecking { get { lock (_sync) return _isChecking; } }

    public bool HasRunningOpenCodeProcess()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var processes = Process.GetProcessesByName("opencode");
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public async Task CheckForUpdatesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_isChecking)
                return;
            if (refresh)
            {
                _hasChecked = false;
                _statuses = [];
            }
            if (_hasChecked)
                return;
            _isChecking = true;
            _checkTask = CheckCoreAsync(cancellationToken);
        }
        UpdatesChanged?.Invoke();
        await _checkTask;
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            if (!OperatingSystem.IsWindows())
                return;

            var tasks = Agents.Select(agent => CheckAgentAsync(agent, cancellationToken));
            var results = await Task.WhenAll(tasks);
            lock (_sync)
            {
                _statuses = results.Where(status => status is not null).Cast<AgentUpdateStatus>().ToArray();
                _hasChecked = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check coding agent updates.");
        }
        finally
        {
            lock (_sync) _isChecking = false;
            UpdatesChanged?.Invoke();
        }
    }

    public async Task<string> CreateConfigBackupAsync(string agent, LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var files = GetConfigFiles(agent, settings)
            .Concat(GetConfigDirectories(agent, settings).SelectMany(EnumerateBackupFiles))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBadger", "AgentBackups");
        Directory.CreateDirectory(backupDirectory);
        var archivePath = Path.Combine(backupDirectory, $"{SanitizeFileName(agent)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFullPath(file).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            await using var entryStream = entry.Open();
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            await input.CopyToAsync(entryStream, cancellationToken);
        }
        if (files.Length == 0)
            archive.CreateEntry("No existing agent configuration files were found.txt");
        return archivePath;
    }

    public void StartUpdateInTerminal(string agent)
    {
        var command = agent switch
        {
            AgentTools.OpenCode => "opencode upgrade",
            AgentTools.ClaudeCode => "claude update",
            AgentTools.Codex => GetCodexUpdateCommand(),
            AgentTools.Hermes => "hermes update --backup",
            _ => throw new InvalidOperationException("Unsupported agent update.")
        };
        var startInfo = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false };
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add("pwsh.exe");
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"{command}; if ($LASTEXITCODE -eq 0) {{ Write-Host 'Update command completed. Review the output above; restart CodeBadger if the updated executable is still in use.' -ForegroundColor Green }} else {{ Write-Host 'Update command failed or did not complete. Review the output above.' -ForegroundColor Yellow }}");
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Could not start Windows Terminal.");
    }

    private static string GetCodexUpdateCommand()
    {
        var executable = FindExecutableAsync("codex", CancellationToken.None).GetAwaiter().GetResult();
        if (executable is null)
            throw new FileNotFoundException("Codex could not be found on PATH.");
        var path = executable.Replace("'", "''", StringComparison.Ordinal);
        return $"& '{path}' upgrade; if ($LASTEXITCODE -ne 0) {{ Write-Host 'Use the install method shown by Codex, such as its official installer, npm, or Homebrew.' -ForegroundColor Yellow }}";
    }

    private async Task<AgentUpdateStatus?> CheckAgentAsync(
        (string Agent, string Command, string Repository, string NotesUrl) definition,
        CancellationToken cancellationToken)
    {
        string? executable;
        string installedText;
        try
        {
            executable = await FindExecutableAsync(definition.Command, cancellationToken);
            if (executable is null)
                return null;
            installedText = await RunVersionAsync(executable, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not identify installed {Agent}.", definition.Agent);
            return null;
        }

        try
        {
            var installedVersion = ExtractVersion(installedText);
            if (installedVersion is null)
                return null;

            var release = definition.Agent == AgentTools.ClaudeCode
                ? await GetLatestClaudeCodeReleaseAsync(installedVersion, cancellationToken)
                : await GetLatestReleaseAsync(definition.Repository, installedVersion, cancellationToken);
            if (release is null)
                return new AgentUpdateStatus(definition.Agent, installedVersion, null, false, false, false, false, false, definition.NotesUrl, executable);

            var latestVersion = ExtractVersion(release.Value.Version);
            if (latestVersion is null || !Version.TryParse(installedVersion, out var current) || !Version.TryParse(latestVersion, out var latest))
                return new AgentUpdateStatus(definition.Agent, installedVersion, null, false, false, false, false, false, definition.NotesUrl, executable);

            var isUpdateAvailable = latest > current;
            var isMajorUpgrade = latest.Major > current.Major;
            var notesUrl = string.IsNullOrWhiteSpace(release.Value.Url) ? definition.NotesUrl : release.Value.Url;
            var coversInstalledVersion = release.Value.ReachedInstalledVersion || string.Equals(latestVersion, installedVersion, StringComparison.OrdinalIgnoreCase);
            return new AgentUpdateStatus(definition.Agent, installedVersion, latestVersion, isUpdateAvailable,
                true, coversInstalledVersion, isMajorUpgrade,
                isMajorUpgrade || ConfigChangePattern.IsMatch(release.Value.Notes), notesUrl, executable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check update for {Agent}.", definition.Agent);
            var installedVersion = ExtractVersion(installedText);
            return installedVersion is null
                ? null
                : new AgentUpdateStatus(definition.Agent, installedVersion, null, false, false, false, false, false, definition.NotesUrl, executable);
        }
    }

    private static async Task<(string Version, string Url, string Notes, bool ReachedInstalledVersion)?> GetLatestClaudeCodeReleaseAsync(
        string installedVersion, CancellationToken cancellationToken)
    {
        using var packageResponse = await Http.GetAsync("https://registry.npmjs.org/@anthropic-ai%2Fclaude-code/latest", cancellationToken);
        packageResponse.EnsureSuccessStatusCode();
        await using var packageStream = await packageResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var package = await JsonDocument.ParseAsync(packageStream, cancellationToken: cancellationToken);
        if (!package.RootElement.TryGetProperty("version", out var versionElement) || versionElement.GetString() is not { Length: > 0 } latestVersion)
            return null;

        using var notesResponse = await Http.GetAsync("https://raw.githubusercontent.com/anthropics/claude-code/main/CHANGELOG.md", cancellationToken);
        notesResponse.EnsureSuccessStatusCode();
        var allNotes = await notesResponse.Content.ReadAsStringAsync(cancellationToken);
        var latestNotes = GetChangelogRange(allNotes, latestVersion, installedVersion);
        var reachedInstalledVersion = latestNotes is not null || string.Equals(latestVersion, installedVersion, StringComparison.OrdinalIgnoreCase);
        return (latestVersion, "https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md", latestNotes ?? string.Empty, reachedInstalledVersion);
    }

    private static string? GetChangelogRange(string changelog, string latestVersion, string installedVersion)
    {
        var latest = FindVersionHeader(changelog, latestVersion);
        if (latest < 0)
            return null;
        var installed = FindVersionHeader(changelog, installedVersion, latest + 1);
        return changelog[latest..(installed >= 0 ? installed : changelog.Length)];
    }

    private static int FindVersionHeader(string changelog, string version, int startIndex = 0)
    {
        var escaped = Regex.Escape(version);
        var match = Regex.Match(changelog[startIndex..], $"(?m)^#+[^\\r\\n]*\\bv?{escaped}\\b[^\\r\\n]*");
        return match.Success ? startIndex + match.Index : -1;
    }

    private static async Task<(string Version, string Url, string Notes, bool ReachedInstalledVersion)?> GetLatestReleaseAsync(
        string repository, string installedVersion, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=100", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        string? latestTag = null;
        string latestUrl = string.Empty;
        var notes = new List<string>();
        var reachedInstalledVersion = false;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
                continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var version = ExtractVersion(tag);
            if (version is null)
                continue;
            if (latestTag is null)
            {
                latestTag = tag;
                latestUrl = release.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
            }
            if (Version.TryParse(version, out var parsedInstalled) && Version.TryParse(installedVersion, out var installed) && parsedInstalled <= installed)
            {
                reachedInstalledVersion = true;
                break;
            }
            if (release.TryGetProperty("body", out var bodyElement) && bodyElement.GetString() is { Length: > 0 } body)
                notes.Add(body);
        }
        return latestTag is null ? null : (latestTag, latestUrl, string.Join("\n", notes), reachedInstalledVersion);
    }

    private static async Task<string?> FindExecutableAsync(string command, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "where.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { command }
        });
        if (process is null)
            return null;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0
            ? output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists)
            : null;
    }

    private static async Task<string> RunVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var escaped = executable.Replace("'", "''", StringComparison.Ordinal);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", $"& '{escaped}' --version" }
        });
        if (process is null)
            return string.Empty;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? await stdout : await stderr;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return string.Empty;
        }
    }

    private static IEnumerable<string> GetConfigFiles(string agent, LauncherSettings settings)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] global = agent switch
        {
            AgentTools.OpenCode => [Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "opencode.json"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "opencode.jsonc"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "cli.json"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "tui.json"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "tui.jsonc")],
            AgentTools.ClaudeCode => [Path.Combine(home, ".claude.json"), Path.Combine(home, ".claude", "settings.json"), Path.Combine(home, ".claude", "settings.local.json"), Path.Combine(home, ".claude", "CLAUDE.md")],
            AgentTools.Codex => [Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "config.toml")],
            AgentTools.Hermes => [Path.Combine(Environment.GetEnvironmentVariable("HERMES_HOME") ?? Path.Combine(home, ".hermes"), "config.yaml")],
            _ => Array.Empty<string>()
        };

        foreach (var path in global)
            if (File.Exists(path))
                yield return path;

        foreach (var repository in settings.Repositories.Where(repository => Directory.Exists(repository.Path)))
        {
            string[] projectFiles = agent switch
            {
                AgentTools.OpenCode => [Path.Combine(repository.Path, "opencode.json"), Path.Combine(repository.Path, "opencode.jsonc"), Path.Combine(repository.Path, ".opencode", "opencode.json"), Path.Combine(repository.Path, ".opencode", "opencode.jsonc")],
                AgentTools.ClaudeCode => [Path.Combine(repository.Path, ".mcp.json"), Path.Combine(repository.Path, ".claude", "settings.json"), Path.Combine(repository.Path, ".claude", "settings.local.json"), Path.Combine(repository.Path, "CLAUDE.md"), Path.Combine(repository.Path, ".claude", "CLAUDE.md")],
                AgentTools.Codex => [Path.Combine(repository.Path, ".codex", "config.toml")],
                _ => Array.Empty<string>()
            };
            foreach (var file in projectFiles)
                if (File.Exists(file))
                    yield return file;

        }
    }

    private static IEnumerable<string> GetConfigDirectories(string agent, LauncherSettings settings)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        if (agent == AgentTools.OpenCode)
        {
            var globalRoot = Path.Combine(xdg, "opencode");
            foreach (var name in new[] { "agents", "commands", "plugins", "skills" })
                yield return Path.Combine(globalRoot, name);
        }
        else if (agent == AgentTools.ClaudeCode)
        {
            foreach (var name in new[] { "agents", "commands", "rules", "skills" })
                yield return Path.Combine(home, ".claude", name);
        }

        foreach (var repository in settings.Repositories.Where(repository => Directory.Exists(repository.Path)))
        {
            if (agent == AgentTools.OpenCode)
            {
                foreach (var name in new[] { "agents", "commands", "plugins", "skills" })
                    yield return Path.Combine(repository.Path, ".opencode", name);
            }
            else if (agent == AgentTools.ClaudeCode)
            {
                foreach (var name in new[] { "agents", "commands", "rules", "skills" })
                    yield return Path.Combine(repository.Path, ".claude", name);
            }
            else if (agent == AgentTools.Codex)
                yield return Path.Combine(repository.Path, ".agents", "skills");
        }
    }

    private static IEnumerable<string> EnumerateBackupFiles(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(".venv", StringComparison.OrdinalIgnoreCase))
                        pending.Push(entry);
                }
                else
                    yield return entry;
            }
        }
    }

    private static string? ExtractVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = VersionPattern.Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeBadger-AgentUpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
