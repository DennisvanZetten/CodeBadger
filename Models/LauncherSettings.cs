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
    public string DismissedUpdateVersion { get; set; } = string.Empty;
    public string AiProvider { get; set; } = "OpenAI";
    public string AiCustomUrl { get; set; } = string.Empty;
    public string AiModel { get; set; } = "gpt-4o-mini";
    public int AiMaxInputTokens { get; set; } = 12000;
    public string AiCustomInstruction { get; set; } = string.Empty;
    public int SandboxMaxFileMb { get; set; } = 64;
    public int SandboxMaxTotalMb { get; set; } = 1024;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AiApiKey { get; set; } = string.Empty;
    public List<RepositoryEntry> Repositories { get; set; } = [];
}

public sealed class RepositoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string AgentTool { get; set; } = AgentTools.OpenCode;
    public bool OpenCodeSandboxEnabled { get; set; }
    public string OpenCodeSandboxModel { get; set; } = "bonzai/gpt-6-luna";
    public string OpenCodeSandboxFiles { get; set; } = string.Empty;
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
