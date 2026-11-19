using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeBadger.Services;

public sealed record ClaudeProjectMcpServer(string Name, bool Enabled);

public sealed class ClaudeRepositoryConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string GetMcpConfigPath(string repositoryPath) => Path.Combine(repositoryPath, ".mcp.json");

    public string GetLocalSettingsPath(string repositoryPath) => Path.Combine(repositoryPath, ".claude", "settings.local.json");

    public async Task<string> LoadMcpConfigAsync(string repositoryPath)
    {
        var path = GetMcpConfigPath(repositoryPath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : "{\n  \"mcpServers\": {}\n}";
    }

    public async Task<IReadOnlyList<ClaudeProjectMcpServer>> GetMcpServersAsync(string repositoryPath, string json)
    {
        var root = ParseObject(json);
        var servers = root["mcpServers"] as JsonObject;
        if (servers is null)
            return [];

        var sharedSettingsPath = Path.Combine(repositoryPath, ".claude", "settings.json");
        var sharedSettings = File.Exists(sharedSettingsPath)
            ? ParseObject(await File.ReadAllTextAsync(sharedSettingsPath))
            : new JsonObject();
        var localSettings = await LoadLocalSettingsAsync(repositoryPath);
        var enabledList = ReadStringList(sharedSettings["enabledMcpjsonServers"])
            .Concat(ReadStringList(localSettings["enabledMcpjsonServers"]))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var disabledList = ReadStringList(sharedSettings["disabledMcpjsonServers"])
            .Concat(ReadStringList(localSettings["disabledMcpjsonServers"]))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return servers.Select(pair => new ClaudeProjectMcpServer(pair.Key,
                enabledList.Contains(pair.Key, StringComparer.Ordinal)
                && !disabledList.Contains(pair.Key, StringComparer.Ordinal)))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> SetMcpServerEnabledAsync(string repositoryPath, string name, bool enabled)
    {
        var settings = await LoadLocalSettingsAsync(repositoryPath);
        var enabledList = ReadStringList(settings["enabledMcpjsonServers"]);
        var disabledList = ReadStringList(settings["disabledMcpjsonServers"]);
        enabledList.RemoveAll(item => string.Equals(item, name, StringComparison.Ordinal));
        disabledList.RemoveAll(item => string.Equals(item, name, StringComparison.Ordinal));
        if (enabled)
            enabledList.Add(name);
        else
            disabledList.Add(name);

        settings["enabledMcpjsonServers"] = ToJsonArray(enabledList);
        settings["disabledMcpjsonServers"] = ToJsonArray(disabledList);
        var path = GetLocalSettingsPath(repositoryPath);
        await SaveWithBackupAsync(path, settings.ToJsonString(JsonOptions));
        return await LoadMcpConfigAsync(repositoryPath);
    }

    public async Task SaveMcpConfigAsync(string repositoryPath, string json)
    {
        var root = ParseObject(json);
        if (root["mcpServers"] is not JsonObject)
            throw new JsonException("Configuration must contain an mcpServers object.");

        await SaveWithBackupAsync(GetMcpConfigPath(repositoryPath), json);
    }

    private async Task<JsonObject> LoadLocalSettingsAsync(string repositoryPath)
    {
        var path = GetLocalSettingsPath(repositoryPath);
        if (!File.Exists(path))
            return new JsonObject();
        return ParseObject(await File.ReadAllTextAsync(path));
    }

    private async Task SaveWithBackupAsync(string path, string content)
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

    private static List<string> ReadStringList(JsonNode? node) => node is JsonArray array
        ? array.Where(item => item is JsonValue)
            .Select(item => item!.GetValue<string>())
            .ToList()
        : [];

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject
        ?? throw new JsonException("The configuration must contain a JSON object at its root.");
}
