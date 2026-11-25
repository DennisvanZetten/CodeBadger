namespace CodeBadger.Models;

using Microsoft.Extensions.Logging;

public sealed class LauncherSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "midnight";
    public bool CompletionSoundEnabled { get; set; } = true;
    public string CompletionSound { get; set; } = "bell";
    public bool QuestionSoundEnabled { get; set; } = true;
    public string QuestionSound { get; set; } = "chime";
    public int ToastDurationSeconds { get; set; } = 1;
    public double CodeBadgerChatScale { get; set; } = 1.0;
    public int LogRetentionDays { get; set; } = 30;
    public string GeneralLogLevel { get; set; } = "Warning";
    public string GitLogLevel { get; set; } = "Warning";
    public string NotificationsLogLevel { get; set; } = "Warning";
    public string AiLogLevel { get; set; } = "Warning";
    public int LoggingSettingsVersion { get; set; } = 1;
    public string GitOverviewStartSize { get; set; } = "Large";
    public string DismissedUpdateVersion { get; set; } = string.Empty;
    public string AiProvider { get; set; } = "OpenAI";
    public string AiCustomUrl { get; set; } = string.Empty;
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiConflictModel { get; set; } = "gpt-4o-mini";
    public List<CodeBadgerAgentPreferences> CodeBadgerAgentPreferences { get; set; } = CodeBadgerRoles.CreateAgentDefaults();
    public int AiMaxInputTokens { get; set; } = 12000;
    public int SandboxMaxFileMb { get; set; } = 64;
    public int SandboxMaxTotalMb { get; set; } = 1024;
    public string TaskBranchPrefix { get; set; } = "feature";
    public bool SlackNotificationsEnabled { get; set; }
    public string NotificationChannel { get; set; } = "Slack";
    public string SlackChannelId { get; set; } = string.Empty;
    public string SlackAllowedUserId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string SlackBotToken { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string SlackAppToken { get; set; } = string.Empty;
    public string DiscordChannelId { get; set; } = string.Empty;
    public string DiscordGuildId { get; set; } = string.Empty;
    public string DiscordAllowedUserId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DiscordBotToken { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AiApiKey { get; set; } = string.Empty;
    public List<RepositoryEntry> Repositories { get; set; } = [];

    public LogLevel GetLogLevel(string category) => ParseLogLevel(category switch
    {
        "Git" => GitLogLevel,
        "Notifications" => NotificationsLogLevel,
        "AI" => AiLogLevel,
        _ => GeneralLogLevel
    });

    public static string NormalizeLogLevel(string? value, string fallback = "Information") =>
        Enum.TryParse<LogLevel>(value, true, out var level)
            ? level.ToString()
            : fallback;

    private static LogLevel ParseLogLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, true, out var level) ? level : LogLevel.Information;
}

public sealed class RepositoryEntry
{
    public const string DefaultTaskStoryTemplate = "User story: As a [role], I want [goal], so that [value].\n\nContext\n- [Relevant context]\n\nAcceptance criteria\n- Given [context], when [action], then [outcome]\n\nTechnical notes\n- [Implementation considerations]\n\nOut of scope\n- [Explicit exclusions]";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string AgentTool { get; set; } = AgentTools.OpenCode;
    public bool OpenCodeSandboxEnabled { get; set; }
    public bool CodeBadgerTerminalEnabled { get; set; }
    public string CodeBadgerOrchestrationMode { get; set; } = CodeBadgerOrchestrationModes.SingleAgent;
    public string OpenCodeSandboxModel { get; set; } = "bonzai/gpt-6-luna";
    public string OpenCodeSandboxFiles { get; set; } = string.Empty;
    public bool TicketRequired { get; set; }
    public string TicketSystem { get; set; } = TicketSystems.None;
    public string TicketMcpServerName { get; set; } = string.Empty;
    public string TicketProjectKey { get; set; } = string.Empty;
    public string TaskStoryTemplate { get; set; } = DefaultTaskStoryTemplate;
    public string AiCustomInstruction { get; set; } = string.Empty;
    public bool SlackNotificationsMuted { get; set; }
    public List<string> HiddenStashCommitHashes { get; set; } = [];
    public bool ShowCommitBranchLabels { get; set; } = true;
    public bool ShowCommitGraph { get; set; } = true;
    public bool ShowCommitAuthor { get; set; } = true;
    public bool ShowCommitDate { get; set; } = true;
    public bool ShowCommitSha { get; set; } = true;
    public bool AutoSyncSubmodules { get; set; }
}

