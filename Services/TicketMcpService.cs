using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed class TicketMcpService
{
    private readonly AgentConfigService _agentConfig;

    public TicketMcpService(AgentConfigService agentConfig) => _agentConfig = agentConfig;

    public async Task<string> CreateTicketAsync(RepositoryEntry repository, IdeaType itemType, string title, string description,
        CancellationToken cancellationToken = default)
    {
        if (TicketSystems.Normalize(repository.TicketSystem) == TicketSystems.None ||
            string.IsNullOrWhiteSpace(repository.TicketMcpServerName))
            throw new InvalidOperationException("Configure a ticket system and MCP server for this repository first.");
        if (TicketSystems.Normalize(repository.TicketSystem) == TicketSystems.Jira &&
            string.IsNullOrWhiteSpace(repository.TicketProjectKey))
            throw new InvalidOperationException("Configure the Jira project key for this repository before creating a ticket.");
        var agentTool = AgentTools.Normalize(repository.AgentTool);
        JsonObject? server;
        try
        {
            server = await _agentConfig.GetRepositoryMcpServerAsync(agentTool, repository, repository.TicketMcpServerName);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not load the effective MCP configuration for {AgentTools.GetDisplayName(agentTool)}.", exception);
        }
        if (server is null)
            throw new InvalidOperationException($"The selected ticket MCP server '{repository.TicketMcpServerName}' was not found in the global or repository-specific {AgentTools.GetDisplayName(agentTool)} configuration.");
        if (server["disabled"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"The selected ticket MCP server '{repository.TicketMcpServerName}' is disabled.");
        if (server["url"] is JsonValue)
            throw new InvalidOperationException("Ticket creation through remote HTTP MCP servers is not supported yet; configure a local stdio MCP server.");
        if (server["command"] is not JsonArray command || command.Count == 0 || command[0]?.GetValue<string>() is not { Length: > 0 } executable)
            throw new InvalidOperationException("Ticket creation currently requires a local stdio MCP server with a command.");

        JsonObject? toolResult;
        try
        {
            toolResult = await CallServerAsync(repository.Path, command, server, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not start MCP server '{repository.TicketMcpServerName}' or list its tools: {exception.Message}", exception);
        }
        var tools = toolResult?[("tools")] as JsonArray ??
            throw new InvalidOperationException("The ticket MCP server did not return a tool list.");
        var availableTools = tools.OfType<JsonObject>()
            .Select(tool => (Tool: tool, Name: tool["name"]?.GetValue<string>() ?? string.Empty,
                Description: tool["description"]?.GetValue<string>() ?? string.Empty))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .ToArray();
        var createTool = availableTools
            .Where(candidate => IsIssueCreationTool(candidate.Name, candidate.Description))
            .OrderBy(candidate => candidate.Name.Contains("issue", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (createTool.Tool is null)
            throw new InvalidOperationException(
                $"No Jira issue-creation tool was recognized on MCP server '{repository.TicketMcpServerName}'. " +
                $"Available tools: {string.Join("; ", availableTools.Select(candidate =>
                    string.IsNullOrWhiteSpace(candidate.Description)
                        ? candidate.Name
                        : $"{candidate.Name}: {candidate.Description}"))}.");

        var arguments = BuildArguments(createTool.Tool["inputSchema"] as JsonObject, title, description,
            repository.TicketProjectKey, GetJiraIssueType(itemType));
        JsonObject? callResult;
        try
        {
            callResult = await CallServerAsync(repository.Path, command, server, cancellationToken,
                createTool.Name, arguments);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"MCP server '{repository.TicketMcpServerName}' failed while calling tool '{createTool.Name}': {exception.Message}", exception);
        }
        var text = callResult?.ToJsonString() ?? string.Empty;
        var ticketNumber = Regex.Match(text, @"\b[A-Z][A-Z0-9_]+-\d+\b|#\d+", RegexOptions.IgnoreCase).Value;
        if (string.IsNullOrWhiteSpace(ticketNumber))
            throw new InvalidOperationException("The ticket tool returned, but no ticket number could be identified. Response: " + text);
        return ticketNumber;
    }

    private static string GetJiraIssueType(IdeaType itemType) => itemType switch
    {
        IdeaType.Bug => "Bug",
        IdeaType.Task => "Task",
        _ => "Story"
    };

    private static bool IsIssueCreationTool(string name, string description)
    {
        var normalizedName = name.Replace('_', '-').Replace('.', '-');
        var isCreateOperation = normalizedName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.Contains("add", StringComparison.OrdinalIgnoreCase);
        var isIssueOperation = normalizedName.Contains("issue", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.Contains("ticket", StringComparison.OrdinalIgnoreCase) ||
            normalizedName.Contains("work-item", StringComparison.OrdinalIgnoreCase);
        if (isCreateOperation && isIssueOperation)
            return true;

        var lowerDescription = description.ToLowerInvariant();
        return (lowerDescription.Contains("create") || lowerDescription.Contains("create a") || lowerDescription.Contains("create an")) &&
            (lowerDescription.Contains("issue") || lowerDescription.Contains("ticket"));
    }

    private static JsonObject BuildArguments(JsonObject? schema, string title, string description,
        string projectKey, string issueType)
    {
        var properties = schema?["properties"] as JsonObject ?? new JsonObject();
        var args = new JsonObject();
        foreach (var (name, node) in properties)
        {
            var normalized = name.ToLowerInvariant();
            if (normalized.Contains("title") || normalized is "summary" or "name")
                args[name] = title;
            else if (normalized.Contains("description") || normalized is "body" or "details")
                args[name] = description;
            else if (normalized is "project" or "projectkey" or "project_key" or "project-key")
                args[name] = projectKey;
            else if (normalized is "issue_type" or "issuetype" or "issue-type" or "type")
                args[name] = issueType;
        }

        if (schema?["required"] is JsonArray required)
        {
            var missing = required.Select(value => value?.GetValue<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name) && !args.ContainsKey(name))
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"The ticket tool requires additional fields that CodeBadger cannot infer: {string.Join(", ", missing)}.");
        }
        return args;
    }

    private static async Task<JsonObject?> CallServerAsync(string workingDirectory, JsonArray command, JsonObject server,
        CancellationToken cancellationToken, string? toolName = null, JsonObject? arguments = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command[0]!.GetValue<string>(), WorkingDirectory = workingDirectory,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in command.Skip(1))
            startInfo.ArgumentList.Add(arg?.GetValue<string>() ?? string.Empty);
        if (server["environment"] is JsonObject environment)
            foreach (var (name, value) in environment)
                if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                    startInfo.Environment[name] = text;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the ticket MCP server.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await WriteMessageAsync(process, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "CodeBadger", version = "1.0" }
            } }, timeout.Token);
            _ = await ReadResponseAsync(process, 1, timeout.Token);
            await WriteMessageAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, timeout.Token);
            if (toolName is null)
            {
                await WriteMessageAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, timeout.Token);
                return await ReadResponseAsync(process, 2, timeout.Token);
            }

            await WriteMessageAsync(process, new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = toolName, arguments } }, timeout.Token);
            return await ReadResponseAsync(process, 3, timeout.Token);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static async Task WriteMessageAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonObject?> ReadResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new InvalidOperationException("The ticket MCP server closed its output before responding.");
            if (JsonNode.Parse(line) is not JsonObject response || response["id"]?.GetValue<int>() != id)
                continue;
            if (response["error"] is JsonObject error)
                throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "The ticket MCP call failed.");
            return response["result"] as JsonObject;
        }
    }
}
