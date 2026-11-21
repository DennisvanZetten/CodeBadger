using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeBadger.Services;

public sealed record OpenCodeProjectMcpServer(string Name, bool Enabled);
public sealed record OpenCodeProjectSkill(string Id, string RelativePath);

public sealed class OpenCodeRepositoryConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string GetProjectConfigPath(string repositoryPath)
    {
        var candidates = new[]
        {
            Path.Combine(repositoryPath, ".opencode", "opencode.jsonc"),
            Path.Combine(repositoryPath, ".opencode", "opencode.json"),
            Path.Combine(repositoryPath, "opencode.jsonc"),
            Path.Combine(repositoryPath, "opencode.json")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[3];
    }

    public async Task<string> LoadProjectConfigAsync(string repositoryPath)
    {
        var path = GetProjectConfigPath(repositoryPath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : "{}";
    }

    public async Task<IReadOnlyList<OpenCodeProjectMcpServer>> GetMcpServersAsync(string json)
    {
        var root = ParseObject(json);
        var servers = (root["mcp"] as JsonObject)?["servers"] as JsonObject;
        if (servers is null)
            return [];

        return servers.Select(pair => new OpenCodeProjectMcpServer(pair.Key,
                !(pair.Value is JsonObject server && server["disabled"] is JsonValue disabledValue
                  && disabledValue.TryGetValue<bool>(out var disabled) && disabled)))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> SetMcpServerEnabledAsync(string repositoryPath, string json, string name, bool enabled)
    {
        var root = ParseObject(json);
        var servers = (root["mcp"] as JsonObject)?["servers"] as JsonObject
            ?? throw new InvalidOperationException("The MCP server configuration was not found.");
        if (servers[name] is not JsonObject server)
            throw new InvalidOperationException("The MCP server was not found in the project configuration.");

        if (enabled)
            server.Remove("disabled");
        else
            server["disabled"] = true;

        var updated = root.ToJsonString(JsonOptions);
        await SaveProjectConfigAsync(repositoryPath, updated);
        return updated;
    }

    public async Task SaveProjectConfigAsync(string repositoryPath, string json)
    {
        _ = ParseObject(json);
        await SaveWithBackupAsync(GetProjectConfigPath(repositoryPath), json);
    }

    public string GetSkillsDirectory(string repositoryPath) => Path.Combine(repositoryPath, ".opencode", "skills");

    public Task<IReadOnlyList<OpenCodeProjectSkill>> GetSkillsAsync(string repositoryPath)
    {
        var root = GetSkillsDirectory(repositoryPath);
        if (!Directory.Exists(root))
            return Task.FromResult<IReadOnlyList<OpenCodeProjectSkill>>([]);

        var skills = new Dictionary<string, OpenCodeProjectSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith('.') || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
                continue;

            var id = Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(file)!)
                : Path.GetFileNameWithoutExtension(file);
            skills[id] = new OpenCodeProjectSkill(id, relative);
        }

        return Task.FromResult<IReadOnlyList<OpenCodeProjectSkill>>(
            skills.Values.OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public string GetSkillPath(string repositoryPath, string relativePath)
    {
        var root = Path.GetFullPath(GetSkillsDirectory(repositoryPath));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The skill path must be inside the project's skills directory.");
        return path;
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

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject
        ?? throw new JsonException("The configuration must contain a JSON object at its root.");
}
