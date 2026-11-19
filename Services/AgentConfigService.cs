using System.Text.Json;
using System.Text.Json.Nodes;
using CodeBadger.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CodeBadger.Services;

public sealed record AgentMcpServer(string Name, bool Enabled);

public sealed class AgentConfigService(CodexConfigService codexConfigService)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string GetConfigPath(string agentTool)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (AgentTools.Normalize(agentTool) == AgentTools.Codex)
            return codexConfigService.GetGlobalConfigPath();
        if (AgentTools.Normalize(agentTool) == AgentTools.ClaudeCode)
            return Path.Combine(home, ".claude.json");
        if (AgentTools.Normalize(agentTool) == AgentTools.Hermes)
            return Path.Combine(Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } hermesHome
                ? hermesHome
                : Path.Combine(home, ".hermes"), "config.yaml");

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
            return AgentTools.Normalize(agentTool) switch
            {
                AgentTools.Codex => string.Empty,
                AgentTools.Hermes => "# Hermes Agent configuration\n\nmcp_servers: {}\n",
                _ => "{}"
            };

        return await File.ReadAllTextAsync(path);
    }

    public async Task<IReadOnlyList<AgentMcpServer>> GetMcpServersAsync(
        string agentTool, string json, IReadOnlyCollection<RepositoryEntry> repositories)
    {
        if (AgentTools.Normalize(agentTool) == AgentTools.Codex)
            return (await codexConfigService.GetMcpServersAsync(json))
                .Select(server => new AgentMcpServer(server.Name, server.Enabled))
                .ToArray();

        if (AgentTools.Normalize(agentTool) == AgentTools.Hermes)
            return GetHermesMcpServers(json);

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
        if (AgentTools.Normalize(agentTool) == AgentTools.Codex)
            return await codexConfigService.SetMcpServerEnabledAsync(GetConfigPath(agentTool), json, serverName, enabled);

        if (AgentTools.Normalize(agentTool) == AgentTools.Hermes)
        {
            var stream = ParseYaml(json);
            var yamlRoot = stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new InvalidOperationException("The Hermes config must contain a YAML mapping at its root.");
            var yamlServers = GetHermesServersNode(yamlRoot);
            if (!yamlServers.Children.TryGetValue(new YamlScalarNode(serverName), out var serverNode))
                throw new InvalidOperationException("The MCP server was not found in the configuration.");
            if (serverNode is not YamlMappingNode server)
                throw new InvalidOperationException("The Hermes MCP server configuration must be a YAML mapping.");
            SetYamlValue(server, "enabled", enabled ? "true" : "false");
            var updatedYaml = SerializeYaml(stream);
            await SaveAsync(agentTool, updatedYaml);
            return updatedYaml;
        }

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
        if (AgentTools.Normalize(agentTool) == AgentTools.Codex)
        {
            await codexConfigService.SaveConfigAsync(GetConfigPath(agentTool), json);
            return;
        }

        if (AgentTools.Normalize(agentTool) == AgentTools.Hermes)
        {
            _ = ParseYaml(json);
            await SaveTextAsync(agentTool, json);
            return;
        }

        _ = ParseObject(json);
        await SaveTextAsync(agentTool, json);
    }

    private async Task SaveTextAsync(string agentTool, string content)
    {
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
            await File.WriteAllTextAsync(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyList<AgentMcpServer> GetHermesMcpServers(string yaml)
    {
        var stream = ParseYaml(yaml);
        if (stream.Documents[0].RootNode is not YamlMappingNode root || !root.Children.TryGetValue(new YamlScalarNode("mcp_servers"), out var serversNode))
            return [];
        if (serversNode is not YamlMappingNode servers)
            throw new InvalidOperationException("Hermes mcp_servers must be a YAML mapping.");

        return servers.Children
            .Select(pair =>
            {
                var name = (pair.Key as YamlScalarNode)?.Value
                    ?? throw new InvalidOperationException("Hermes MCP server names must be YAML strings.");
                if (pair.Value is not YamlMappingNode server)
                    throw new InvalidOperationException($"Hermes MCP server '{name}' must be a YAML mapping.");
                var enabledNode = GetYamlValue(server, "enabled");
                var enabled = !string.Equals(enabledNode?.Value, "false", StringComparison.OrdinalIgnoreCase);
                return new AgentMcpServer(name, enabled);
            })
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static YamlMappingNode GetHermesServersNode(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("mcp_servers"), out var node))
        {
            var servers = new YamlMappingNode();
            root.Add("mcp_servers", servers);
            return servers;
        }
        return node as YamlMappingNode
            ?? throw new InvalidOperationException("Hermes mcp_servers must be a YAML mapping.");
    }

    private static YamlScalarNode? GetYamlValue(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value as YamlScalarNode : null;

    private static void SetYamlValue(YamlMappingNode mapping, string key, string value)
    {
        var yamlKey = new YamlScalarNode(key);
        if (mapping.Children.ContainsKey(yamlKey))
            mapping.Children[yamlKey] = new YamlScalarNode(value);
        else
            mapping.Add(yamlKey, new YamlScalarNode(value));
    }

    private static YamlStream ParseYaml(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException($"The Hermes config is not valid YAML: {exception.Message}", exception);
        }
        if (stream.Documents.Count != 1)
            throw new InvalidOperationException("The Hermes config must contain exactly one YAML document.");
        if (stream.Documents[0].RootNode is null)
            throw new InvalidOperationException("The Hermes config cannot be empty.");
        return stream;
    }

    private static string SerializeYaml(YamlStream stream)
    {
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject
        ?? throw new JsonException("The configuration must contain a JSON object at its root.");
}