public static class CodeBadgerOrchestrationModes
{
    public const string SingleAgent = "SingleAgent";
    public const string PlanBuild = "PlanBuild";
    public const string Full = "Full";

    public static string Normalize(string? value) => value switch
    {
        PlanBuild => PlanBuild,
        Full => Full,
        _ => SingleAgent
    };
}

public static class CodeBadgerRoles
{
    public const string Orchestrator = "Orchestrator";
    public const string Router = "Router";
    public const string Planner = "Planner";
    public const string Worker = "Worker";
    public const string Rag = "RAG";
    public const string Critic = "Critic";

    public static readonly string[] All = [Orchestrator, Router, Planner, Worker, Rag, Critic];
    public static readonly string[] AgentTypes = ["Primary", "Plan", "Build", "Custom"];

    public static List<CodeBadgerRolePreference> CreateDefaults() =>
        All.Select(role => new CodeBadgerRolePreference { Role = role, AgentType = DefaultAgentType(role) }).ToList();

    public static List<CodeBadgerAgentPreferences> CreateAgentDefaults() =>
        new[] { AgentTools.OpenCode, AgentTools.ClaudeCode, AgentTools.Hermes, AgentTools.Codex }
            .Select(agent => new CodeBadgerAgentPreferences { AgentTool = agent, RolePreferences = CreateDefaults() }).ToList();

