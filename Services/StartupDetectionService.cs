using System.Diagnostics;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed record AgentStartupState(bool Installed, bool HasGlobalConfig, string ConfigPath);

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
    LocalizationService localization)
{
    public StartupDetectionResult? Result { get; private set; }

    public async Task<StartupDetectionResult> DetectAsync(
        LauncherSettings settings,
        Func<string, double, Task> reportProgress)
    {
        var repositories = settings.Repositories;
        var totalSteps = 4 + repositories.Count * 2;
        var completedSteps = 0;

        async Task Report(string stage)
        {
            completedSteps++;
            await reportProgress(stage, completedSteps * 100d / totalSteps);
        }

        await reportProgress(localization.T("Detecting OpenCode..."), 0);
        var openCodePath = GetExistingGlobalConfigPath(AgentTools.OpenCode);
        var openCode = new AgentStartupState(await IsCommandAvailableAsync("opencode"), File.Exists(openCodePath), openCodePath);
        await Report(localization.T("Detecting Claude Code..."));

        var claudePath = GetExistingGlobalConfigPath(AgentTools.ClaudeCode);
        var claude = new AgentStartupState(await IsCommandAvailableAsync("claude"), File.Exists(claudePath), claudePath);
        await Report(localization.T("Detecting Codex..."));

        var codexPath = GetExistingGlobalConfigPath(AgentTools.Codex);
        var codex = new AgentStartupState(await IsCommandAvailableAsync("codex"), File.Exists(codexPath), codexPath);
        await Report(localization.T("Detecting Hermes..."));

        var hermesPath = GetExistingGlobalConfigPath(AgentTools.Hermes);
        var hermes = new AgentStartupState(await IsCommandAvailableAsync("hermes"), File.Exists(hermesPath), hermesPath);
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
            var gitStatus = await gitBranchService.GetRepositoryStatusAsync(repository.Path);
            repositoryStates[repository.Id] = new RepositoryStartupState(hasOpenCodeSettings, hasClaudeSettings, hasCodexSettings, gitStatus.IsValid);
            await Report(localization.T("Checking repository Git status..."));
        }

        Result = new StartupDetectionResult(openCode, claude, codex, hermes, repositoryStates);
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

    private static async Task<bool> IsCommandAvailableAsync(string command)
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
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
