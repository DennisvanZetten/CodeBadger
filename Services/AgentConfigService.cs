using System.Text.Json;
using System.Text.Json.Nodes;
using RepoLauncher.Models;

namespace RepoLauncher.Services;

public sealed record AgentMcpServer(string Name, bool Enabled);

public sealed class AgentConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string GetConfigPath(string agentTool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (AgentTools.Normalize(agentTool) == AgentTools.ClaudeCode)
            return Path.Combine(home, ".claude.json");

        var configDirectory = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configHome
            ? configHome
            : Path.Combine(home, ".config"), "opencode");
        var jsoncPath = Path.Combine(configDirectory, "opencode.jsonc");
        return File.Exists(jsoncPath) ? jsoncPath : Path.Combine(configDirectory, "opencode.json");
    }

    public async Task<string> LoadAsync(string agentTool)
    {
        var path = GetConfigPath(agentTool);
        if (!File.Exists(path))
            return "{}";

        return await File.ReadAllTextAsync(path);
    }

    public async Task<IReadOnlyList<AgentMcpServer>> GetMcpServersAsync(
        string agentTool, string json, IReadOnlyCollection<RepositoryEntry> repositories)
    {
        var root = ParseObject(json);
        var isClaude = AgentTools.Normalize(agentTool) == AgentTools.ClaudeCode;
        var serverConfig = isClaude
            ? root["mcpServers"] as JsonObject
            : (root["mcp"] as JsonObject)?["servers"] as JsonObject;
        if (serverConfig is null)
            return [];

        var repositoryPaths = repositories
            .Where(repository => !string.IsNullOrWhiteSpace(repository.Path))
            .Select(repository => OpenCodeActivityService.NormalizePath(repository.Path))
            .Where(repositoryPath => repositoryPath is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var projectConfigs = root["projects"] as JsonObject;

        return serverConfig.Select(pair =>
        {
            var enabled = true;
            if (isClaude && repositoryPaths.Length > 0)
            {
                enabled = repositoryPaths.All(repositoryPath =>
                    !(projectConfigs?[repositoryPath]?["disabledMcpServers"] is JsonArray disabled)
                    || !disabled.Any(item => string.Equals(item?.GetValue<string>(), pair.Key, StringComparison.Ordinal)));
            }
            else if (!isClaude && pair.Value is JsonObject server && server["disabled"] is JsonValue disabledValue
                     && disabledValue.TryGetValue<bool>(out var configuredDisabled))
            {
                enabled = !configuredDisabled;
            }

            return new AgentMcpServer(pair.Key, enabled);
        }).OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> SetMcpServerEnabledAsync(
        string agentTool, string json, string serverName, bool enabled,
        IReadOnlyCollection<RepositoryEntry> repositories)
    {
        var root = ParseObject(json);
        var isClaude = AgentTools.Normalize(agentTool) == AgentTools.ClaudeCode;
        var servers = isClaude
            ? root["mcpServers"] as JsonObject
            : (root["mcp"] as JsonObject)?["servers"] as JsonObject;
        if (servers is null)
            throw new InvalidOperationException("The MCP server configuration was not found.");
        if (!servers.ContainsKey(serverName))
            throw new InvalidOperationException("The MCP server was not found in the configuration.");

        if (isClaude)
        {
            var projectPaths = repositories
                .Where(repository => !string.IsNullOrWhiteSpace(repository.Path))
                .Select(repository => OpenCodeActivityService.NormalizePath(repository.Path))
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (projectPaths.Length == 0)
                throw new InvalidOperationException("Add at least one repository with a valid path to control Claude Code MCP servers.");

            var projects = root["projects"] as JsonObject;
            if (projects is null)
            {
                projects = new JsonObject();
                root["projects"] = projects;
            }
            foreach (var projectPath in projectPaths)
            {
                var project = projects[projectPath] as JsonObject;
                if (project is null)
                {
                    project = new JsonObject();
                    projects[projectPath] = project;
                }
                var disabled = project["disabledMcpServers"] as JsonArray;
                if (disabled is null)
                {
                    disabled = new JsonArray();
                    project["disabledMcpServers"] = disabled;
                }
                for (var index = disabled.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(disabled[index]?.GetValue<string>(), serverName, StringComparison.Ordinal))
                        disabled.RemoveAt(index);
                }
                if (!enabled)
                    disabled.Add(serverName);
            }
        }
        else
        {
            var server = servers[serverName] as JsonObject
                ?? throw new InvalidOperationException("Only object-based OpenCode MCP configurations can be toggled.");
            server["disabled"] = !enabled;
        }

        var updatedJson = root.ToJsonString(JsonOptions);
        await SaveAsync(agentTool, updatedJson);
        return updatedJson;
    }

    public async Task SaveAsync(string agentTool, string json)
    {
        _ = ParseObject(json);
        var path = GetConfigPath(agentTool);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            var backupPath = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            File.Copy(path, backupPath);
        }
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json);
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
