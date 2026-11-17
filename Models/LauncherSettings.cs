namespace RepoLauncher.Models;

public sealed class LauncherSettings
{
    public string PowerShellExecutable { get; set; } = "pwsh.exe";
    public string ToolCommand { get; set; } = "opencode";
    public string AiProvider { get; set; } = "OpenAI";
    public string AiCustomUrl { get; set; } = string.Empty;
    public string AiModel { get; set; } = "gpt-4o-mini";
    public int AiMaxInputTokens { get; set; } = 12000;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AiApiKey { get; set; } = string.Empty;
    public List<RepositoryEntry> Repositories { get; set; } = [];
}

public sealed class RepositoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
