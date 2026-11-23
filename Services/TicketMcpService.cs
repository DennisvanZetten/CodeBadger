using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed class TicketMcpService
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly AgentConfigService _agentConfig;
    private readonly ILogger<TicketMcpService> _logger;

    public TicketMcpService(AgentConfigService agentConfig, ILogger<TicketMcpService> logger)
    {
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public async Task<string> CreateTicketAsync(RepositoryEntry repository, IdeaType itemType, string title, string description,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _logger.LogInformation("Creating {TicketType} through MCP server {McpServer} for repository {RepositoryName}; ticket text omitted.",
            itemType, repository.TicketMcpServerName, repository.Name);
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
            _logger.LogWarning("Could not load ticket MCP configuration for repository {RepositoryName} ({ExceptionType}).",
                repository.Name, exception.GetType().Name);
            throw new InvalidOperationException($"Could not load the effective MCP configuration for {AgentTools.GetDisplayName(agentTool)}.", exception);
        }
        if (server is null)
            throw new InvalidOperationException($"The selected ticket MCP server '{repository.TicketMcpServerName}' was not found in the global or repository-specific {AgentTools.GetDisplayName(agentTool)} configuration.");
        if (server["disabled"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"The selected ticket MCP server '{repository.TicketMcpServerName}' is disabled.");
        var hasUrl = server["url"] is JsonValue;
        var hasCommand = server["command"] is JsonArray;
        if (hasUrl == hasCommand)
            throw new InvalidOperationException("The selected MCP server must define exactly one transport: a remote 'url' or a local 'command'.");

        JsonObject? toolResult;
        JsonArray? command = null;
        string? endpoint = null;
        try
        {
            if (hasUrl)
            {
                endpoint = server["url"]?.GetValue<string>();
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException("The remote MCP server URL must be an absolute HTTP or HTTPS URL.");
                toolResult = await CallRemoteServerAsync(uri, server, cancellationToken);
            }
            else
            {
                command = server["command"] as JsonArray;
                if (command is null || command.Count == 0 || string.IsNullOrWhiteSpace(command[0]?.GetValue<string>()))
                    throw new InvalidOperationException("The local MCP server must define a command.");
                toolResult = await CallServerAsync(repository.Path, command, server, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Could not connect to ticket MCP server {McpServer} ({ExceptionType}).",
                repository.TicketMcpServerName, exception.GetType().Name);
            throw new InvalidOperationException($"Could not connect to MCP server '{repository.TicketMcpServerName}' or list its tools: {exception.Message}", exception);
        }
        var tools = toolResult?[("tools")] as JsonArray ??
            throw new InvalidOperationException("The ticket MCP server did not return a tool list.");
        var availableTools = tools.OfType<JsonObject>()
            .Select(tool => (Tool: tool, Name: tool["name"]?.GetValue<string>() ?? string.Empty,
                Description: tool["description"]?.GetValue<string>() ?? string.Empty))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .ToArray();
        var isGitHub = TicketSystems.Normalize(repository.TicketSystem) == TicketSystems.GitHub;
        var createTool = availableTools
            .Where(candidate => IsIssueCreationTool(candidate.Name, candidate.Description) &&
                (!isGitHub || !RequiresExistingIssueNumber(candidate.Tool["inputSchema"] as JsonObject)))
            .OrderBy(candidate => candidate.Name.Contains("issue", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (createTool.Tool is null)
            throw new InvalidOperationException(
                $"No issue-creation tool that can create a new issue was recognized on MCP server '{repository.TicketMcpServerName}'. " +
                "Tools requiring an issue_number are treated as tools for existing issues and are skipped. " +
                $"Available tools: {string.Join("; ", availableTools.Select(candidate =>
                    string.IsNullOrWhiteSpace(candidate.Description)
                        ? candidate.Name
                        : $"{candidate.Name}: {candidate.Description}"))}.");

        var githubRepository = isGitHub
            ? await GetGitHubRepositoryAsync(repository.Path, cancellationToken)
            : (Owner: string.Empty, Name: string.Empty);
        var arguments = BuildArguments(createTool.Tool["inputSchema"] as JsonObject, title, description,
            repository.TicketProjectKey, GetJiraIssueType(itemType), githubRepository.Owner, githubRepository.Name, isGitHub);
        JsonObject? callResult;
        try
        {
            callResult = endpoint is not null
                ? await CallRemoteServerAsync(new Uri(endpoint), server, cancellationToken, createTool.Name, arguments)
                : await CallServerAsync(repository.Path, command!, server, cancellationToken, createTool.Name, arguments);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("MCP ticket-creation tool {ToolName} failed on server {McpServer} ({ExceptionType}).",
                createTool.Name, repository.TicketMcpServerName, exception.GetType().Name);
            throw new InvalidOperationException($"MCP server '{repository.TicketMcpServerName}' failed while calling tool '{createTool.Name}': {exception.Message}", exception);
        }
        if (callResult?["isError"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"MCP server '{repository.TicketMcpServerName}' returned an error: {callResult.ToJsonString()}");
        var text = callResult?.ToJsonString() ?? string.Empty;
        var ticketMatch = Regex.Match(text,
            @"\b[A-Z][A-Z0-9_]+-\d+\b|#\d+|/issues/(\d+)\b|""number""\s*:\s*(\d+)",
            RegexOptions.IgnoreCase);
        var ticketNumber = ticketMatch.Success
            ? ticketMatch.Value.StartsWith("/issues/", StringComparison.OrdinalIgnoreCase)
                ? "#" + ticketMatch.Groups[1].Value
                : ticketMatch.Value.StartsWith("\"number\"", StringComparison.OrdinalIgnoreCase)
                    ? "#" + ticketMatch.Groups[2].Value
                    : ticketMatch.Value
            : string.Empty;
        if (string.IsNullOrWhiteSpace(ticketNumber))
            throw new InvalidOperationException("The ticket tool returned, but no ticket number could be identified. Response: " + text);
        _logger.LogInformation("Created ticket {TicketNumber} through MCP server {McpServer} in {ElapsedMilliseconds} ms.",
            ticketNumber, repository.TicketMcpServerName, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
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

    private static bool RequiresExistingIssueNumber(JsonObject? schema) =>
        schema?["required"] is JsonArray required && required
            .Select(value => value?.GetValue<string>())
            .Any(name => string.Equals(name, "issue_number", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, "issue_id", StringComparison.OrdinalIgnoreCase)) == true;

    private static JsonObject BuildArguments(JsonObject? schema, string title, string description,
        string projectKey, string issueType, string githubOwner, string githubRepository, bool isGitHub)
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
            else if (!isGitHub && (normalized is "issue_type" or "issuetype" or "issue-type" or "type"))
                args[name] = issueType;
            else if (normalized is "owner" or "repository_owner" or "repositoryowner")
                args[name] = githubOwner;
            else if (normalized is "repo" or "repository" or "repository_name" or "repositoryname")
                args[name] = githubRepository;
            else if ((normalized is "method" or "action") && githubRepository.Length > 0)
                args[name] = "create";
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

    private static async Task<(string Owner, string Name)> GetGitHubRepositoryAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = path, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("remote");
        startInfo.ArgumentList.Add("get-url");
        startInfo.ArgumentList.Add("origin");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not inspect the repository's origin remote.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Could not determine the GitHub repository from the origin remote: " + error.Trim());

        var remote = output.Trim();
        var match = Regex.Match(remote, @"(?:https?://|ssh://(?:[^@/]+@)?|[^@]+@)[^/:]+[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException("The origin remote is not a recognized GitHub repository URL. Configure it as https://github.com/owner/repository.git or git@github.com:owner/repository.git.");
        return (match.Groups["owner"].Value, match.Groups["repo"].Value);
    }

    private static async Task<JsonObject?> CallRemoteServerAsync(Uri endpoint, JsonObject server,
        CancellationToken cancellationToken, string? toolName = null, JsonObject? arguments = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (server["headers"] is JsonObject configuredHeaders)
        {
            foreach (var (name, node) in configuredHeaders)
            {
                var value = node?.GetValue<string>();
                if (value is not null)
                    headers[name] = Regex.Replace(value, @"\{env:([^}]+)\}", match =>
                        Environment.GetEnvironmentVariable(match.Groups[1].Value)
                        ?? throw new InvalidOperationException($"Environment variable '{match.Groups[1].Value}' referenced by MCP header '{name}' is not set."));
            }
        }

        string? sessionId = null;
        string protocolVersion = "2025-03-26";
        var initialize = await SendRemoteRequestAsync(endpoint, headers, null, protocolVersion,
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            {
                protocolVersion, capabilities = new { }, clientInfo = new { name = "CodeBadger", version = "1.0" }
            } }, 1, cancellationToken);
        sessionId = initialize.SessionId;
        if (initialize.Result?["protocolVersion"]?.GetValue<string>() is { Length: > 0 } negotiatedVersion)
            protocolVersion = negotiatedVersion;

        await SendRemoteNotificationAsync(endpoint, headers, sessionId, protocolVersion,
            new { jsonrpc = "2.0", method = "notifications/initialized" }, cancellationToken);

        var requestId = 2;
        if (toolName is null)
        {
            var response = await SendRemoteRequestAsync(endpoint, headers, sessionId, protocolVersion,
                new { jsonrpc = "2.0", id = requestId, method = "tools/list", @params = new { } }, requestId, cancellationToken);
            return response.Result;
        }

        var call = await SendRemoteRequestAsync(endpoint, headers, sessionId, protocolVersion,
            new { jsonrpc = "2.0", id = requestId, method = "tools/call", @params = new { name = toolName, arguments } }, requestId, cancellationToken);
        return call.Result;
    }

    private static async Task<(JsonObject? Result, string? SessionId)> SendRemoteRequestAsync(Uri endpoint,
        IReadOnlyDictionary<string, string> headers, string? sessionId, string protocolVersion, object message,
        int id, CancellationToken cancellationToken)
    {
        using var request = CreateRemoteRequest(endpoint, headers, sessionId, protocolVersion, message);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var authHelp = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? " Check the remote server's configured headers or authentication; OAuth credentials managed internally by OpenCode are not automatically available to CodeBadger."
                : string.Empty;
            throw new InvalidOperationException($"Remote MCP server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).{authHelp} {responseText}");
        }
        var result = ParseRemoteResponse(responseText, id);
        if (result?["error"] is JsonObject error)
            throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "The remote MCP request failed.");
        var returnedSessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues)
            ? sessionValues.FirstOrDefault()
            : null;
        return (result?["result"] as JsonObject, returnedSessionId ?? sessionId);
    }

    private static async Task SendRemoteNotificationAsync(Uri endpoint, IReadOnlyDictionary<string, string> headers,
        string? sessionId, string protocolVersion, object message, CancellationToken cancellationToken)
    {
        using var request = CreateRemoteRequest(endpoint, headers, sessionId, protocolVersion, message);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote MCP server rejected initialization notification with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
    }

    private static HttpRequestMessage CreateRemoteRequest(Uri endpoint, IReadOnlyDictionary<string, string> headers,
        string? sessionId, string protocolVersion, object message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(message), System.Text.Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (!string.IsNullOrWhiteSpace(protocolVersion))
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        foreach (var (name, value) in headers)
            if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private static JsonObject? ParseRemoteResponse(string responseText, int id)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            throw new InvalidOperationException("The remote MCP server returned an empty response.");
        try
        {
            if (JsonNode.Parse(responseText) is JsonObject json && json["id"]?.GetValue<int>() == id)
                return json;
        }
        catch (JsonException) { }

        foreach (var line in responseText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var payload = trimmed[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;
            try
            {
                if (JsonNode.Parse(payload) is JsonObject json && json["id"]?.GetValue<int>() == id)
                    return json;
            }
            catch (JsonException) { }
        }
        throw new InvalidOperationException("The remote MCP server response did not contain the expected JSON-RPC response.");
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