    public static void NormalizeAgentPreferences(List<CodeBadgerAgentPreferences> preferences)
    {
        var existing = (preferences ?? []).Where(item => AgentTools.All.Contains(item.AgentTool, StringComparer.Ordinal))
            .GroupBy(item => item.AgentTool, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        preferences?.Clear();
        foreach (var agent in AgentTools.All)
        {
            if (!existing.TryGetValue(agent, out var preference))
                preference = new CodeBadgerAgentPreferences { AgentTool = agent, RolePreferences = CreateDefaults() };
            preference.TerminalModel = preference.TerminalModel?.Trim() ?? string.Empty;
            preference.SingleAgentPreferredModels = NormalizePreferredModels(preference.SingleAgentPreferredModels);
            preference.PlanPreferredModels = NormalizePreferredModels(preference.PlanPreferredModels);
            preference.BuildPreferredModels = NormalizePreferredModels(preference.BuildPreferredModels);
            preference.RolePreferences ??= CreateDefaults();
            Normalize(preference.RolePreferences);
            preferences!.Add(preference);
        }
    }

    public static void Normalize(List<CodeBadgerRolePreference> preferences)
    {
        preferences ??= [];
        var normalized = preferences.Where(preference => All.Contains(preference.Role, StringComparer.Ordinal))
            .GroupBy(preference => preference.Role, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        preferences.Clear();
        foreach (var role in All)
        {
            if (!normalized.TryGetValue(role, out var preference))
                preference = new CodeBadgerRolePreference { Role = role, AgentType = DefaultAgentType(role) };
            preference.AgentType = string.IsNullOrWhiteSpace(preference.AgentType) ? DefaultAgentType(role) : preference.AgentType.Trim();
            preference.CustomAgentId ??= string.Empty;
            preference.PreferredModels ??= [];
            var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            preference.PreferredModels = preference.PreferredModels
                .Select(model => string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim())
                .Select(model => model.Length == 0 || seenModels.Add(model) ? model : string.Empty)
                .ToList();
            while (preference.PreferredModels.Count > 0 && string.IsNullOrEmpty(preference.PreferredModels[^1]))
                preference.PreferredModels.RemoveAt(preference.PreferredModels.Count - 1);
            preferences.Add(preference);
        }
    }

    public static List<string> NormalizePreferredModels(IEnumerable<string>? preferredModels)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = (preferredModels ?? [])
            .Select(model => string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim())
            .Select(model => model.Length == 0 || seen.Add(model) ? model : string.Empty)
            .ToList();
        while (normalized.Count > 0 && string.IsNullOrEmpty(normalized[^1]))
            normalized.RemoveAt(normalized.Count - 1);
        return normalized;
    }

    public static string DefaultAgentType(string role) => role switch
    {
        Orchestrator or Router or Planner => "Plan",
        _ => "Build"
    };
}

public sealed class CodeBadgerRolePreference
{
    public string Role { get; set; } = CodeBadgerRoles.Worker;
    public string AgentType { get; set; } = "Build";
    public string CustomAgentId { get; set; } = string.Empty;
    public List<string> PreferredModels { get; set; } = [];
}

public sealed class CodeBadgerAgentPreferences
{
    public string AgentTool { get; set; } = AgentTools.OpenCode;
    public string TerminalModel { get; set; } = string.Empty;
    public List<string> SingleAgentPreferredModels { get; set; } = [];
    public List<string> PlanPreferredModels { get; set; } = [];
    public List<string> BuildPreferredModels { get; set; } = [];
    public List<CodeBadgerRolePreference> RolePreferences { get; set; } = CodeBadgerRoles.CreateDefaults();
}

public sealed record CodeBadgerRecommendedModel(string Reference, decimal InputCost, decimal OutputCost, bool IsUnpriced = false);
public sealed record CodeBadgerRoleRecommendation(string RoleName, IReadOnlyList<CodeBadgerRecommendedModel> Models);
public sealed record CodeBadgerModelRecommendation(string SetName, IReadOnlyList<CodeBadgerRoleRecommendation> Roles);
public sealed record CodeBadgerRecommendationOptions(decimal CostCeiling, bool AllowFreeModels, bool Confirmed);

public sealed class CodeBadgerTerminalState
{
    public Guid RepositoryId { get; set; }
    public Guid SelectedSessionId { get; set; }
    public string SelectedOpenCodeSessionId { get; set; } = string.Empty;
    public List<CodeBadgerTerminalSessionState> Sessions { get; set; } = [];
}

public sealed class CodeBadgerTerminalSessionState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Mode { get; set; } = "Plan";
    public string ActiveAgentRole { get; set; } = string.Empty;
    public Dictionary<string, string> AgentSessionIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> AgentSessionGenerations { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AgentContextTokens { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AgentContextModels { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RoleModelOverrides { get; set; } = new(StringComparer.Ordinal);
    public List<string> RecentModels { get; set; } = [];
    public List<CodeBadgerTerminalMessage> Messages { get; set; } = [];
}

public sealed class CodeBadgerTerminalMessage
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    public List<string> Images { get; set; } = [];
    public string AgentRole { get; set; } = string.Empty;
    public string ModelReference { get; set; } = string.Empty;
    public bool IsCodeBadgerMessage { get; set; }
}

public static class TicketSystems
{
    public const string None = "None";
    public const string Jira = "Jira";
    public const string GitHub = "GitHub Issues";

    public static string Normalize(string? ticketSystem) => ticketSystem switch
    {
        Jira => Jira,
        GitHub => GitHub,
        _ => None
    };
}

public static class AgentTools
{
    public const string OpenCode = "OpenCode";
    public const string ClaudeCode = "Claude Code";
    public const string Codex = "Codex";
    public const string Hermes = "Hermes";
    public static readonly string[] All = [OpenCode, ClaudeCode, Hermes, Codex];

    public static string Normalize(string? agentTool) => agentTool switch
    {
        ClaudeCode => ClaudeCode,
        Codex => Codex,
        Hermes => Hermes,
        _ => OpenCode
    };

    public static string GetCommand(string? agentTool) => Normalize(agentTool) switch
    {
        ClaudeCode => "claude",
        Codex => "codex",
        Hermes => "hermes",
        _ => "opencode"
    };

    public static string GetDisplayName(string? agentTool) => Normalize(agentTool) switch
    {
        ClaudeCode => "Claude Code",
        Codex => "Codex",
        Hermes => "Hermes",
        _ => "OpenCode"
    };
}
