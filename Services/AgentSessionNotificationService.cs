using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace CodeBadger.Services;

/// <summary>Bridges native agent lifecycle hooks into CodeBadger's Slack/Discord session threads.</summary>
public sealed class AgentSessionNotificationService : IAsyncDisposable
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private const string HermesWebhookName = "codebadger-agent-session-notifications";
    private const string ClaudeHookCommandName = "CodeBadgerClaudeSessionNotification";
    private readonly LauncherSettingsService _settingsService;
    private readonly AgentConfigService _agentConfigService;
    private readonly SlackNotificationService _notifications;
    private readonly ILogger<AgentSessionNotificationService> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _hermesConfigGate = new(1, 1);
    private readonly Channel<AgentHookEvent> _events = Channel.CreateBounded<AgentHookEvent>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenDeliveries = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionDirectories = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _claudeSettingsGate = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptTask;
    private Task? _processTask;
    private string? _token;
    private int _port;

    public AgentSessionNotificationService(LauncherSettingsService settingsService,
        AgentConfigService agentConfigService, SlackNotificationService notifications,
        ILogger<AgentSessionNotificationService> logger)
    {
        _settingsService = settingsService;
        _agentConfigService = agentConfigService;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task PrepareAsync(IReadOnlyCollection<RepositoryEntry> repositories,
        CancellationToken cancellationToken = default)
    {
        if (!repositories.Any(repository => AgentTools.Normalize(repository.AgentTool) is AgentTools.ClaudeCode or AgentTools.Hermes))
            return;

        await EnsureListenerAsync(cancellationToken);
        if (repositories.Any(repository => AgentTools.Normalize(repository.AgentTool) == AgentTools.Hermes))
            await ConfigureHermesAsync(cancellationToken);
        if (repositories.Any(repository => AgentTools.Normalize(repository.AgentTool) == AgentTools.ClaudeCode))
            await EnsureClaudeHookFilesAsync(cancellationToken);
    }

    public async Task<string> GetRepositoryCommandAsync(RepositoryEntry repository,
        CancellationToken cancellationToken = default)
    {
        var agent = AgentTools.Normalize(repository.AgentTool);
        if (agent == AgentTools.ClaudeCode)
        {
            await PrepareAsync(new[] { repository }, cancellationToken);
            var settingsPath = GetClaudeSettingsPath();
            return $"claude --settings \"{settingsPath}\"";
        }

        if (agent == AgentTools.Hermes)
            await PrepareAsync(new[] { repository }, cancellationToken);

        return AgentTools.GetCommand(agent);
    }

    private async Task EnsureListenerAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
            return;
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_listener is not null)
                return;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(16);
            _listener = listener;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _shutdown = new CancellationTokenSource();
            _acceptTask = AcceptRequestsAsync(listener, _shutdown.Token);
            _processTask = ProcessEventsAsync(_shutdown.Token);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private string BuildEndpoint(string agent) => $"http://127.0.0.1:{_port}/{_token}/{agent}";

    private async Task EnsureClaudeHookFilesAsync(CancellationToken cancellationToken)
    {
        await _claudeSettingsGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(FileSystem.AppDataDirectory, "agent-session-hooks");
            Directory.CreateDirectory(directory);
            var scriptPath = Path.Combine(directory, "claude-hook.ps1");
            var settingsPath = GetClaudeSettingsPath();
            const string script = """
            param([Parameter(Mandatory = $true)][string]$Endpoint)
            try {
                $inputText = [Console]::In.ReadToEnd()
                if ([string]::IsNullOrWhiteSpace($inputText)) { exit 0 }
                $hook = $inputText | ConvertFrom-Json
                $event = switch ([string]$hook.hook_event_name) {
                    'UserPromptSubmit' { 'prompt' }
                    'Stop' { 'complete' }
                    default { exit 0 }
                }
                if ($event -eq 'complete' -and [string]::IsNullOrWhiteSpace([string]$hook.last_assistant_message)) { exit 0 }
                $body = @{
                    source = 'claude'
                    event = $event
                    event_id = [guid]::NewGuid().ToString('N')
                    session_id = [string]$hook.session_id
                    cwd = [string]$hook.cwd
                    prompt = [string]$hook.prompt
                    response = [string]$hook.last_assistant_message
                } | ConvertTo-Json -Depth 8 -Compress
                $request = [System.Net.HttpWebRequest]::Create($Endpoint)
                $request.Method = 'POST'
                $request.ContentType = 'application/json'
                $request.Timeout = 3000
                $request.ReadWriteTimeout = 3000
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
                $request.ContentLength = $bytes.Length
                $requestStream = $request.GetRequestStream()
                $requestStream.Write($bytes, 0, $bytes.Length)
                $requestStream.Dispose()
                $response = $request.GetResponse()
                $response.Dispose()
            } catch { }
            exit 0
            """;
        var endpoint = BuildEndpoint("claude");
        await WriteIfChangedAsync(scriptPath, script, cancellationToken);
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(command))
            command = "powershell.exe";
        var args = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-Endpoint", endpoint };
        await MergeClaudeSettingsAsync(settingsPath, command, args, cancellationToken);
        }
        finally
        {
            _claudeSettingsGate.Release();
        }
    }

    private string GetClaudeSettingsPath() => Path.Combine(FileSystem.AppDataDirectory,
        "agent-session-hooks", "claude-settings.json");

    private async Task MergeClaudeSettingsAsync(string path, string command, string[] args, CancellationToken cancellationToken)
    {
        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop" })
        {
            var events = hooks[eventName];
            var groups = events as JsonArray;
            if (groups is null)
            {
                groups = new JsonArray();
                if (events is JsonObject existingGroup)
                    groups.Add(existingGroup.DeepClone());
                hooks[eventName] = groups;
            }
            var group = groups.OfType<JsonObject>().FirstOrDefault(candidate =>
                candidate["hooks"] is JsonArray existingHandlers && existingHandlers.OfType<JsonObject>().Any(handler =>
                    string.Equals(handler["name"]?.GetValue<string>(), ClaudeHookCommandName, StringComparison.Ordinal)));
            group ??= groups.OfType<JsonObject>().FirstOrDefault(candidate => candidate["matcher"] is null &&
                candidate["hooks"] is JsonArray defaultHandlers && defaultHandlers.Count == 0);
            if (group is null)
            {
                group = new JsonObject { ["matcher"] = ClaudeHookCommandName, ["hooks"] = new JsonArray() };
                groups.Add(group);
            }
            if (group["matcher"] is JsonValue matcherValue && matcherValue.TryGetValue<string>(out var matcher) &&
                !string.Equals(matcher, "CodeBadgerClaudeSessionNotification", StringComparison.Ordinal))
            {
                group = new JsonObject { ["matcher"] = ClaudeHookCommandName, ["hooks"] = new JsonArray() };
                groups.Add(group);
            }
        var handlers = group["hooks"] as JsonArray;
            if (handlers is null)
            {
                handlers = new JsonArray();
                group["hooks"] = handlers;
            }
            var index = -1;
            for (var handlerIndex = 0; handlerIndex < handlers.Count; handlerIndex++)
                if (handlers[handlerIndex] is JsonObject handler &&
                    string.Equals(handler["name"]?.GetValue<string>(), ClaudeHookCommandName, StringComparison.Ordinal))
                {
                    index = handlerIndex;
                    break;
                }
            var ownedHandler = new JsonObject
            {
                ["type"] = "command",
                ["name"] = ClaudeHookCommandName,
                ["command"] = command,
                ["args"] = JsonSerializer.SerializeToNode(args),
                ["timeout"] = 5
            };
            if (index < 0)
                handlers.Add(ownedHandler);
            else
                handlers[index] = ownedHandler;
        }
        await WriteIfChangedAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private async Task ConfigureHermesAsync(CancellationToken cancellationToken)
    {
        await _hermesConfigGate.WaitAsync(cancellationToken);
        try
        {
            var configPath = GetHermesConfigPath();
            var yaml = File.Exists(configPath)
                ? await File.ReadAllTextAsync(configPath, cancellationToken)
                : "{}\n";
            var stream = new YamlStream();
            using (var reader = new StringReader(yaml))
                stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidOperationException("Hermes config.yaml must contain a YAML mapping.");
            var hooksKey = new YamlScalarNode("hooks");
            if (root.Children.TryGetValue(hooksKey, out var hooksNode))
            {
                if (hooksNode is not YamlMappingNode hooks)
                    throw new InvalidOperationException("Hermes hooks config must be a YAML mapping.");
                if (hooks.Children.TryGetValue(new YamlScalarNode("outbound"), out var outboundNode) && outboundNode is not YamlSequenceNode)
                    throw new InvalidOperationException("Hermes hooks.outbound must be a YAML sequence.");
            }

            var endpoint = BuildEndpoint("hermes");
            var updatedYaml = UpdateHermesOutboundYaml(yaml, endpoint);
            if (!string.Equals(yaml, updatedYaml, StringComparison.Ordinal))
                await SaveHermesConfigAsync(configPath, updatedYaml, cancellationToken);
            _logger.LogInformation("Configured Hermes outbound session hooks in {ConfigPath}.", configPath);
        }
        finally
        {
            _hermesConfigGate.Release();
        }
    }

    private static string UpdateHermesOutboundYaml(string yaml, string endpoint)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var hooksIndex = lines.FindIndex(line => Regex.IsMatch(line, "^hooks\\s*:"));
        if (hooksIndex >= 0 && !Regex.IsMatch(lines[hooksIndex], "^hooks\\s*:\\s*(?:#.*)?$"))
            throw new InvalidOperationException("Hermes config uses an inline hooks YAML value; CodeBadger will not rewrite it automatically.");
        if (hooksIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.AddRange(new[] { "hooks:", "  outbound:" }.Concat(GetHermesOutboundEntryLines(endpoint)));
            return string.Join(newline, lines) + newline;
        }

        var hooksEnd = FindYamlBlockEnd(lines, hooksIndex + 1, 0);
        var outboundIndex = Enumerable.Range(hooksIndex + 1, Math.Max(0, hooksEnd - hooksIndex - 1))
            .FirstOrDefault(index => Regex.IsMatch(lines[index], "^\\s+outbound\\s*:"), -1);
        if (outboundIndex < 0)
        {
            lines.InsertRange(hooksEnd, new[] { "  outbound:" }.Concat(GetHermesOutboundEntryLines(endpoint)));
            return string.Join(newline, lines) + newline;
        }

        var outboundIndent = lines[outboundIndex].Length - lines[outboundIndex].TrimStart().Length;
        if (!Regex.IsMatch(lines[outboundIndex], "^\\s+outbound\\s*:\\s*(?:#.*)?$"))
            throw new InvalidOperationException("Hermes hooks.outbound uses a YAML inline value; CodeBadger will not rewrite it automatically.");
        var outboundEnd = FindYamlBlockEnd(lines, outboundIndex + 1, outboundIndent);
        var itemIndent = outboundIndent + 2;
        var ownedStart = Enumerable.Range(outboundIndex + 1, Math.Max(0, outboundEnd - outboundIndex - 1))
            .FirstOrDefault(index => IsOwnedHermesEntryLine(lines[index], itemIndent), -1);
        if (ownedStart >= 0)
        {
            var ownedEnd = Enumerable.Range(ownedStart + 1, Math.Max(0, outboundEnd - ownedStart - 1))
                .FirstOrDefault(index => IsSequenceItemAtIndent(lines[index], itemIndent), outboundEnd);
            lines.RemoveRange(ownedStart, ownedEnd - ownedStart);
            outboundEnd -= ownedEnd - ownedStart;
        }

        lines.InsertRange(outboundEnd, GetHermesOutboundEntryLines(endpoint));
        return string.Join(newline, lines) + newline;
    }

    private static int FindYamlBlockEnd(IReadOnlyList<string> lines, int start, int parentIndent)
    {
        for (var index = start; index < lines.Count; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            var indent = line.Length - line.TrimStart().Length;
            if (indent <= parentIndent)
                return index;
        }
        return lines.Count;
    }

    private static bool IsOwnedHermesEntryLine(string line, int itemIndent) =>
        (IsSequenceItemAtIndent(line, itemIndent) ||
         string.IsNullOrWhiteSpace(line)) &&
        Regex.IsMatch(line.TrimStart(), "^-\\s*name\\s*:\\s*['\\\"]?" + Regex.Escape(HermesWebhookName) + "['\\\"]?\\s*(?:#.*)?$");

    private static bool IsSequenceItemAtIndent(string line, int expectedIndent) =>
        !string.IsNullOrWhiteSpace(line) &&
        line.Length - line.TrimStart().Length == expectedIndent &&
        line.TrimStart().StartsWith('-');

    private static string[] GetHermesOutboundEntryLines(string endpoint)
    {
        var quotedEndpoint = endpoint.Contains('"')
            ? "'" + endpoint.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "\"" + endpoint + "\"";
        return new[]
        {
            "    - name: " + HermesWebhookName,
            "      url: " + quotedEndpoint,
            "      events: [pre_llm_call, post_llm_call]",
            "      timeout: 10"
        };
    }

    private string GetHermesConfigPath()
    {
        if (Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } home)
            return Path.Combine(home, "config.yaml");

        var nativeWindowsHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");
        var nativeWindowsConfig = Path.Combine(nativeWindowsHome, "config.yaml");
        if (File.Exists(nativeWindowsConfig) || Directory.Exists(nativeWindowsHome))
            return nativeWindowsConfig;

        var legacyHomeConfig = _agentConfigService.GetConfigPath(AgentTools.Hermes);
        return File.Exists(legacyHomeConfig) ? legacyHomeConfig : nativeWindowsConfig;
    }

    private static async Task SaveHermesConfigAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Hermes config has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static YamlMappingNode CreateHermesWebhook(string endpoint) => new()
    {
        { "name", HermesWebhookName },
        { "url", endpoint },
        { "events", new YamlSequenceNode(new YamlScalarNode("pre_llm_call"), new YamlScalarNode("post_llm_call")) },
            { "timeout", "10" }
    };

    private static string? GetScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value : null;

    private static async Task WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
            return;
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private async Task AcceptRequestsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleRequestAsync(client, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var headerBytes = new List<byte>(1024);
                var headerEnd = false;
                while (headerBytes.Count < 16 * 1024)
                {
                    var next = new byte[1];
                    if (await stream.ReadAsync(next, cancellationToken) == 0)
                        break;
                    headerBytes.Add(next[0]);
                    var length = headerBytes.Count;
                    if (length >= 4 && headerBytes[length - 4] == 13 && headerBytes[length - 3] == 10 &&
                        headerBytes[length - 2] == 13 && headerBytes[length - 1] == 10)
                    {
                        headerEnd = true;
                        break;
                    }
                }
                if (!headerEnd)
                {
                    await WriteResponseAsync(stream, 431, cancellationToken);
                    return;
                }
                var headersText = Encoding.ASCII.GetString(headerBytes.ToArray());
                var headerLines = headersText.Split("\r\n", StringSplitOptions.None);
                var parts = headerLines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var contentLength = 0;
                foreach (var header in headerLines.Skip(1))
                {
                    var separator = header.IndexOf(':');
                    if (separator > 0 && header[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header[(separator + 1)..].Trim(), out contentLength);
                }

                var expectedPath = $"/{_token}/";
                if (parts.Length < 2 || parts[0] != "POST" || !parts[1].StartsWith(expectedPath, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, cancellationToken);
                    return;
                }
                if (contentLength is < 1 or > MaximumRequestBytes)
                {
                    await WriteResponseAsync(stream, 413, cancellationToken);
                    return;
                }

                var body = new byte[contentLength];
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken);
                    if (read == 0)
                        break;
                    offset += read;
                }
                if (offset != body.Length)
                {
                    await WriteResponseAsync(stream, 400, cancellationToken);
                    return;
                }

                using var payload = JsonDocument.Parse(body.AsMemory(0, offset));
                var source = parts[1][expectedPath.Length..].TrimEnd('/');
                var expectedSource = parts[1].EndsWith("/hermes", StringComparison.Ordinal) ? "hermes" :
                    parts[1].EndsWith("/claude", StringComparison.Ordinal) ? "claude" : string.Empty;
                if (source != expectedSource)
                {
                    await WriteResponseAsync(stream, 404, cancellationToken);
                    return;
                }
                var accepted = TryQueueEvent(source, payload.RootElement);
                await WriteResponseAsync(stream, accepted ? 202 : 400, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Ignored an invalid local agent notification hook request.");
                try { await WriteResponseAsync(client.GetStream(), 400, CancellationToken.None); }
                catch (Exception) { }
            }
        }
    }

    private bool TryQueueEvent(string source, JsonElement payload)
    {
        if (source is not ("claude" or "hermes") || payload.ValueKind != JsonValueKind.Object)
            return false;

        var eventName = GetString(payload, "event");
        if (source == "claude")
        {
            if (eventName is not ("prompt" or "complete"))
                return false;
            var claudeSessionId = GetString(payload, "session_id");
            if (string.IsNullOrWhiteSpace(claudeSessionId))
                return false;
            if (eventName == "complete" && string.IsNullOrWhiteSpace(GetString(payload, "response")))
                return false;
            var eventId = GetString(payload, "event_id");
            var deliveryKey = claudeSessionId + ":" + eventName + ":" + eventId;
            if (!string.IsNullOrWhiteSpace(eventId) && !_seenDeliveries.TryAdd(deliveryKey, 0))
                return true;
            return _events.Writer.TryWrite(new AgentHookEvent(source, eventName,
                claudeSessionId, GetString(payload, "cwd"), GetText(payload, "prompt"),
                GetString(payload, "response"), eventId, string.Empty));
        }

        var hookName = GetString(payload, "hook_event_name");
        if (hookName is not ("pre_llm_call" or "post_llm_call") ||
            !payload.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Object)
            return false;
        if (!extra.TryGetProperty("conversation_history", out var conversationHistory) ||
            conversationHistory.ValueKind != JsonValueKind.Array ||
            !conversationHistory.EnumerateArray().Any(message => message.ValueKind == JsonValueKind.Object &&
                GetString(message, "role") == "user"))
            return false;
        var sessionId = GetString(payload, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;
        if (hookName == "post_llm_call" && string.IsNullOrWhiteSpace(GetText(extra, "assistant_response")))
            return false;
        var platform = GetString(extra, "platform");
        if (platform is not ("cli" or "tui" or "desktop"))
            return false;
        var deliveryId = GetString(payload, "delivery_id");
        if (!string.IsNullOrWhiteSpace(deliveryId) && !_seenDeliveries.TryAdd(deliveryId, 0))
            return true;
        if (_seenDeliveries.Count > 2048)
            foreach (var key in _seenDeliveries.Keys.Take(512))
                _seenDeliveries.TryRemove(key, out _);
        var eventType = hookName == "pre_llm_call" ? "prompt" : "complete";
        var directory = GetString(payload, "cwd");
        if (eventType == "prompt" && !string.IsNullOrWhiteSpace(directory))
            _sessionDirectories[sessionId] = directory;
        else if (string.IsNullOrWhiteSpace(directory) && _sessionDirectories.TryGetValue(sessionId, out var recordedDirectory))
            directory = recordedDirectory;
        if (_sessionDirectories.Count > 2048)
            foreach (var key in _sessionDirectories.Keys.Take(512))
                _sessionDirectories.TryRemove(key, out _);
        return _events.Writer.TryWrite(new AgentHookEvent(source, eventType,
            sessionId, directory, GetText(extra, "user_message"),
            GetText(extra, "assistant_response"), deliveryId, deliveryId));
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var hookEvent in _events.Reader.ReadAllAsync(cancellationToken))
            {
                try { await DispatchEventAsync(hookEvent, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not publish {Agent} notification event for session {SessionId}.",
                        hookEvent.Source, hookEvent.SessionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task DispatchEventAsync(AgentHookEvent hookEvent, CancellationToken cancellationToken)
    {
        var actualSessionId = hookEvent.SessionId;
        var eventDirectory = hookEvent.Directory;
        if (string.IsNullOrWhiteSpace(eventDirectory) && hookEvent.Source == "claude" &&
            TryGetClaudeSessionTranscript(actualSessionId, out var mappedTranscript))
            eventDirectory = Path.GetDirectoryName(mappedTranscript) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(actualSessionId) || string.IsNullOrWhiteSpace(eventDirectory) ||
            OpenCodeActivityService.NormalizePath(eventDirectory) is not { } directory)
            return;
        var settings = await _settingsService.LoadAsync();
        var agent = hookEvent.Source == "claude" ? AgentTools.ClaudeCode : AgentTools.Hermes;
        var repository = settings.Repositories
            .Where(candidate => candidate.IsActive && AgentTools.Normalize(candidate.AgentTool) == agent &&
                !string.IsNullOrWhiteSpace(candidate.Path) &&
                OpenCodeActivityService.NormalizePath(candidate.Path) is { } repoPath && IsPathWithin(directory, repoPath))
            .OrderByDescending(candidate => OpenCodeActivityService.NormalizePath(candidate.Path)?.Length ?? 0)
            .FirstOrDefault();
        if (repository is null)
            return;
        if (repository.SlackNotificationsMuted || !_notifications.CanSendNotifications)
            return;

        var sessionId = CreateNotificationSessionId(hookEvent.Source, actualSessionId);
        var repositoryPath = OpenCodeActivityService.NormalizePath(repository.Path) ?? directory;
        var repositoryName = string.IsNullOrWhiteSpace(repository.Name)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path)) : repository.Name;
        var prompt = hookEvent.Prompt;
        var title = GetSessionTitle(prompt, agent);
        if (hookEvent.Source == "claude" && TryGetClaudeSessionTranscript(actualSessionId, out var transcriptPath))
        {
            var transcript = await ReadClaudeTranscriptAsync(transcriptPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(transcript.Title))
                title = transcript.Title;
            if (hookEvent.Event == "complete" && !string.IsNullOrWhiteSpace(transcript.Prompt))
                prompt = transcript.Prompt;
        }
        var session = new OpenCodeActivityService.ActiveSession(sessionId, repositoryPath,
            AgentTools.GetDisplayName(agent), title, repositoryName);

        if (hookEvent.Event == "prompt")
            await _notifications.PublishExternalAgentPromptAsync(session, hookEvent.Prompt,
                hookEvent.EventId, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(hookEvent.Response))
            await _notifications.PublishExternalAgentCompletionAsync(session, hookEvent.Response, cancellationToken);
    }

    private bool TryGetClaudeSessionTranscript(string sessionId, out string transcriptPath)
    {
        transcriptPath = string.Empty;
        var settingsPath = GetClaudeSettingsPath();
        if (!File.Exists(settingsPath))
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var eventHooks in hooks.EnumerateObject())
            {
                if (eventHooks.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var group in eventHooks.Value.EnumerateArray())
                {
                    var handlers = group.ValueKind == JsonValueKind.Array
                        ? group
                        : group.ValueKind == JsonValueKind.Object && group.TryGetProperty("hooks", out var groupHandlers)
                            ? groupHandlers : default;
                    if (handlers.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var handler in handlers.EnumerateArray())
                    {
                        if (!handler.TryGetProperty("args", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
                            continue;
                        var args = arguments.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                            .Select(value => value.GetString() ?? string.Empty).ToArray();
                        for (var index = 0; index < args.Length - 1; index++)
                        {
                            if (!args[index].Equals("-SessionMapPath", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var mapPath = args[index + 1];
                            if (!File.Exists(mapPath))
                                return false;
                            using var mapDocument = JsonDocument.Parse(File.ReadAllText(mapPath));
                            if (!mapDocument.RootElement.TryGetProperty(sessionId, out var path) || path.ValueKind != JsonValueKind.String)
                                return false;
                            transcriptPath = path.GetString() ?? string.Empty;
                            return !string.IsNullOrWhiteSpace(transcriptPath);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(exception, "Could not read Claude Code session transcript mapping.");
        }
        return false;
    }

    private static async Task<(string Prompt, string Title)> ReadClaudeTranscriptAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return (string.Empty, string.Empty);
        var newestUserText = string.Empty;
        var title = string.Empty;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 || line.Length > 2 * 1024 * 1024)
                continue;
            try
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
                    continue;
                var recordType = type.GetString();
                if (recordType == "custom-title" && root.TryGetProperty("customTitle", out var customTitle))
                    title = customTitle.GetString() ?? title;
                if (recordType != "user" || !root.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("content", out var content))
                    continue;
                var text = ExtractClaudeText(content);
                if (!string.IsNullOrWhiteSpace(text))
                    newestUserText = text;
            }
            catch (JsonException) { }
        }
        return (newestUserText, title);
    }

    private static string ExtractClaudeText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Join("\n", content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "text")
            .Select(part => GetString(part, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool IsPathWithin(string candidatePath, string repositoryPath)
    {
        if (string.Equals(candidatePath, repositoryPath, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = Path.TrimEndingDirectorySeparator(repositoryPath) + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSessionTitle(string prompt, string agent)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return $"{AgentTools.GetDisplayName(agent)} session";
        var line = prompt.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => !value.StartsWith("Ticket:", StringComparison.OrdinalIgnoreCase) &&
                                     !value.StartsWith("Branch:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
            return $"{AgentTools.GetDisplayName(agent)} session";
        return line.Length > 120 ? line[..120].TrimEnd() + "…" : line;
    }

    private static string CreateNotificationSessionId(string source, string sessionId)
    {
        var safeSource = source == "claude" ? "claude" : "hermes";
        if (sessionId.Length <= 160 && sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            return $"ses_{safeSource}_{sessionId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant();
        return $"ses_{safeSource}_{hash}";
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static string GetText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Join("\n", value.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "text")
            .Select(part => GetString(part, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static async Task WriteJsonIfChangedAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await WriteIfChangedAsync(path, json, cancellationToken);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, CancellationToken cancellationToken)
    {
        var status = statusCode switch
        {
            202 => "Accepted",
            400 => "Bad Request",
            404 => "Not Found",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            _ => "Service Unavailable"
        };
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _shutdown?.Cancel();
        _listener?.Stop();
        if (_acceptTask is not null)
        {
            try { await _acceptTask; }
            catch (OperationCanceledException) { }
        }
        if (_processTask is not null)
        {
            try { await _processTask; }
            catch (OperationCanceledException) { }
        }
        _shutdown?.Dispose();
        _startGate.Dispose();
        _hermesConfigGate.Dispose();
        _claudeSettingsGate.Dispose();
    }

    private sealed record AgentHookEvent(string Source, string Event, string SessionId, string Directory,
        string Prompt, string Response, string EventId, string DeliveryId);
}
