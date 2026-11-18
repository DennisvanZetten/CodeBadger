namespace RepoLauncher.Models;

public sealed class LauncherSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "midnight";
    public string ToolCommand { get; set; } = "opencode";
    public string AiProvider { get; set; } = "OpenAI";
    public string AiCustomUrl { get; set; } = string.Empty;
    public string AiModel { get; set; } = "gpt-4o-mini";
    public int AiMaxInputTokens { get; set; } = 12000;
    public string AiCustomInstruction { get; set; } = string.Empty;
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
}
