using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed record AgentStartupState(bool Installed, bool HasGlobalConfig, string ConfigPath);

public sealed record OpenCodeInstanceState(
    string ExecutablePath,
    string InstallMethod,
    string? NodeVersion,
    string? Version,
    bool IsWorking,
    bool IsSelected,
    string? Failure);

public sealed record OpenCodeStartupHealth(
    List<OpenCodeInstanceState> Instances,
    string? SelectedExecutablePath,
    bool ServiceHealthy,
    bool HasConflict,
    string? Warning);

public sealed record RepositoryStartupState(bool HasOpenCodeSettings, bool HasClaudeSettings, bool HasCodexSettings, bool IsGitRepository);

public sealed record StartupDetectionResult(
    AgentStartupState OpenCode,
    AgentStartupState ClaudeCode,
    AgentStartupState Codex,
    AgentStartupState Hermes,
    IReadOnlyDictionary<Guid, RepositoryStartupState> Repositories);

public sealed class StartupDetectionService(
    AgentConfigService agentConfigService,
    OpenCodeRepositoryConfigService openCodeConfigService,
    ClaudeRepositoryConfigService claudeConfigService,
    CodexConfigService codexConfigService,
    GitBranchService gitBranchService,
    OpenCodeActivityService openCodeActivityService,
    LocalizationService localization,
    ILogger<StartupDetectionService> logger)
{
    public StartupDetectionResult? Result { get; private set; }
    private readonly SemaphoreSlim _openCodeCheckGate = new(1, 1);
    private readonly object _openCodeSync = new();
    private IReadOnlyList<OpenCodeInstanceState> _openCodeInstances = [];
    private string? _selectedOpenCodeExecutablePath;
    private bool _openCodeServiceHealthy;

    public event Action<OpenCodeStartupHealth>? OpenCodeStartupHealthChanged;

    public OpenCodeStartupHealth OpenCodeHealth
    {
        get
        {
            lock (_openCodeSync)
            {
                var conflicts = _openCodeInstances.Count > 1;
                var noWorkingInstance = _openCodeInstances.Count > 0 && _openCodeInstances.All(instance => !instance.IsWorking);
                string? warning = null;
                if (noWorkingInstance)
                    warning = "The OpenCode executable was detected, but its managed service did not pass the startup health check. CodeBadger will not keep polling it during this launch. Review the database and installation details below.";
                else if (_selectedOpenCodeExecutablePath is not null && !_openCodeServiceHealthy)
                    warning = "OpenCode answered its health check but returned an empty session list, which CodeBadger cannot distinguish from an inaccessible database. Activity polling has been disabled for this launch.";
                else if (conflicts)
                    warning = "Multiple OpenCode installations were detected. CodeBadger uses the PATH-selected instance below. Failed instances are not polled again during this launch.";
                return new OpenCodeStartupHealth(_openCodeInstances.ToList(), _selectedOpenCodeExecutablePath,
                    _openCodeServiceHealthy, warning is not null, warning);
            }
        }
    }

    public void RefreshOpenCodeStartupWarning(string? warning)
    {
        lock (_openCodeSync)
        {
            _openCodeInstances = _openCodeInstances.Select(instance => instance with
            {
                Failure = instance.IsSelected && warning is not null ? warning : instance.Failure
            }).ToList();
        }
        OpenCodeStartupHealthChanged?.Invoke(OpenCodeHealth);
    }

    public void RecheckOpenCodeActivityEndpoint() => openCodeActivityService.ResetActivityEndpointFailure();
    public async Task<bool> RecheckOpenCodeActivityAsync(CancellationToken cancellationToken)
    {
        var executable = OpenCodeHealth.SelectedExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
            return false;
        return await CheckOpenCodeServiceAsync(executable, cancellationToken);
    }

    public async Task<StartupDetectionResult> DetectAsync(
        LauncherSettings settings,
        Func<string, double, Task> reportProgress)
    {
        var startedAt = Stopwatch.GetTimestamp();
        logger.LogInformation("Startup checks started for {RepositoryCount} configured repositories.", settings.Repositories.Count);
        var repositories = settings.Repositories;
        var totalSteps = 4 + repositories.Count * 2;
        var completedSteps = 0;

        async Task Report(string stage)
        {
            completedSteps++;
            await reportProgress(stage, completedSteps * 100d / totalSteps);
        }

        await DetectOpenCodeInstancesAsync(CancellationToken.None);
        await reportProgress(localization.T("Detecting OpenCode..."), 0);
        var openCodePath = GetExistingGlobalConfigPath(AgentTools.OpenCode);
        var openCode = new AgentStartupState(OpenCodeHealth.SelectedExecutablePath is not null, File.Exists(openCodePath), openCodePath);
        await Report(localization.T("Detecting Claude Code..."));

        var claudePath = GetExistingGlobalConfigPath(AgentTools.ClaudeCode);
        var claude = new AgentStartupState(await IsCommandAvailableAsync("claude", logger), File.Exists(claudePath), claudePath);
        await Report(localization.T("Detecting Codex..."));

        var codexPath = GetExistingGlobalConfigPath(AgentTools.Codex);
        var codex = new AgentStartupState(await IsCommandAvailableAsync("codex", logger), File.Exists(codexPath), codexPath);
        await Report(localization.T("Detecting Hermes..."));

        var hermesPath = GetExistingGlobalConfigPath(AgentTools.Hermes);
        var hermes = new AgentStartupState(await IsCommandAvailableAsync("hermes", logger), File.Exists(hermesPath), hermesPath);
        await Report(localization.T("Checking global agent settings..."));

        var repositoryStates = new Dictionary<Guid, RepositoryStartupState>();
        foreach (var repository in repositories)
        {
            await reportProgress(localization.T("Checking repository: {0}", string.IsNullOrWhiteSpace(repository.Name) ? repository.Path : repository.Name),
                completedSteps * 100d / totalSteps);
            await Task.Yield();

            var hasOpenCodeSettings = Directory.Exists(repository.Path) && HasOpenCodeSettings(repository.Path);
            var hasClaudeSettings = Directory.Exists(repository.Path) && HasClaudeSettings(repository.Path);
            var hasCodexSettings = Directory.Exists(repository.Path) && HasCodexSettings(repository.Path);
            await Report(localization.T("Checking repository settings..."));
            var isGitRepository = await gitBranchService.IsGitRepositoryAsync(repository.Path);
            repositoryStates[repository.Id] = new RepositoryStartupState(hasOpenCodeSettings, hasClaudeSettings, hasCodexSettings, isGitRepository);
            await Report(localization.T("Checking repository Git status..."));
        }

        Result = new StartupDetectionResult(openCode, claude, codex, hermes, repositoryStates);
        logger.LogInformation(
            "Startup checks finished in {ElapsedMilliseconds} ms. Agents available: OpenCode={OpenCodeAvailable}, ClaudeCode={ClaudeAvailable}, Codex={CodexAvailable}, Hermes={HermesAvailable}; Git repositories detected: {GitRepositoryCount}/{RepositoryCount}.",
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, openCode.Installed, claude.Installed, codex.Installed, hermes.Installed,
            repositoryStates.Values.Count(state => state.IsGitRepository), repositories.Count);
        await reportProgress(localization.T("Startup checks complete."), 100);
        return Result;
    }

    public AgentStartupState? GetAgentState(string agentTool) => Result is null ? null
        : AgentTools.Normalize(agentTool) switch
        {
            AgentTools.ClaudeCode => Result.ClaudeCode,
            AgentTools.Codex => Result.Codex,
            AgentTools.Hermes => Result.Hermes,
            _ => Result.OpenCode
        };

    public bool IsAgentAvailable(string agentTool) => GetAgentState(agentTool)?.Installed == true;

    public async Task DetectOpenCodeInstancesAsync(CancellationToken cancellationToken, bool force = false)
    {
        await _openCodeCheckGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && OpenCodeExecutableLocator.HealthCheckCompleted)
                return;
            var candidates = new List<string>();
            candidates.AddRange(OpenCodeExecutableLocator.FindAllInPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
            candidates.AddRange(GetKnownManagedOpenCodeExecutables());
            if (!string.IsNullOrWhiteSpace(OpenCodeExecutableLocator.PreferredExecutable))
                candidates.Add(OpenCodeExecutableLocator.PreferredExecutable);
            candidates = candidates.Where(File.Exists).Select(CanonicalizePath)
                .Aggregate(new List<string>(), (unique, candidate) =>
                {
                    if (!unique.Any(existing => OpenCodeExecutableLocator.AreSameFile(existing, candidate)))
                        unique.Add(candidate);
                    return unique;
                });

            if (candidates.Count == 0)
            {
                OpenCodeExecutableLocator.SetHealthCheckResult(null);
                lock (_openCodeSync)
                {
                    _openCodeInstances = new List<OpenCodeInstanceState>();
                    _selectedOpenCodeExecutablePath = null;
                    _openCodeServiceHealthy = false;
                }
                OpenCodeStartupHealthChanged?.Invoke(OpenCodeHealth);
                return;
            }

            var instances = new List<OpenCodeInstanceState>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                instances.Add(await ReadOpenCodeVersionAsync(candidate, cancellationToken));
            }

            var selected = instances.FirstOrDefault(instance => instance.IsWorking);
            var serviceHealthy = selected is not null && await CheckOpenCodeServiceAsync(selected.ExecutablePath, cancellationToken);
            if (selected is not null && !serviceHealthy)
                instances = instances.Select(instance => string.Equals(instance.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    ? instance with { Failure = "The OpenCode executable responds, but its managed session service is unavailable." }
                    : instance).ToList();
            if (selected is not null)
                instances = instances.Select(instance => instance with
                    { IsSelected = string.Equals(instance.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase) }).ToList();
            OpenCodeExecutableLocator.SetHealthCheckResult(selected?.ExecutablePath);

            lock (_openCodeSync)
            {
                _openCodeInstances = instances;
                _selectedOpenCodeExecutablePath = selected?.ExecutablePath;
                _openCodeServiceHealthy = serviceHealthy;
            }
            OpenCodeStartupHealthChanged?.Invoke(OpenCodeHealth);
        }
        finally
        {
            _openCodeCheckGate.Release();
        }
    }

    private async Task<OpenCodeInstanceState> ReadOpenCodeVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var versionText = await RunExecutableAsync(executable, new[] { "--version" }, cancellationToken);
            var versionMatch = Regex.Match(versionText, @"(?i)v?(\d+\.\d+\.\d+)");
            var version = versionMatch.Success ? versionMatch.Groups[1].Value : null;
            var installation = DetectOpenCodeInstallMethod(executable);
            if (version is null)
                return new OpenCodeInstanceState(executable, installation.Method, installation.NodeVersion, null, false, false,
                    "The executable did not return a readable version.");

            return new OpenCodeInstanceState(executable, installation.Method, installation.NodeVersion, version, true, false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var installation = DetectOpenCodeInstallMethod(executable);
            return new OpenCodeInstanceState(executable, installation.Method, installation.NodeVersion, null, false, false,
                "The OpenCode health check timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "OpenCode instance health check failed for {ExecutablePath}.", executable);
            var installation = DetectOpenCodeInstallMethod(executable);
            return new OpenCodeInstanceState(executable, installation.Method, installation.NodeVersion, null, false, false,
                Truncate(exception.Message, 320));
        }
    }

    private async Task<bool> CheckOpenCodeServiceAsync(string executable, CancellationToken cancellationToken)
    {
        var hasSelectedExecutable = false;
        lock (_openCodeSync)
            hasSelectedExecutable = _selectedOpenCodeExecutablePath is not null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "api", "get", "/api/session/active" }
            });
            if (probe is null)
                return false;
            var stdout = probe.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = probe.StandardError.ReadToEndAsync(timeout.Token);
            await probe.WaitForExitAsync(timeout.Token);
            if (probe.ExitCode != 0)
            {
                var failure = (await stderr).Trim();
                if (failure.Contains("disk I/O error", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("OpenCode managed service reported a database disk I/O error for {ExecutablePath}.", executable);
                    openCodeActivityService.MarkActivityEndpointFailed();
                    return false;
                }
                logger.LogWarning("OpenCode service health check failed for {ExecutablePath}: {Failure}", executable,
                    Truncate(failure, 320));
                MarkSelectedOpenCodeServiceUnhealthy(executable, Truncate(failure, 320));
                return false;
            }
            using var response = JsonDocument.Parse(await stdout);
            var healthy = response.RootElement.ValueKind == JsonValueKind.Object &&
                          response.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            if (!healthy)
            {
                openCodeActivityService.MarkActivityEndpointFailed();
                MarkSelectedOpenCodeServiceUnhealthy(executable, "OpenCode returned an unexpected response for its active session list.");
            }
            return healthy;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenCode service health check timed out for {ExecutablePath}.", executable);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "OpenCode service health check failed for {ExecutablePath}.", executable);
            if (hasSelectedExecutable)
                MarkSelectedOpenCodeServiceUnhealthy(executable, Truncate(exception.Message, 320));
            return false;
        }
    }

    private void MarkSelectedOpenCodeServiceUnhealthy(string executable, string failure)
    {
        lock (_openCodeSync)
        {
            _openCodeServiceHealthy = false;
            _openCodeInstances = _openCodeInstances.Select(instance => instance with
            {
                IsSelected = string.Equals(instance.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase),
                Failure = string.Equals(instance.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase) ? failure : instance.Failure
            }).ToList();
        }
        OpenCodeStartupHealthChanged?.Invoke(OpenCodeHealth);
    }

    private static async Task<string> RunExecutableAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The OpenCode executable could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
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
            throw new TimeoutException("OpenCode version check timed out.");
        }
    }

    private static (string Method, string? NodeVersion) DetectOpenCodeInstallMethod(string executablePath)
    {
        var normalized = executablePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\nvm4w\\", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains("\\nvm4w\\", StringComparison.OrdinalIgnoreCase))
                return ("npm", null);
            var nvmRoot = Environment.GetEnvironmentVariable("NVM_HOME");
            if (string.IsNullOrWhiteSpace(nvmRoot))
                nvmRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "nvm");
            string? installRoot = null;
            try
            {
                installRoot = Directory.GetDirectories(nvmRoot, "v*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(directory => directory.Length)
                    .FirstOrDefault(directory => IsUnderDirectory(executablePath, directory));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (installRoot is null)
                return ("npm", null);
            var relative = Path.GetRelativePath(nvmRoot, installRoot);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return firstSegment is not null && firstSegment.StartsWith('v') && Directory.Exists(Path.Combine(nvmRoot, firstSegment))
                ? ("npm", firstSegment)
                : ("npm", null);
        }
        if (normalized.Contains("\\scoop\\apps\\", StringComparison.OrdinalIgnoreCase))
            return ("Scoop", null);
        if (normalized.Contains("\\WinGet\\Packages\\", StringComparison.OrdinalIgnoreCase))
            return ("WinGet", null);
        return ("Standalone executable", null);
    }

    private static string CanonicalizePath(string path)
    {
        try
        {
            var canonical = OpenCodeExecutableLocator.CanonicalizePath(path);
            var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME") ??
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "nvm");
            var nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
            if (!string.IsNullOrWhiteSpace(nvmSymlink) && IsUnderDirectory(path, nvmSymlink))
                return canonical;
            if (!string.IsNullOrWhiteSpace(nvmSymlink) && IsUnderDirectory(canonical, nvmHome))
                return Path.GetFullPath(path);
            return canonical;
        }
        catch (IOException) { return Path.GetFullPath(path); }
        catch (UnauthorizedAccessException) { return Path.GetFullPath(path); }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetKnownManagedOpenCodeExecutables()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetEnvironmentVariable("NVM_HOME") ?? Path.Combine(home, "AppData", "Local", "nvm"),
                "node_modules", "@opencode", "cli", "bin", "opencode.exe")
        };

        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME") ?? Path.Combine(home, "AppData", "Local", "nvm");
        if (Directory.Exists(nvmHome))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateDirectories(nvmHome, "v*", SearchOption.TopDirectoryOnly)
                    .Select(versionDirectory => Path.Combine(versionDirectory, "node_modules", "@opencode", "cli", "bin", "opencode.exe")));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (Environment.GetEnvironmentVariable("NVM_SYMLINK") is { Length: > 0 } nvmSymlink)
            candidates.Add(Path.Combine(nvmSymlink, "node_modules", "@opencode", "cli", "bin", "opencode.exe"));

        foreach (var candidate in candidates.Where(File.Exists))
            yield return candidate;
    }

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..maximumLength];

    private bool HasOpenCodeSettings(string repositoryPath) =>
        HasExistingOpenCodeProjectConfig(repositoryPath) ||
        Directory.Exists(openCodeConfigService.GetSkillsDirectory(repositoryPath));

    private bool HasExistingOpenCodeProjectConfig(string repositoryPath) => new[]
    {
        Path.Combine(repositoryPath, ".opencode", "opencode.jsonc"),
        Path.Combine(repositoryPath, ".opencode", "opencode.json"),
        Path.Combine(repositoryPath, "opencode.jsonc"),
        Path.Combine(repositoryPath, "opencode.json")
    }.Any(File.Exists);

    private bool HasClaudeSettings(string repositoryPath) =>
        File.Exists(claudeConfigService.GetMcpConfigPath(repositoryPath)) ||
        File.Exists(Path.Combine(repositoryPath, ".claude", "settings.json")) ||
        File.Exists(claudeConfigService.GetLocalSettingsPath(repositoryPath));

    private bool HasCodexSettings(string repositoryPath) =>
        File.Exists(codexConfigService.GetProjectConfigPath(repositoryPath)) ||
        Directory.Exists(codexConfigService.GetSkillsDirectory(repositoryPath));

    private string GetExistingGlobalConfigPath(string agentTool)
    {
        var configPath = agentConfigService.GetConfigPath(agentTool);
        if (AgentTools.Normalize(agentTool) == AgentTools.Codex)
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
                    ? codexHome
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "config.toml")
            };
            return candidates.FirstOrDefault(File.Exists) ?? configPath;
        }

        if (AgentTools.Normalize(agentTool) == AgentTools.OpenCode)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configDirectory = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configHome
                ? configHome
                : Path.Combine(home, ".config"), "opencode");
            var candidates = new[]
            {
                Path.Combine(configDirectory, "opencode.jsonc"),
                Path.Combine(configDirectory, "opencode.json")
            };
            return candidates.FirstOrDefault(File.Exists) ?? configPath;
        }

        if (AgentTools.Normalize(agentTool) == AgentTools.Hermes)
            return Path.Combine(Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } hermesHome
                ? hermesHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes"), "config.yaml");

        return configPath;
    }

    private static async Task<bool> IsCommandAvailableAsync(string command, ILogger logger)
    {
        if (OperatingSystem.IsIOS())
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(command);
            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            await process.WaitForExitAsync();
            var available = process.ExitCode == 0;
            logger.LogInformation("Agent executable probe for {CommandName}: {Availability}.", command, available ? "available" : "not found");
            return available;
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "Agent executable probe for {CommandName} failed.", command);
            return false;
        }
    }
}
