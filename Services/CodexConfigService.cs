using Tomlyn;
using Tomlyn.Model;

namespace CodeBadger.Services;

public sealed record CodexMcpServer(string Name, bool Enabled);
public sealed record CodexProjectSkill(string Id, string RelativePath);

public sealed class CodexConfigService
{
    public string GetGlobalConfigPath() => Path.Combine(GetCodexHome(), "config.toml");

    public string GetProjectConfigPath(string repositoryPath) =>
        Path.Combine(repositoryPath, ".codex", "config.toml");

    public string GetSkillsDirectory(string repositoryPath) =>
        Path.Combine(repositoryPath, ".agents", "skills");

    public async Task<string> LoadProjectConfigAsync(string repositoryPath)
    {
        var path = GetProjectConfigPath(repositoryPath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }

    public Task<IReadOnlyList<CodexMcpServer>> GetMcpServersAsync(string toml) =>
        Task.FromResult<IReadOnlyList<CodexMcpServer>>(GetServers(Parse(toml)));

    public async Task<string> SetMcpServerEnabledAsync(string path, string toml, string serverName, bool enabled)
    {
        var root = Parse(toml);
        var servers = root["mcp_servers"] as TomlTable
            ?? throw new InvalidOperationException("The mcp_servers table was not found in the Codex config.");
        var server = servers[serverName] as TomlTable
            ?? throw new InvalidOperationException("The MCP server was not found in the Codex config.");
        server["enabled"] = enabled;
        var updated = TomlSerializer.Serialize(root);
        await SaveWithBackupAsync(path, updated);
        return updated;
    }

    public async Task SaveConfigAsync(string path, string toml)
    {
        _ = Parse(toml);
        await SaveWithBackupAsync(path, toml);
    }

    public async Task<IReadOnlyList<CodexMcpServer>> GetProjectMcpServersAsync(string repositoryPath, string toml) =>
        await GetMcpServersAsync(toml);

    public async Task<string> SetProjectMcpServerEnabledAsync(string repositoryPath, string toml, string name, bool enabled) =>
        await SetMcpServerEnabledAsync(GetProjectConfigPath(repositoryPath), toml, name, enabled);

    public async Task SaveProjectConfigAsync(string repositoryPath, string toml) =>
        await SaveConfigAsync(GetProjectConfigPath(repositoryPath), toml);

    public IReadOnlyList<CodexProjectSkill> GetSkills(string repositoryPath)
    {
        var root = GetSkillsDirectory(repositoryPath);
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories)
            .Select(file => new CodexProjectSkill(
                Path.GetFileName(Path.GetDirectoryName(file)!),
                Path.GetRelativePath(root, file)))
            .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> LoadSkillAsync(string repositoryPath, string relativePath)
    {
        var path = GetSkillPath(repositoryPath, relativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }

    public Task SaveSkillAsync(string repositoryPath, string relativePath, string content) =>
        SaveWithBackupAsync(GetSkillPath(repositoryPath, relativePath), content);

    public async Task SaveNewSkillAsync(string repositoryPath, string id, string content)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidOperationException("Use a skill ID containing only letters, numbers, and hyphens.");
        await SaveSkillAsync(repositoryPath, Path.Combine(id, "SKILL.md"), content);
    }

    private string GetSkillPath(string repositoryPath, string relativePath)
    {
        var root = Path.GetFullPath(GetSkillsDirectory(repositoryPath));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The skill path must be inside the project's skills directory.");
        return path;
    }

    private static IReadOnlyList<CodexMcpServer> GetServers(TomlTable root)
    {
        if (root["mcp_servers"] is not TomlTable servers)
            return [];

        return servers
            .Where(pair => pair.Value is TomlTable server &&
                (!server.TryGetValue("command", out var command) || command is string) &&
                (!server.TryGetValue("url", out var url) || url is string))
            .Select(pair => new CodexMcpServer(pair.Key,
                (pair.Value as TomlTable)?["enabled"] is not bool configured || configured))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TomlTable Parse(string toml) => string.IsNullOrWhiteSpace(toml)
        ? new TomlTable()
        : TomlSerializer.Deserialize<TomlTable>(toml)
            ?? throw new InvalidOperationException("The Codex config did not contain a TOML table.");

    private static string GetCodexHome()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(configuredHome)
            ? configuredHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static async Task SaveWithBackupAsync(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}");

        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
