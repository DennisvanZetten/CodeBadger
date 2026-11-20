using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed class SlackNotificationService : IAsyncDisposable
{
    private const string ThreadLinksStorageKey = "codebadger-slack-session-threads";
    private const string MutedSessionsStorageKey = "codebadger-slack-muted-sessions";
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://slack.com/api/") };
    private readonly ConcurrentDictionary<string, ThreadLink> _sessionThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenReplies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _mutedSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private CancellationTokenSource? _connectionCancellation;
    private Task? _connectionTask;
    private string _botToken = string.Empty;
    private string _appToken = string.Empty;
    private string _channelId = string.Empty;
    private string _allowedUserId = string.Empty;

    public event Action<OpenCodeActivityService.ActiveSession, string>? ReplyReceived;
    public event Action<bool, string>? ConnectionChanged;
    public event Action? SessionNotificationsChanged;
    public bool IsConnected { get; private set; }
    public string ConnectionStatus { get; private set; } = "Slack notifications are not connected.";

    public sealed record SlackChannel(string Id, string Name, bool IsMember, bool IsPrivate);
    public sealed record SessionNotification(string Id, string Title, string Agent, string Directory, bool IsMuted);
    private sealed record ThreadLink(string ThreadTs, string ChannelId, string Directory, string Agent, string Title, DateTime CreatedAt);

    public async Task<IReadOnlyList<SlackChannel>> GetChannelsAsync(string botToken, CancellationToken cancellationToken = default)
    {
        var channels = new List<SlackChannel>();
        string? cursor = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "conversations.list?types=public_channel,private_channel&exclude_archived=true&limit=200" +
                (string.IsNullOrWhiteSpace(cursor) ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireToken(botToken, "xoxb-"));
            using var response = await Http.SendAsync(request, cancellationToken);
            using var json = await ReadSlackResponseAsync(response, cancellationToken);
            foreach (var channel in json.RootElement.GetProperty("channels").EnumerateArray())
            {
                var id = channel.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var name = channel.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    continue;
                var isMember = channel.TryGetProperty("is_member", out var member) && member.GetBoolean();
                channels.Add(new SlackChannel(id, name, isMember,
                    channel.TryGetProperty("is_private", out var isPrivate) && isPrivate.GetBoolean()));
            }
            cursor = json.RootElement.TryGetProperty("response_metadata", out var metadata) &&
                metadata.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return channels.OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task TestConnectionAsync(string botToken, string appToken, CancellationToken cancellationToken = default)
    {
        _ = await GetChannelsAsync(botToken, cancellationToken);
        var socketUri = await OpenSocketAsync(appToken, cancellationToken);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUri, cancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection test", cancellationToken);
    }

    public async Task ConfigureAsync(LauncherSettings settings)
    {
        await _configurationGate.WaitAsync();
        try
        {
            var botToken = settings.SlackBotToken.Trim();
            var appToken = settings.SlackAppToken.Trim();
            var channelId = settings.SlackChannelId.Trim();
            var allowedUserId = settings.SlackAllowedUserId.Trim();
            if (_botToken == botToken && _appToken == appToken && _channelId == channelId && _allowedUserId == allowedUserId &&
                settings.SlackNotificationsEnabled == (_connectionTask is not null))
                return;

            await StopConnectionAsync();
            _botToken = botToken;
            _appToken = appToken;
            _channelId = channelId;
            _allowedUserId = allowedUserId;
            await LoadThreadLinksAsync(channelId);
            await LoadMutedSessionsAsync();
            if (!settings.SlackNotificationsEnabled)
            {
                SetConnectionState(false, "Slack notifications are disabled.");
                return;
            }
            if (!botToken.StartsWith("xoxb-", StringComparison.Ordinal) ||
                !appToken.StartsWith("xapp-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(channelId) ||
                !allowedUserId.StartsWith("U", StringComparison.Ordinal))
            {
                SetConnectionState(false, "Add valid Slack tokens and select a channel to enable notifications.");
                return;
            }

            var cancellation = new CancellationTokenSource();
            _connectionCancellation = cancellation;
            _connectionTask = Task.Run(() => RunSocketLoopAsync(cancellation.Token));
        }
        finally { _configurationGate.Release(); }
    }

    public async Task PublishCompletionAsync(OpenCodeActivityService.ActiveSession session, string answer,
        CancellationToken cancellationToken = default)
    {
        if (!CanSendNotifications || IsSessionMuted(session.Id))
            return;
        var title = string.IsNullOrWhiteSpace(session.Title) ? "OpenCode session" : session.Title;
        if (_sessionThreads.TryGetValue(session.Id, out var existingThread))
            title = string.IsNullOrWhiteSpace(existingThread.Title) ? title : existingThread.Title;
        var safeAnswer = answer.Replace("```", "'''");
        safeAnswer = safeAnswer.Length > 3500 ? safeAnswer[..3500] + "\n…" : safeAnswer;
        var message = $"✅ *OpenCode klaar: {EscapeSlack(title)}*\n" +
            $"Agent: `{EscapeSlack(session.Agent)}` · Sessie: `{EscapeSlack(session.Id)}`\n" +
            $"```\n{safeAnswer}\n```";
        string? threadTs = null;
        if (_sessionThreads.TryGetValue(session.Id, out var previous))
            threadTs = previous.ThreadTs;
        var result = await PostMessageAsync(_botToken, _channelId, message, threadTs, cancellationToken);
        if (threadTs is null && result is not null)
        {
            _sessionThreads[session.Id] = new ThreadLink(result, _channelId, session.Directory, session.Agent, title, DateTime.UtcNow);
            await SaveThreadLinksAsync();
            SessionNotificationsChanged?.Invoke();
        }
    }

    public bool CanSendNotifications => IsConnected && !string.IsNullOrWhiteSpace(_botToken) &&
        !string.IsNullOrWhiteSpace(_channelId) && !string.IsNullOrWhiteSpace(_allowedUserId);

    public IReadOnlyList<SessionNotification> GetSessionNotifications(string directory)
    {
        var normalized = OpenCodeActivityService.NormalizePath(directory);
        if (normalized is null)
            return [];
        return _sessionThreads
            .Where(pair => string.Equals(OpenCodeActivityService.NormalizePath(pair.Value.Directory), normalized,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => new SessionNotification(pair.Key, pair.Value.Title, pair.Value.Agent, pair.Value.Directory,
                IsSessionMuted(pair.Key)))
            .OrderByDescending(session => session.Id)
            .ToArray();
    }

    public bool IsSessionMuted(string sessionId) => _mutedSessions.ContainsKey(sessionId);

    public async Task SetSessionMutedAsync(string sessionId, bool muted)
    {
        if (!sessionId.StartsWith("ses_", StringComparison.Ordinal) ||
            !sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            return;
        if (muted)
            _mutedSessions[sessionId] = 0;
        else
            _mutedSessions.TryRemove(sessionId, out _);
        await SaveMutedSessionsAsync();
        SessionNotificationsChanged?.Invoke();
    }

    private async Task LoadMutedSessionsAsync()
    {
        _mutedSessions.Clear();
        var json = await SecureStorage.Default.GetAsync(MutedSessionsStorageKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(json) ?? [];
            foreach (var id in ids.Where(id => id.StartsWith("ses_", StringComparison.Ordinal) &&
                         id.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')))
                _mutedSessions[id] = 0;
        }
        catch (JsonException) { }
    }

    private Task SaveMutedSessionsAsync() => _mutedSessions.IsEmpty
        ? RemoveMutedSessionsAsync()
        : SecureStorage.Default.SetAsync(MutedSessionsStorageKey, JsonSerializer.Serialize(_mutedSessions.Keys.Order().ToArray()));

    private static Task RemoveMutedSessionsAsync()
    {
        SecureStorage.Default.Remove(MutedSessionsStorageKey);
        return Task.CompletedTask;
    }

    public async Task PublishReplyStatusAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        if (!_sessionThreads.TryGetValue(sessionId, out var link))
            return;
        await PostMessageAsync(_botToken, link.ChannelId, text, link.ThreadTs, cancellationToken);
    }

    public async Task UpdateSessionAgentAsync(string sessionId, string agent)
    {
        if (agent is not ("build" or "plan") || !_sessionThreads.TryGetValue(sessionId, out var link))
            return;
        _sessionThreads[sessionId] = link with { Agent = agent };
        await SaveThreadLinksAsync();
        SessionNotificationsChanged?.Invoke();
    }

    private async Task RunSocketLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var uri = await OpenSocketAsync(_appToken, cancellationToken);
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(uri, cancellationToken);
                SetConnectionState(true, "Connected to Slack using Socket Mode.");
                delay = TimeSpan.FromSeconds(2);
                await ReceiveEventsAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                SetConnectionState(false, "Slack connection unavailable: " + exception.Message);
            }
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveEventsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 1024 * 1024)
                    throw new InvalidDataException("Slack event exceeded the allowed size.");
            } while (!result.EndOfMessage);

            using var json = JsonDocument.Parse(message.ToArray());
            var root = json.RootElement;
            if (root.TryGetProperty("envelope_id", out var envelope))
            {
                var ack = JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelope.GetString() });
                await socket.SendAsync(new ArraySegment<byte>(ack), WebSocketMessageType.Text, true, cancellationToken);
            }
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("event", out var slackEvent) ||
                !slackEvent.TryGetProperty("type", out var type) || type.GetString() != "message")
                continue;
            await HandleMessageEventAsync(payload, slackEvent);
        }
    }

    private async Task HandleMessageEventAsync(JsonElement payload, JsonElement slackEvent)
    {
        if (slackEvent.TryGetProperty("bot_id", out _) ||
            !slackEvent.TryGetProperty("channel", out var channelValue) || channelValue.GetString() != _channelId ||
            !slackEvent.TryGetProperty("user", out var userValue) || userValue.GetString() != _allowedUserId ||
            !slackEvent.TryGetProperty("text", out var textValue) || string.IsNullOrWhiteSpace(textValue.GetString()) ||
            !slackEvent.TryGetProperty("ts", out var tsValue))
            return;

        var eventId = payload.TryGetProperty("event_id", out var idValue) ? idValue.GetString() : null;
        var ts = tsValue.GetString();
        if (string.IsNullOrWhiteSpace(ts) || !_seenReplies.TryAdd(eventId ?? ts, 0))
            return;
        var threadTs = slackEvent.TryGetProperty("thread_ts", out var threadValue) ? threadValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(threadTs))
        {
            _seenReplies.TryRemove(eventId ?? ts, out _);
            return;
        }
        var linkedSession = _sessionThreads.FirstOrDefault(pair => pair.Value.ThreadTs == threadTs && pair.Value.ChannelId == _channelId);
        if (string.IsNullOrWhiteSpace(linkedSession.Key))
            return;
        var text = textValue.GetString()!.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var session = new OpenCodeActivityService.ActiveSession(linkedSession.Key,
                linkedSession.Value.Directory, linkedSession.Value.Agent, linkedSession.Value.Title);
            ReplyReceived?.Invoke(session, text);
        }
        await Task.CompletedTask;
    }

    private async Task LoadThreadLinksAsync(string channelId)
    {
        _sessionThreads.Clear();
        var json = await SecureStorage.Default.GetAsync(ThreadLinksStorageKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var links = JsonSerializer.Deserialize<Dictionary<string, ThreadLink>>(json);
            if (links is null)
                return;
            foreach (var pair in links.Where(pair => pair.Value.ChannelId == channelId)
                         .OrderByDescending(pair => pair.Value.CreatedAt).Take(100))
                if (pair.Key.StartsWith("ses_", StringComparison.Ordinal))
                    _sessionThreads[pair.Key] = pair.Value;
        }
        catch (JsonException) { }
    }

    private async Task SaveThreadLinksAsync()
    {
        var links = _sessionThreads.OrderByDescending(pair => pair.Value.CreatedAt).Take(100)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        await SecureStorage.Default.SetAsync(ThreadLinksStorageKey, JsonSerializer.Serialize(links));
    }

    private static async Task<Uri> OpenSocketAsync(string appToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "apps.connections.open");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireToken(appToken, "xapp-"));
        using var response = await Http.SendAsync(request, cancellationToken);
        using var json = await ReadSlackResponseAsync(response, cancellationToken);
        var url = json.RootElement.GetProperty("url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" ||
            !(uri.Host.Equals("slack.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Slack did not return a valid Socket Mode URL.");
        return uri;
    }

    private static async Task<string> PostMessageAsync(string botToken, string channelId, string text,
        string? threadTs, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat.postMessage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        var payload = new Dictionary<string, object?>
        {
            ["channel"] = channelId,
            ["text"] = text,
            ["unfurl_links"] = false,
            ["unfurl_media"] = false
        };
        if (!string.IsNullOrWhiteSpace(threadTs))
            payload["thread_ts"] = threadTs;
        request.Content = JsonContent.Create(payload);
        using var response = await Http.SendAsync(request, cancellationToken);
        using var json = await ReadSlackResponseAsync(response, cancellationToken);
        return json.RootElement.GetProperty("ts").GetString()
            ?? throw new InvalidOperationException("Slack did not return the notification message ID.");
    }

    private static async Task<JsonDocument> ReadSlackResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = json.RootElement.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : response.ReasonPhrase;
            json.Dispose();
            throw new InvalidOperationException("Slack API error: " + error);
        }
        return json;
    }

    private static string RequireToken(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value :
            throw new InvalidOperationException($"Slack token must start with {prefix}.");

    private static string EscapeSlack(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private void SetConnectionState(bool connected, string status)
    {
        IsConnected = connected;
        ConnectionStatus = status;
        ConnectionChanged?.Invoke(connected, status);
    }

    private async Task StopConnectionAsync()
    {
        if (_connectionCancellation is null)
            return;
        _connectionCancellation.Cancel();
        if (_connectionTask is not null)
        {
            try { await _connectionTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
        _connectionCancellation.Dispose();
        _connectionCancellation = null;
        _connectionTask = null;
        SetConnectionState(false, "Slack connection stopped.");
        _seenReplies.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _configurationGate.WaitAsync();
        try { await StopConnectionAsync(); }
        finally { _configurationGate.Release(); _configurationGate.Dispose(); }
    }
}
