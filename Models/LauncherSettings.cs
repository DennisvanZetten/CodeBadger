namespace CodeBadger.Models;

public sealed class LauncherSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "midnight";
    public bool CompletionSoundEnabled { get; set; } = true;
    public string CompletionSound { get; set; } = "bell";
    public bool QuestionSoundEnabled { get; set; } = true;
    public string QuestionSound { get; set; } = "chime";
    public int ToastDurationSeconds { get; set; } = 1;
    public string GitOverviewStartSize { get; set; } = "Large";
    public string DismissedUpdateVersion { get; set; } = string.Empty;
    public string AiProvider { get; set; } = "OpenAI";
    public string AiCustomUrl { get; set; } = string.Empty;
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiConflictModel { get; set; } = "gpt-4o-mini";
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
