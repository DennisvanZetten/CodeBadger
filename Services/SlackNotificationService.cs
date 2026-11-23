using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed class SlackNotificationService : IAsyncDisposable
{
    private const string ThreadLinksStorageKey = "codebadger-slack-session-threads";
    private const string ThreadLinksFileName = "slack-session-threads.json";
    private const string MutedSessionsStorageKey = "codebadger-slack-muted-sessions";
    private readonly LauncherSettingsService _settingsService;
    private readonly OpenCodeActivityService _openCodeActivity;
    private readonly GitBranchService _gitBranchService;
    private readonly AiCommitMessageService _aiCommitMessageService;
    private readonly IdeaListService _ideaListService;
    private readonly ILogger<SlackNotificationService> _logger;
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://slack.com/api/") };
    private static readonly HttpClient DiscordHttp = new() { BaseAddress = new Uri("https://discord.com/api/v10/") };
    private readonly ConcurrentDictionary<string, ThreadLink> _sessionThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingNewSession> _pendingNewSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommitMessage> _pendingCommitMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingInteractionThread> _pendingInteractionThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _announcedInteractions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenReplies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _submittedMessageTexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _publishedSessionMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _mutedSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly SemaphoreSlim _threadLinksStorageGate = new(1, 1);
    private CancellationTokenSource? _connectionCancellation;
    private Task? _connectionTask;
    private string _botToken = string.Empty;
    private string _appToken = string.Empty;
    private string _channelId = string.Empty;
    private string _allowedUserId = string.Empty;
    private string _discordBotToken = string.Empty;
    private string _discordChannelId = string.Empty;
    private string _discordAllowedUserId = string.Empty;
    private string _activePlatform = "Slack";

    public SlackNotificationService(LauncherSettingsService settingsService, OpenCodeActivityService openCodeActivity,
        GitBranchService gitBranchService, AiCommitMessageService aiCommitMessageService, IdeaListService ideaListService,
        ILogger<SlackNotificationService> logger)
    {
        _settingsService = settingsService;
        _openCodeActivity = openCodeActivity;
        _gitBranchService = gitBranchService;
        _aiCommitMessageService = aiCommitMessageService;
        _ideaListService = ideaListService;
        _logger = logger;
    }

    public event Action<OpenCodeActivityService.ActiveSession, string>? ReplyReceived;
    public event Action<OpenCodeActivityService.ActiveSession>? SessionStarted;
    public event Action<OpenCodeActivityService.ActiveSession>? SessionLinked;
    public event Action<bool, string>? ConnectionChanged;
    public event Action? SessionNotificationsChanged;
    public bool IsConnected { get; private set; }
    public string ConnectionStatus { get; private set; } = "Slack notifications are not connected.";

    public sealed record SlackChannel(string Id, string Name, bool IsMember, bool IsPrivate);
    public sealed record DiscordChannel(string Id, string Name, string Type);
    public sealed record SessionNotification(string Id, string Title, string Agent, string Directory, bool IsMuted);
    private sealed record ThreadLink(string ThreadTs, string ChannelId, string Directory, string Agent, string Title,
        DateTime CreatedAt, bool InitialPromptPublished = false, bool RootMessageIsTitle = false,
        string Platform = "Slack", string RootMessageId = "");
    private sealed class PendingNewSession(IReadOnlyList<RepositoryEntry> repositories, string rootMessageId = "")
    {
        public IReadOnlyList<RepositoryEntry> Repositories { get; } = repositories;
        public string RootMessageId { get; } = rootMessageId;
        public RepositoryEntry? SelectedRepository { get; set; }
    }
    private sealed record PendingCommitMessage(string SessionId, string Directory, string Title, string Description);
    private sealed record PendingInteractionThread(OpenCodeActivityService.ActiveSession Session,
        IReadOnlyList<OpenCodeActivityService.PendingInteraction> Interactions);

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

    public async Task<IReadOnlyList<DiscordChannel>> GetDiscordChannelsAsync(string botToken, string guildId,
        CancellationToken cancellationToken = default)
    {
        if (!IsDiscordSnowflake(guildId))
            throw new InvalidOperationException("Enter a valid Discord server ID first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"guilds/{Uri.EscapeDataString(guildId.Trim())}/channels");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken.Trim());
        using var response = await DiscordHttp.SendAsync(request, cancellationToken);
        using var json = await ReadDiscordResponseAsync(response, cancellationToken);
        var channels = new List<DiscordChannel>();
        foreach (var channel in json.RootElement.EnumerateArray())
        {
            var type = channel.TryGetProperty("type", out var typeValue) ? typeValue.GetInt32() : -1;
            if (type is not (0 or 5))
                continue;
            var id = channel.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var name = channel.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (id is not null && name is not null)
                channels.Add(new DiscordChannel(id, name, type == 0 ? "Text" : "Announcement"));
        }
        var result = channels.OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _logger.LogDebug("Loaded {ChannelCount} Discord channels for guild {GuildId}.", result.Length, guildId);
        return result;
    }

    public async Task TestDiscordConnectionAsync(string botToken, string channelId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"channels/{Uri.EscapeDataString(channelId.Trim())}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken.Trim());
        using var response = await DiscordHttp.SendAsync(request, cancellationToken);
        using var json = await ReadDiscordResponseAsync(response, cancellationToken);
        if (!json.RootElement.TryGetProperty("id", out _))
            throw new InvalidOperationException("Discord did not return the configured channel.");
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
            var discordBotToken = settings.DiscordBotToken.Trim();
            var discordChannelId = settings.DiscordChannelId.Trim();
            var discordAllowedUserId = settings.DiscordAllowedUserId.Trim();
            var platform = settings.NotificationChannel == "Discord" ? "Discord" : "Slack";
            if (_botToken == botToken && _appToken == appToken && _channelId == channelId && _allowedUserId == allowedUserId &&
                _discordBotToken == discordBotToken && _discordChannelId == discordChannelId &&
                _discordAllowedUserId == discordAllowedUserId && _activePlatform == platform &&
                settings.SlackNotificationsEnabled == (_connectionTask is not null) &&
                (!settings.SlackNotificationsEnabled || IsConnected))
                return;

            await StopConnectionAsync();
            _botToken = botToken;
            _appToken = appToken;
            _channelId = channelId;
            _allowedUserId = allowedUserId;
            _discordBotToken = discordBotToken;
            _discordChannelId = discordChannelId;
            _discordAllowedUserId = discordAllowedUserId;
            _activePlatform = platform;
            _logger.LogInformation("Configuring notification integration for platform {NotificationPlatform}; enabled={NotificationsEnabled}.",
                platform, settings.SlackNotificationsEnabled);
            _pendingNewSessions.Clear();
            _pendingCommitMessages.Clear();
            _pendingInteractionThreads.Clear();
            _announcedInteractions.Clear();
            await LoadThreadLinksAsync(platform, platform == "Discord" ? discordChannelId : channelId);
            await LoadMutedSessionsAsync();
            if (!settings.SlackNotificationsEnabled)
            {
                SetConnectionState(false, $"{platform} notifications are disabled.");
                return;
            }
            if (platform == "Discord")
            {
                if (string.IsNullOrWhiteSpace(discordBotToken) || string.IsNullOrWhiteSpace(discordChannelId) ||
                    !IsDiscordSnowflake(discordAllowedUserId))
                {
                    SetConnectionState(false, "Add a Discord bot token, select a channel, and enter your Discord user ID.");
                    return;
                }
                var discordCancellation = new CancellationTokenSource();
                _connectionCancellation = discordCancellation;
                _connectionTask = Task.Run(() => RunDiscordGatewayLoopAsync(discordCancellation.Token));
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
        using var repositoryScope = _logger.BeginScope(new Dictionary<string, object?> { ["RepositoryPath"] = session.Directory });
        if (!CanSendNotifications || IsSessionMuted(session.Id))
            return;
        var title = string.IsNullOrWhiteSpace(session.Title) || session.Title.Equals("New session", StringComparison.OrdinalIgnoreCase)
            ? "OpenCode session" : session.Title;
        if (_sessionThreads.TryGetValue(session.Id, out var existingThread) && existingThread.Platform == _activePlatform &&
            existingThread.ChannelId == (_activePlatform == "Discord" ? _discordChannelId : _channelId))
            title = string.IsNullOrWhiteSpace(existingThread.Title) ? title : existingThread.Title;
        try
        {
            _logger.LogDebug("Publishing completion notification for session {SessionId} through {NotificationPlatform}.", session.Id, _activePlatform);
            var thread = await EnsureSessionThreadLinkAsync(session, title, cancellationToken);
            await PostLongThreadMessageAsync(thread, "✅ *OpenCode klaar*", answer, cancellationToken);
            _logger.LogInformation("Published completion notification for session {SessionId} through {NotificationPlatform}.", session.Id, _activePlatform);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not post completion for OpenCode session {SessionId} to Slack.", session.Id);
            throw;
        }
    }

    public bool CanSendNotifications => IsConnected && (_activePlatform == "Discord"
        ? !string.IsNullOrWhiteSpace(_discordBotToken) && !string.IsNullOrWhiteSpace(_discordChannelId) &&
          !string.IsNullOrWhiteSpace(_discordAllowedUserId)
        : !string.IsNullOrWhiteSpace(_botToken) && !string.IsNullOrWhiteSpace(_channelId) &&
          !string.IsNullOrWhiteSpace(_allowedUserId));

    public IReadOnlyList<SessionNotification> GetSessionNotifications(string directory, bool repositoryMuted = false)
    {
        if (repositoryMuted || !CanSendNotifications)
            return [];
        var normalized = OpenCodeActivityService.NormalizePath(directory);
        if (normalized is null)
            return [];
        return _sessionThreads
            .Where(pair => pair.Value.Platform == _activePlatform &&
                pair.Value.ChannelId == (_activePlatform == "Discord" ? _discordChannelId : _channelId) &&
                string.Equals(OpenCodeActivityService.NormalizePath(pair.Value.Directory), normalized,
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
        if (!_sessionThreads.TryGetValue(sessionId, out var link) || link.Platform != _activePlatform ||
            link.ChannelId != (_activePlatform == "Discord" ? _discordChannelId : _channelId))
            return;
        await PostMessageAsync(_botToken, link.ChannelId, text, link.ThreadTs, cancellationToken);
    }

    public async Task PublishApplicationMessagesAsync(OpenCodeActivityService.ActiveSession session,
        OpenCodeActivityService.SessionTextMessage? initialMessage,
        IReadOnlyList<OpenCodeActivityService.SessionTextMessage> messages, CancellationToken cancellationToken = default)
    {
        if (!CanSendNotifications || IsSessionMuted(session.Id))
            return;
        if ((!_sessionThreads.TryGetValue(session.Id, out var existingLink) || existingLink.Platform != _activePlatform) &&
            initialMessage is null && messages.Count == 0)
            return;
        var title = IsGenericTitle(session.Title)
            ? GetTitleFromPrompt(initialMessage?.Text ?? messages.FirstOrDefault()?.Text) : session.Title;
        var thread = await EnsureSessionThreadLinkAsync(session, title, cancellationToken);
        var published = _publishedSessionMessages.GetOrAdd(session.Id,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (initialMessage is not null && !thread.InitialPromptPublished && !string.IsNullOrWhiteSpace(initialMessage.Id) &&
            !string.IsNullOrWhiteSpace(initialMessage.Text) && published.TryAdd(initialMessage.Id, 0))
        {
            await PostLongThreadMessageAsync(thread, "*Eerste opdracht:*", initialMessage.Text, cancellationToken);
            thread = thread with { InitialPromptPublished = true };
            _sessionThreads[session.Id] = thread;
            await SaveThreadLinksAsync();
        }
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.Text) || published.ContainsKey(message.Id))
                continue;
            var textHash = GetMessageHash(session.Id, message.Text);
            if (_submittedMessageTexts.TryRemove(textHash, out _))
            {
                published.TryAdd(message.Id, 0);
                continue;
            }
            if (!thread.InitialPromptPublished)
            {
                await PostLongThreadMessageAsync(thread, "*Eerste opdracht:*", message.Text, cancellationToken);
                thread = thread with { InitialPromptPublished = true };
            }
            else
                await PostLongThreadMessageAsync(thread, "*Gebruiker:*", message.Text, cancellationToken);
            published.TryAdd(message.Id, 0);
        }
    }

    public void RememberSlackSubmittedMessage(string sessionId, string text)
    {
        _submittedMessageTexts[GetMessageHash(sessionId, text)] = 0;
    }

    private static string GetMessageHash(string sessionId, string text) =>
        sessionId + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text.Trim())));

    private static string GetTitleFromPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "OpenCode session";
        var lines = prompt.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var taskLabel = Array.FindIndex(lines, line => line.Equals("Task:", StringComparison.OrdinalIgnoreCase));
        var title = taskLabel >= 0 && taskLabel + 1 < lines.Length
            ? lines[taskLabel + 1]
            : lines.FirstOrDefault(line => !line.StartsWith("Ticket:", StringComparison.OrdinalIgnoreCase) &&
                                           !line.StartsWith("Branch:", StringComparison.OrdinalIgnoreCase)) ?? prompt.Trim();
        return NormalizeTitle(title);
    }

    private static bool IsGenericTitle(string? title) => string.IsNullOrWhiteSpace(title) ||
        title.Equals("New session", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("OpenCode session", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitle(string? title)
    {
        var normalized = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
            return "OpenCode session";
        return normalized.Length > 120 ? normalized[..120].TrimEnd() + "…" : normalized;
    }

    private async Task PostLongThreadMessageAsync(ThreadLink thread, string heading, string text,
        CancellationToken cancellationToken)
    {
        var chunkSize = _activePlatform == "Discord" ? 1800 : 3500;
        for (var offset = 0; offset < text.Length; offset += chunkSize)
        {
            var chunk = text.Substring(offset, Math.Min(chunkSize, text.Length - offset));
            var chunkHeading = offset == 0 ? heading + "\n" : "*Vervolg:*\n";
            await PostMessageAsync(_botToken, thread.ChannelId, chunkHeading + EscapeSlack(chunk),
                thread.ThreadTs, cancellationToken);
        }
    }

    private async Task<ThreadLink> EnsureSessionThreadLinkAsync(OpenCodeActivityService.ActiveSession session, string title,
        CancellationToken cancellationToken)
    {
        title = NormalizeTitle(title);
        var selectedChannel = _activePlatform == "Discord" ? _discordChannelId : _channelId;
        if (_sessionThreads.TryGetValue(session.Id, out var existing) && existing.ChannelId == selectedChannel && existing.Platform == _activePlatform)
        {
            var updatedTitle = !string.IsNullOrWhiteSpace(title) && title != "OpenCode session" ? title : existing.Title;
            if (!existing.RootMessageIsTitle || updatedTitle != existing.Title)
            {
                await UpdateThreadTitleAsync(existing.ThreadTs, updatedTitle, cancellationToken);
                existing = existing with { Title = updatedTitle, RootMessageIsTitle = true };
                _sessionThreads[session.Id] = existing;
                await SaveThreadLinksAsync();
                SessionNotificationsChanged?.Invoke();
            }
            return existing;
        }

        var rootMessageId = await PostMessageAsync(_botToken, _channelId,
            _activePlatform == "Discord" ? $"**{title}**" : $"*{EscapeSlack(title)}*", null, cancellationToken);
        var threadTs = rootMessageId;
        if (_activePlatform == "Discord")
            threadTs = await CreateDiscordThreadAsync(threadTs, title, cancellationToken);
            var link = new ThreadLink(threadTs, _activePlatform == "Discord" ? _discordChannelId : _channelId,
            session.Directory, session.Agent, title, DateTime.UtcNow, RootMessageIsTitle: true,
            Platform: _activePlatform, RootMessageId: rootMessageId);
        _sessionThreads[session.Id] = link;
        await SaveThreadLinksAsync();
        SessionNotificationsChanged?.Invoke();
        SessionLinked?.Invoke(session with { Title = title });
        return link;
    }

    private async Task UpdateThreadTitleAsync(string threadTs, string title, CancellationToken cancellationToken)
    {
        await UpdateRootMessageAsync(threadTs, title, cancellationToken);
    }

    private async Task UpdateRootMessageAsync(string threadTs, string title, CancellationToken cancellationToken)
    {
        if (_activePlatform == "Discord")
        {
            var rootMessageId = _sessionThreads.Values.FirstOrDefault(link => link.ThreadTs == threadTs)?.RootMessageId;
            if (string.IsNullOrWhiteSpace(rootMessageId))
                return;
            var channelId = _sessionThreads.Values.FirstOrDefault(link => link.ThreadTs == threadTs)?.ChannelId ?? _discordChannelId;
            using var discordRequest = new HttpRequestMessage(HttpMethod.Patch,
                $"channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(rootMessageId)}");
            discordRequest.Headers.Authorization = new AuthenticationHeaderValue("Bot", _discordBotToken);
            discordRequest.Content = JsonContent.Create(new { content = $"**{EscapeSlack(title)}**" });
            using var discordResponse = await DiscordHttp.SendAsync(discordRequest, cancellationToken);
            using var discordJson = await ReadDiscordResponseAsync(discordResponse, cancellationToken);
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat.update");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
        request.Content = JsonContent.Create(new { channel = _channelId, ts = threadTs, text = $"*{EscapeSlack(title)}*" });
        using var response = await Http.SendAsync(request, cancellationToken);
        using var json = await ReadSlackResponseAsync(response, cancellationToken);
        _ = json.RootElement.GetProperty("ts").GetString();
    }

    public async Task UpdatePendingInteractionsAsync(
        IReadOnlyDictionary<string, OpenCodeActivityService.ActiveSession> sessions,
        IReadOnlyDictionary<string, IReadOnlyList<OpenCodeActivityService.PendingInteraction>> interactions,
        CancellationToken cancellationToken = default)
    {
        if (!CanSendNotifications)
            return;

        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in interactions)
        {
            if (!sessions.TryGetValue(pair.Key, out var session))
                continue;

            var title = string.IsNullOrWhiteSpace(session.Title) || session.Title.Equals("New session", StringComparison.OrdinalIgnoreCase)
                ? "OpenCode session" : session.Title;
            var link = await EnsureSessionThreadLinkAsync(session, title, cancellationToken);

            var current = pair.Value;
            _pendingInteractionThreads[link.ThreadTs] = new PendingInteractionThread(session, current);
            foreach (var interaction in current)
            {
                var key = pair.Key + ":" + interaction.Kind + ":" + interaction.Id;
                activeKeys.Add(key);
                if (!_announcedInteractions.TryAdd(key, 0))
                    continue;
                try
                {
                    await PostMessageAsync(_botToken, link.ChannelId,
                        FormatPendingInteraction(interaction, current), link.ThreadTs, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _announcedInteractions.TryRemove(key, out _);
                    _logger.LogWarning(exception, "Could not publish OpenCode interaction {InteractionId} for session {SessionId} to Slack.",
                        interaction.Id, pair.Key);
                }
            }
        }

        foreach (var key in _announcedInteractions.Keys)
            if (!activeKeys.Contains(key))
                _announcedInteractions.TryRemove(key, out _);
        foreach (var pair in _pendingInteractionThreads)
        {
            var sessionId = pair.Value.Session.Id;
            if (!sessions.ContainsKey(sessionId))
            {
                _pendingInteractionThreads.TryRemove(pair.Key, out _);
                continue;
            }
            if (interactions.TryGetValue(sessionId, out var current) && current.Count > 0)
                _pendingInteractionThreads[pair.Key] = pair.Value with { Interactions = current };
            else
                _pendingInteractionThreads.TryRemove(pair.Key, out _);
        }
    }

    private static string FormatPendingInteraction(OpenCodeActivityService.PendingInteraction interaction,
        IReadOnlyList<OpenCodeActivityService.PendingInteraction> pendingInteractions)
    {
        if (interaction.Kind == "permission")
        {
            var permissionNumber = pendingInteractions.Where(item => item.Kind == "permission")
                .TakeWhile(item => item.Id != interaction.Id).Count() + 1;
            var resources = interaction.Resources.Count == 0 ? string.Empty :
                "\nResources:\n" + string.Join("\n", interaction.Resources.Select(resource => "• " + EscapeSlack(resource)));
            return $"⏸️ *Toestemmingsverzoek {permissionNumber}:* {EscapeSlack(interaction.Details)}\n" +
                $"Request ID: `{EscapeSlack(interaction.Id)}`{resources}\n" +
                $"Antwoord met `!allow {permissionNumber}` (eenmalig), `!always {permissionNumber}` (altijd) " +
                $"of `!deny {permissionNumber} [reden]`. Als er maar één verzoek openstaat, mag je het nummer weglaten. Je kunt ook de ID gebruiken.";
        }

        var formNumber = pendingInteractions.Where(item => item.Kind == "form")
            .TakeWhile(item => item.Id != interaction.Id).Count() + 1;
        var fields = interaction.Fields.Count == 0 ? "(geen zichtbare velden)" : string.Join("\n", interaction.Fields.Select((field, fieldIndex) =>
        {
            var title = EscapeSlack(string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title);
            var options = field.Options.Select((option, optionIndex) =>
                $"   {optionIndex + 1}. {EscapeSlack(option.Label)}")
                .ToList();
            if (field.Type == "boolean" && options.Count == 0)
            {
                options.Add("   1. Ja");
                options.Add("   2. Nee");
            }
            if (field.Custom)
                options.Add($"   {field.Options.Count + 1}. Anders — eigen antwoord");
            var choices = options.Count == 0
                ? (field.Custom ? "\n   Antwoord met `!answer " + (interaction.Fields.Count == 1 ? "tekst <antwoord>" : $"{fieldIndex + 1} tekst <antwoord>") + "`." : string.Empty)
                : "\n" + string.Join("\n", options);
            var description = string.IsNullOrWhiteSpace(field.Description) ? string.Empty :
                $"\n   {EscapeSlack(field.Description)}";
            return $"{fieldIndex + 1}. *{title}* (`{EscapeSlack(field.Key)}`{(field.Required ? ", verplicht" : string.Empty)})" + description + choices;
        }));
        var multipleForms = pendingInteractions.Count(item => item.Kind == "form") > 1;
        return $"❓ *OpenCode-formulier {formNumber}:* {EscapeSlack(interaction.Title)}\n{fields}\n" +
            (interaction.Fields.Count == 1 && !multipleForms
                ? $"Kies met `!answer <optienummer>`; bij ‘Anders’ gebruik je `!answer {interaction.Fields[0].Options.Count + 1} <eigen antwoord>`."
                : multipleForms && interaction.Fields.Count == 1
                    ? "Antwoord met `!answer <formuliernummer> <optienummer>`; voeg bij ‘Anders’ je eigen tekst toe na het optienummer."
                    : multipleForms
                    ? "Antwoord met `!answer <formuliernummer> <veldnummer> <optienummer>`; bij ‘Anders’ voeg je je tekst toe na het optienummer. Combineer meerdere velden met puntkomma's."
                    : "Antwoord per veld met `!answer <veldnummer> <optienummer>`; bij ‘Anders’ voeg je je tekst toe na het optienummer. Combineer meerdere velden met puntkomma's.") +
            $" Je kunt ook de bestaande syntax met ID en veldnamen gebruiken: `!answer {EscapeSlack(interaction.Id)} sleutel=waarde`.";
    }

    private async Task<bool> HandlePendingInteractionReplyAsync(string threadTs, string text,
        CancellationToken cancellationToken)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text,
            "^!(allow|always|deny|answer)(?:\\s+(.*))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success)
            return false;

        if (!_pendingInteractionThreads.TryGetValue(threadTs, out var pending))
        {
            await PostMessageAsync(_botToken, _channelId,
                "⚠️ Er staat geen OpenCode-vraag of toestemmingsverzoek open in deze thread.", threadTs, cancellationToken);
            return true;
        }

        var command = match.Groups[1].Value.ToLowerInvariant();
        var arguments = match.Groups[2].Value.Trim();
        var kind = command == "answer" ? "form" : "permission";
        var matchingInteractions = pending.Interactions.Where(item => item.Kind == kind).ToArray();
        var firstToken = arguments.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var explicitId = firstToken is not null && matchingInteractions.Any(item => item.Id == firstToken);
        var byId = explicitId ? matchingInteractions.First(item => item.Id == firstToken) : null;
        var numericIndex = !explicitId && int.TryParse(firstToken, out var parsedIndex) ? parsedIndex : 0;
        var formIndexSelect = command == "answer" && !explicitId && matchingInteractions.Length > 1 &&
            numericIndex > 0 && numericIndex <= matchingInteractions.Length;
        var interaction = byId ?? (command == "answer"
            ? matchingInteractions.Length == 1 ? matchingInteractions[0]
                : formIndexSelect ? matchingInteractions[numericIndex - 1] : null
            : numericIndex > 0 && numericIndex <= matchingInteractions.Length
                ? matchingInteractions[numericIndex - 1]
                : !explicitId && string.IsNullOrWhiteSpace(arguments) && matchingInteractions.Length == 1
                    ? matchingInteractions[0]
                    : command == "deny" && !explicitId && numericIndex == 0 && matchingInteractions.Length == 1
                        ? matchingInteractions[0] : null);
        var id = interaction?.Id ?? firstToken ?? string.Empty;
        var numberedPermission = kind == "permission" && numericIndex > 0;
        var body = explicitId || formIndexSelect || numberedPermission
            ? arguments[firstToken!.Length..].Trim()
            : arguments;
        if (interaction is null)
        {
            var response = matchingInteractions.Length == 0
            ? "⚠️ Er staat geen passend verzoek meer open in deze thread."
                : matchingInteractions.Length > 1 && string.IsNullOrWhiteSpace(arguments)
                    ? command == "answer"
                        ? "⚠️ Er staan meerdere formulieren open. Kies eerst het formuliernummer, bijvoorbeeld `!answer 1 <antwoord>`; de veld-/optienummers staan in dat formulier."
                        : "⚠️ Er staan meerdere verzoeken open. Kies het getoonde nummer, bijvoorbeeld `!allow 1`."
                    : "⚠️ Onbekend verzoek of nummer. Gebruik een getoond nummer of de ID uit de vraag.";
            await PostMessageAsync(_botToken, _channelId, response,
                threadTs, cancellationToken);
            return true;
        }

        try
        {
            if (kind == "permission")
            {
                if (numericIndex == 0 && !explicitId &&
                    (string.IsNullOrWhiteSpace(arguments) || command is "allow" or "always") && matchingInteractions.Length != 1)
                {
                    await PostMessageAsync(_botToken, _channelId,
                        "⚠️ Geef het nummer van het toestemmingsverzoek op, bijvoorbeeld `!allow 1`.", threadTs, cancellationToken);
                    return true;
                }
                var decision = command switch { "allow" => "once", "always" => "always", _ => "reject" };
                if (numericIndex == 0 && !explicitId && (command is "allow" or "always") && !string.IsNullOrWhiteSpace(arguments))
                {
                    await PostMessageAsync(_botToken, _channelId,
                        "⚠️ Gebruik `!allow` zonder ID bij één verzoek, of geef het getoonde verzoeknummer op.",
                        threadTs, cancellationToken);
                    return true;
                }
                var permissionReason = !explicitId && numericIndex == 0 && command == "deny" ? arguments : body;
                await _openCodeActivity.ReplyToPermissionAsync(pending.Session.Id, pending.Session.Directory,
                    id, decision, permissionReason, cancellationToken);
                await PostMessageAsync(_botToken, _channelId,
                    decision == "reject" ? "✅ Toestemmingsverzoek afgewezen." :
                        decision == "always" ? "✅ Toestemming altijd verleend." : "✅ Eenmalige toestemming verleend.",
                    threadTs, cancellationToken);
                return true;
            }

            var answers = explicitId
                ? ParseFormAnswers(body, interaction.Fields)
                : ParseNumberedFormAnswers(body, interaction.Fields, formIndexSelect && interaction.Fields.Count > 1);
            await _openCodeActivity.ReplyToFormAsync(pending.Session.Id, pending.Session.Directory,
                id, answers, cancellationToken);
            await PostMessageAsync(_botToken, _channelId, "✅ Antwoord naar OpenCode verzonden.", threadTs, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not submit Slack answer for OpenCode interaction {InteractionId}.", id);
            await PostMessageAsync(_botToken, _channelId,
                "⚠️ Antwoord niet verwerkt: " + EscapeSlack(exception.Message), threadTs, cancellationToken);
            return true;
        }
    }

    private static JsonElement ParseNumberedFormAnswers(string text,
        IReadOnlyList<OpenCodeActivityService.FormField> fields, bool forceFieldNumber = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Geef een optienummer of een eigen antwoord op.");
        if (fields.Count == 0)
            throw new InvalidOperationException("Dit formulier bevat geen zichtbare velden.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            var numberedFields = fields.Count > 1 || forceFieldNumber;
            var fieldIndex = !numberedFields ? 1 : parts.Length > 0 && int.TryParse(parts[0], out var fieldNumber)
                ? fieldNumber : 0;
            if (fieldIndex < 1 || fieldIndex > fields.Count)
                throw new InvalidOperationException(fields.Count == 1
                    ? "Kies een optienummer of gebruik `!answer tekst <antwoord>`."
                    : "Gebruik `!answer <veldnummer> <optienummer>`.");
            var field = fields[fieldIndex - 1];
            if (parts.Length == 0 || numberedFields && parts.Length < 2)
                throw new InvalidOperationException("Voeg na het veldnummer een optienummer toe. Bijvoorbeeld `!answer 1 2`.");
            var choiceToken = !numberedFields ? parts.FirstOrDefault() : parts.Skip(1).FirstOrDefault();
            var freeText = !numberedFields && parts.Length > 1 ? string.Join(' ', parts.Skip(1)) :
                parts.Length > 2 ? parts[2] : string.Empty;
            object parsed;
            if (string.Equals(choiceToken, "tekst", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(choiceToken, "text", StringComparison.OrdinalIgnoreCase))
            {
                if (!field.Custom || string.IsNullOrWhiteSpace(freeText))
                    throw new InvalidOperationException($"Veld '{field.Key}' accepteert hier geen eigen tekstantwoord.");
                parsed = ParseFieldValue(field, freeText);
            }
            else if (field.Type == "multiselect" && choiceToken is not null &&
                     choiceToken.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .All(part => int.TryParse(part, out _)))
            {
                var selectedNumbers = choiceToken.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (selectedNumbers.Length == 0 || selectedNumbers.Any(number => number < 1 || number > field.Options.Count))
                    throw new InvalidOperationException($"Een of meer optienummers zijn ongeldig voor '{field.Key}'.");
                parsed = selectedNumbers.Select(number => field.Options[number - 1].Value).Distinct(StringComparer.Ordinal).ToArray();
            }
            else if (int.TryParse(choiceToken, out var choiceNumber))
            {
                if (field.Type == "boolean" && field.Options.Count == 0 && choiceNumber is 1 or 2)
                {
                    parsed = choiceNumber == 1;
                    values[field.Key] = parsed;
                    continue;
                }
                var customNumber = field.Options.Count + 1;
                if (field.Custom && choiceNumber == customNumber)
                {
                    if (string.IsNullOrWhiteSpace(freeText))
                        throw new InvalidOperationException("Voeg je eigen antwoord toe na het nummer van ‘Anders’.");
                    parsed = ParseFieldValue(field, freeText);
                }
                else if (choiceNumber >= 1 && choiceNumber <= field.Options.Count)
                {
                    var selected = field.Options[choiceNumber - 1].Value;
                    parsed = field.Type == "multiselect" ? new[] { selected } : ParseFieldValue(field, selected);
                }
                else
                    throw new InvalidOperationException($"Optienummer {choiceNumber} is ongeldig voor '{field.Key}'.");
            }
            else if (field.Options.Count == 0 && (field.Type is "integer" or "number" or "boolean"))
                parsed = ParseFieldValue(field, choiceToken ?? string.Empty);
            else if (!numberedFields && field.Custom && !string.IsNullOrWhiteSpace(text))
                parsed = ParseFieldValue(field, text);
            else
                throw new InvalidOperationException("Gebruik een optienummer. Voor een eigen antwoord: `!answer tekst <antwoord>`.");
            values[field.Key] = parsed;
        }
        ValidateRequiredFields(values, fields);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.Clone();
    }

    private static object ParseFieldValue(OpenCodeActivityService.FormField field, string value) => field.Type switch
    {
        "boolean" when bool.TryParse(value, out var boolean) => boolean,
        "integer" when long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var integer) => integer,
        "number" when double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        "multiselect" => value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        "boolean" or "integer" or "number" => throw new InvalidOperationException($"Ongeldige waarde voor veld '{field.Key}'."),
        _ => value
    };

    private static void ValidateRequiredFields(IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<OpenCodeActivityService.FormField> fields)
    {
        foreach (var field in fields.Where(field => field.Required))
            if (!values.ContainsKey(field.Key))
                throw new InvalidOperationException($"Verplicht veld '{field.Key}' ontbreekt. Beantwoord alle verplichte velden met puntkomma's ertussen.");
    }

    private static JsonElement ParseFormAnswers(string text, IReadOnlyList<OpenCodeActivityService.FormField> fields)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Gebruik `!answer <id> sleutel=waarde; sleutel2=waarde`.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fieldMap = fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var entry in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                throw new InvalidOperationException($"Ongeldig antwoord '{entry}'. Gebruik sleutel=waarde.");
            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            if (!fieldMap.TryGetValue(key, out var field))
                throw new InvalidOperationException($"Onbekend veld '{key}'. Gebruik de veldnamen uit de vraag.");
            var parsed = ParseFieldValue(field, value);
            if (field.Options.Count > 0)
            {
                var permitted = field.Type == "multiselect" && parsed is string[] selected
                    ? selected : parsed is string single ? [single] : [];
                foreach (var selectedValue in permitted)
                    if (!field.Options.Any(option => option.Value == selectedValue) && !field.Custom)
                        throw new InvalidOperationException($"'{selectedValue}' is geen geldige optie voor '{key}'.");
            }
            values[key] = parsed;
        }
        ValidateRequiredFields(values, fields);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.Clone();
    }

    public async Task UpdateSessionAgentAsync(string sessionId, string agent)
    {
        if (agent is not ("build" or "plan") || !_sessionThreads.TryGetValue(sessionId, out var link) ||
            link.Platform != _activePlatform || link.ChannelId != (_activePlatform == "Discord" ? _discordChannelId : _channelId))
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
                _logger.LogWarning("Slack Socket Mode connection failed ({ExceptionType}); retrying after {RetryDelaySeconds} seconds.",
                    exception.GetType().Name, delay.TotalSeconds);
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
            _ = HandleMessageEventSafelyAsync(payload.Clone(), slackEvent.Clone(), cancellationToken);
        }
    }

    private async Task HandleMessageEventSafelyAsync(JsonElement payload, JsonElement slackEvent,
        CancellationToken cancellationToken)
    {
        var eventChannel = slackEvent.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.String
            ? channel.GetString() : null;
        var repositoryPath = eventChannel is null ? null : _sessionThreads.Values
            .FirstOrDefault(link => link.ChannelId == eventChannel)?.Directory;
        using var repositoryScope = !string.IsNullOrWhiteSpace(repositoryPath)
            ? _logger.BeginScope(new Dictionary<string, object?> { ["RepositoryPath"] = repositoryPath })
            : null;
        try
        {
            await HandleMessageEventAsync(payload, slackEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not process Slack message event.");
            if (slackEvent.TryGetProperty("thread_ts", out var threadValue) && threadValue.GetString() is { Length: > 0 } threadTs)
            {
                try
                {
                    await PostMessageAsync(_botToken, _channelId,
                        "Could not process this request: " + EscapeSlack(exception.Message), threadTs, cancellationToken);
                }
                catch (Exception postException)
                {
                    _logger.LogWarning(postException, "Could not report Slack message processing failure.");
                }
            }
        }
    }

    private async Task HandleMessageEventAsync(JsonElement payload, JsonElement slackEvent, CancellationToken cancellationToken)
    {
        var expectedChannel = _activePlatform == "Discord" ? _discordChannelId : _channelId;
        var expectedUser = _activePlatform == "Discord" ? _discordAllowedUserId : _allowedUserId;
        if (slackEvent.TryGetProperty("bot_id", out _) ||
            !slackEvent.TryGetProperty("channel", out var channelValue) || channelValue.GetString() != expectedChannel ||
            !slackEvent.TryGetProperty("user", out var userValue) || userValue.GetString() != expectedUser ||
            !slackEvent.TryGetProperty("text", out var textValue) || string.IsNullOrWhiteSpace(textValue.GetString()) ||
            !slackEvent.TryGetProperty("ts", out var tsValue))
            return;

        var eventId = payload.TryGetProperty("event_id", out var idValue) ? idValue.GetString() : null;
        var ts = tsValue.GetString();
        if (string.IsNullOrWhiteSpace(ts) || !_seenReplies.TryAdd(eventId ?? ts, 0))
            return;
        var text = textValue.GetString()!.Trim();
        var threadTs = slackEvent.TryGetProperty("thread_ts", out var threadValue) ? threadValue.GetString() : null;
        if (_activePlatform == "Discord" && string.IsNullOrWhiteSpace(threadTs) &&
            slackEvent.TryGetProperty("root_ts", out var rootValue))
            threadTs = rootValue.GetString();
        if (string.IsNullOrWhiteSpace(threadTs))
        {
            if (text.Equals("!help", StringComparison.OrdinalIgnoreCase))
            {
                await PostMessageAsync(_botToken, _channelId,
                "*CodeBadger commands*\n• `!new` — start a new OpenCode session; then choose a repository and send a task in its thread.\n• `!todo <text>` — add an item; the first sentence (up to 160 characters) becomes its title and the rest its description. Optional type prefixes: `idea:`, `wish:`, `task:`, `bug:`, or `story:`.\n• `!todos` — list up to 10 most recently created open items.\n• `!help` — show this command list.",
                    null, cancellationToken);
                return;
            }

            if (text.Equals("!todos", StringComparison.OrdinalIgnoreCase))
            {
                await ListOpenIdeasFromSlackAsync(null, cancellationToken);
                return;
            }

            if (text.Equals("!todo", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("!todo ", StringComparison.OrdinalIgnoreCase))
            {
                await AddIdeaFromSlackAsync(text, null, cancellationToken);
                return;
            }

            if (text.Equals("!new", StringComparison.OrdinalIgnoreCase))
            {
                await BeginNewSessionWizardAsync(cancellationToken);
                return;
            }

            _seenReplies.TryRemove(eventId ?? ts, out _);
            return;
        }

        if (_pendingNewSessions.TryGetValue(threadTs, out var pendingNewSession))
        {
            if (text.Equals("!help", StringComparison.OrdinalIgnoreCase))
            {
                await PostMessageAsync(_botToken, _channelId,
                    "*New session setup*\nReply with a repository number, then send the task for the new OpenCode session. `!help` shows this list.",
                    threadTs, cancellationToken);
                return;
            }

            await HandleNewSessionWizardReplyAsync(threadTs, pendingNewSession, text, cancellationToken);
            return;
        }

        if (text.Equals("!todos", StringComparison.OrdinalIgnoreCase))
        {
            await ListOpenIdeasFromSlackAsync(threadTs, cancellationToken);
            return;
        }

        if (text.Equals("!todo", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("!todo ", StringComparison.OrdinalIgnoreCase))
        {
            await AddIdeaFromSlackAsync(text, threadTs, cancellationToken);
            return;
        }

        var linkedSession = _sessionThreads.FirstOrDefault(pair => pair.Value.ThreadTs == threadTs &&
            pair.Value.Platform == _activePlatform && pair.Value.ChannelId == (_activePlatform == "Discord" ? _discordChannelId : _channelId));
        if (string.IsNullOrWhiteSpace(linkedSession.Key))
            return;
        if (text.Equals("!help", StringComparison.OrdinalIgnoreCase))
        {
            await PostMessageAsync(_botToken, _channelId,
                "*Session commands*\n• `!status` — check whether OpenCode is currently working.\n• `!build <task>` — switch to Build and send the task.\n• `!plan <task>` — switch to Plan and send the task.\n• `!allow [number|id]` / `!always [number|id]` / `!deny [number|id] [reason]` — answer a permission request; the number may be omitted when only one is pending.\n• `!answer <option>` — answer a single-field form by option number; use `!answer <field> <option>` for forms with multiple fields and `;` between fields. Append text after the “Other” option for a custom answer.\n• `!answer <id> key=value; key2=value` — legacy form syntax.\n• `!commit` — stage and commit all repository changes.\n• `!push` — push using the same safe up-to-date check as the Git dialog.\n• `!help` — show this command list.\n\nAny other reply is sent to the current session.",
                threadTs, cancellationToken);
            return;
        }
        if (text.Equals("!status", StringComparison.OrdinalIgnoreCase))
        {
            var active = await _openCodeActivity.IsSessionActiveAsync(linkedSession.Key,
                linkedSession.Value.Directory, cancellationToken);
            var status = active switch
            {
                true => "⏳ OpenCode is momenteel bezig met deze sessie.",
                false => "✅ OpenCode is momenteel niet bezig; de sessie is idle.",
                _ => "⚠️ De actuele status van OpenCode kon niet worden opgehaald."
            };
            await PostMessageAsync(_botToken, _channelId, status, threadTs, cancellationToken);
            return;
        }
        if (await HandlePendingInteractionReplyAsync(threadTs, text, cancellationToken))
            return;
        if (_pendingCommitMessages.TryGetValue(threadTs, out var pendingCommit))
        {
            await CompletePendingCommitAsync(threadTs, pendingCommit, text, cancellationToken);
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Equals("!commit", StringComparison.OrdinalIgnoreCase))
                await StartCommitCommandAsync(linkedSession.Key, linkedSession.Value, threadTs, cancellationToken);
            else if (text.Equals("!push", StringComparison.OrdinalIgnoreCase))
                await PushSessionAsync(linkedSession.Value, threadTs, cancellationToken);
            else
            {
                var session = new OpenCodeActivityService.ActiveSession(linkedSession.Key,
                    linkedSession.Value.Directory, linkedSession.Value.Agent, linkedSession.Value.Title);
                ReplyReceived?.Invoke(session, text);
            }
        }
        await Task.CompletedTask;
    }

    private async Task AddIdeaFromSlackAsync(string command, string? threadTs, CancellationToken cancellationToken)
    {
        var text = command.Length > "!todo".Length ? command["!todo".Length..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            await PostMessageAsync(_botToken, _channelId,
                "Usage: `!todo <text>`. The first sentence becomes the title and the remaining text becomes the description. Optional prefixes: `idea:`, `wish:`, `task:`, `bug:`, or `story:`. Defaults to story.",
                threadTs, cancellationToken);
            return;
        }

        var type = IdeaType.Story;
        foreach (var candidate in Enum.GetValues<IdeaType>())
        {
            var prefix = candidate.ToString().ToLowerInvariant() + ":";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            type = candidate;
            text = text[prefix.Length..].Trim();
            break;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            await PostMessageAsync(_botToken, _channelId, "Add text after the item type.", threadTs, cancellationToken);
            return;
        }

        var (title, description) = SplitSlackTodoTitleAndDescription(text);
        var item = await _ideaListService.AddAsync(title, description, type, cancellationToken);
        await PostMessageAsync(_botToken, _channelId,
            $"✅ Added *{EscapeSlack(item.Title)}* to the CodeBadger ideas and tasks list ({type.ToString().ToLowerInvariant()}).",
            threadTs, cancellationToken);
    }

    private static (string Title, string Description) SplitSlackTodoTitleAndDescription(string text)
    {
        const int maxTitleLength = 160;
        var firstSentenceEnd = -1;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '\r' or '\n')
            {
                firstSentenceEnd = index;
                break;
            }

            if (text[index] is '.' or '!' or '?' &&
                (index == text.Length - 1 || char.IsWhiteSpace(text[index + 1])))
            {
                firstSentenceEnd = index + 1;
                break;
            }
        }

        if (firstSentenceEnd < 0)
            firstSentenceEnd = text.Length;

        var firstSentence = text[..firstSentenceEnd].Trim();
        if (firstSentence.Length <= maxTitleLength)
        {
            var descriptionStart = firstSentenceEnd;
            while (descriptionStart < text.Length && char.IsWhiteSpace(text[descriptionStart]))
                descriptionStart++;
            return (firstSentence, descriptionStart < text.Length ? text[descriptionStart..].Trim() : string.Empty);
        }

        var title = text[..maxTitleLength].TrimEnd();
        var remainderStart = maxTitleLength;
        while (remainderStart < text.Length && char.IsWhiteSpace(text[remainderStart]))
            remainderStart++;
        return (title, remainderStart < text.Length ? text[remainderStart..].Trim() : string.Empty);
    }

    private async Task ListOpenIdeasFromSlackAsync(string? threadTs, CancellationToken cancellationToken)
    {
        var openItems = (await _ideaListService.GetAllAsync(cancellationToken))
            .Where(item => item.Status == IdeaStatus.Open)
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToArray();
        if (openItems.Length == 0)
        {
            await PostMessageAsync(_botToken, _channelId, "✅ Er zijn geen open punten.", threadTs, cancellationToken);
            return;
        }

        var selected = openItems.Take(10).ToArray();
        var lines = selected.Select(item =>
            $"• *{EscapeSlack(item.Title)}* — {item.Type.ToString().ToLowerInvariant()}, {item.CreatedAt.ToLocalTime():yyyy-MM-dd}");
        var heading = openItems.Length > selected.Length
            ? $"*Open punten (10 nieuwste van {openItems.Length}, nieuwste eerst)*\n"
            : $"*Open punten ({openItems.Length}, nieuwste eerst)*\n";
        await PostMessageAsync(_botToken, _channelId, heading + string.Join("\n", lines), threadTs, cancellationToken);
    }

    private async Task PushSessionAsync(ThreadLink thread, string threadTs, CancellationToken cancellationToken)
    {
        var settings = await _settingsService.LoadAsync();
        var repository = settings.Repositories.FirstOrDefault(candidate =>
            AgentTools.Normalize(candidate.AgentTool) == AgentTools.OpenCode &&
            string.Equals(OpenCodeActivityService.NormalizePath(candidate.Path),
                OpenCodeActivityService.NormalizePath(thread.Directory), StringComparison.OrdinalIgnoreCase));
        if (repository is null || repository.OpenCodeSandboxEnabled)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Push is only available for a configured, non-sandboxed OpenCode repository.", threadTs, cancellationToken);
            return;
        }

        await PostMessageAsync(_botToken, _channelId, "⏳ Pushing changes using the Git dialog's safe push check…",
            threadTs, cancellationToken);
        try
        {
            var result = await _gitBranchService.PushWhenUpToDateAsync(thread.Directory);
            await PostMessageAsync(_botToken, _channelId,
                result.Success ? "✅ Push completed." : "❌ Push failed: " + EscapeSlack(result.Message),
                threadTs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not push repository {Directory} from {Platform}.", thread.Directory, _activePlatform);
            await PostMessageAsync(_botToken, _channelId,
                "❌ Push failed: " + EscapeSlack(exception.Message), threadTs, cancellationToken);
        }
    }

    private async Task StartCommitCommandAsync(string sessionId, ThreadLink thread, string threadTs,
        CancellationToken cancellationToken)
    {
        try
        {
            await StartCommitCommandCoreAsync(sessionId, thread, threadTs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Could not run the commit command: " + EscapeSlack(exception.Message), threadTs, cancellationToken);
        }
    }

    private async Task StartCommitCommandCoreAsync(string sessionId, ThreadLink thread, string threadTs,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.LoadAsync();
        var repository = settings.Repositories.FirstOrDefault(candidate =>
            AgentTools.Normalize(candidate.AgentTool) == AgentTools.OpenCode &&
            string.Equals(OpenCodeActivityService.NormalizePath(candidate.Path),
                OpenCodeActivityService.NormalizePath(thread.Directory), StringComparison.OrdinalIgnoreCase));
        if (repository is null || repository.OpenCodeSandboxEnabled)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Commit is only available for a configured, non-sandboxed OpenCode repository.", threadTs, cancellationToken);
            return;
        }

        var stage = await _gitBranchService.StageAllChangesAsync(thread.Directory);
        if (!stage.Success)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Could not stage repository changes: " + EscapeSlack(stage.Message), threadTs, cancellationToken);
            return;
        }

        var diff = await _gitBranchService.GetStagedDiffAsync(thread.Directory);
        if (!diff.Success)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Could not read staged changes: " + EscapeSlack(diff.Message), threadTs, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(diff.Output))
        {
            await PostMessageAsync(_botToken, _channelId, "There are no changes to commit.", threadTs, cancellationToken);
            return;
        }

        var promptSettings = settings;
        var canGenerate = !string.IsNullOrWhiteSpace(promptSettings.AiApiKey) ||
            promptSettings.AiProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
            promptSettings.AiProvider.Equals("Custom URL", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(promptSettings.AiCustomUrl);
        if (canGenerate)
        {
            var branch = await _gitBranchService.GetCurrentBranchAsync(thread.Directory);
            var generated = await _aiCommitMessageService.GenerateAsync(promptSettings, diff.Output, branch.Branch,
                repository.AiCustomInstruction);
            if (generated.Success)
            {
                await CommitStagedChangesAsync(thread.Directory, generated.Title, generated.Description, threadTs, cancellationToken);
                return;
            }

            await PostMessageAsync(_botToken, _channelId,
                "AI could not generate a commit message (" + EscapeSlack(generated.Error) + "). Reply in this thread with a commit title; you may add a description on the next line.",
                threadTs, cancellationToken);
            _pendingCommitMessages[threadTs] = new PendingCommitMessage(sessionId, thread.Directory, string.Empty, string.Empty);
            return;
        }

        await PostMessageAsync(_botToken, _channelId,
            "Changes are staged. Reply in this thread with a commit title; you may add a description on the next line.",
            threadTs, cancellationToken);
        _pendingCommitMessages[threadTs] = new PendingCommitMessage(sessionId, thread.Directory, string.Empty, string.Empty);
    }

    private async Task CompletePendingCommitAsync(string threadTs, PendingCommitMessage pending, string message,
        CancellationToken cancellationToken)
    {
        var lines = message.Split('\n', 2, StringSplitOptions.TrimEntries);
        var title = lines[0].Trim();
        var description = lines.Length > 1 ? lines[1].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 72)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Commit title must contain 1–72 characters. Reply with a title and optional description.",
                threadTs, cancellationToken);
            return;
        }

        if (await CommitStagedChangesAsync(pending.Directory, title, description, threadTs, cancellationToken))
            _pendingCommitMessages.TryRemove(threadTs, out _);
    }

    private async Task<bool> CommitStagedChangesAsync(string directory, string title, string description, string threadTs,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(description) ? title.Trim() : $"{title.Trim()}\n\n{description.Trim()}";
        var result = await _gitBranchService.CommitAsync(directory, message);
        await PostMessageAsync(_botToken, _channelId,
            result.Success ? "✅ Commit created: " + EscapeSlack(title.Trim()) : "❌ Commit failed: " + EscapeSlack(result.Message),
            threadTs, cancellationToken);
        return result.Success;
    }

    private async Task BeginNewSessionWizardAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.LoadAsync();
        var repositories = settings.Repositories
            .Where(repository => repository.IsActive && !repository.OpenCodeSandboxEnabled &&
                AgentTools.Normalize(repository.AgentTool) == AgentTools.OpenCode && Directory.Exists(repository.Path))
            .OrderBy(repository => string.IsNullOrWhiteSpace(repository.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path)) : repository.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (repositories.Length == 0)
        {
            await PostMessageAsync(_botToken, _channelId,
                "No eligible repositories are available. Activate a non-sandboxed OpenCode repository in CodeBadger Settings.",
                null, cancellationToken);
            return;
        }

        var lines = repositories.Select((repository, index) =>
        {
            var name = string.IsNullOrWhiteSpace(repository.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path)) : repository.Name;
            return $"{index + 1}. {EscapeSlack(name)}";
        });
        var rootMessageId = await PostMessageAsync(_botToken, _channelId,
            "*Start a new OpenCode session*\nReply in this thread with a repository number:\n" + string.Join("\n", lines),
            null, cancellationToken);
        var threadTs = _activePlatform == "Discord"
            ? await CreateDiscordThreadAsync(rootMessageId, "New OpenCode session", cancellationToken)
            : rootMessageId;
        _pendingNewSessions[threadTs] = new PendingNewSession(repositories, rootMessageId);
    }

    private async Task HandleNewSessionWizardReplyAsync(string threadTs, PendingNewSession pending,
        string text, CancellationToken cancellationToken)
    {
        if (pending.SelectedRepository is null)
        {
            if (!int.TryParse(text, out var selection) || selection < 1 || selection > pending.Repositories.Count)
            {
                await PostMessageAsync(_botToken, _channelId,
                    $"Reply with a number from 1 to {pending.Repositories.Count} to choose a repository.", threadTs, cancellationToken);
                return;
            }

            pending.SelectedRepository = pending.Repositories[selection - 1];
            var repositoryName = string.IsNullOrWhiteSpace(pending.SelectedRepository.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(pending.SelectedRepository.Path))
                : pending.SelectedRepository.Name;
            await PostMessageAsync(_botToken, _channelId,
                $"Selected *{EscapeSlack(repositoryName)}*. Reply in this thread with the task for OpenCode.",
                threadTs, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Send a task between 1 and 32,000 characters to start the session.", threadTs, cancellationToken);
            return;
        }

        var repository = pending.SelectedRepository;
        _pendingNewSessions.TryRemove(threadTs, out _);
        await PostMessageAsync(_botToken, _channelId,
            "Starting a new OpenCode session and sending your task…", threadTs, cancellationToken);

        OpenCodeActivityService.ActiveSession session;
        try
        {
            session = await _openCodeActivity.CreateSessionAsync(repository.Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _pendingNewSessions.TryRemove(threadTs, out _);
            await PostMessageAsync(_botToken, _channelId,
                "Could not start the OpenCode session: " + EscapeSlack(exception.Message) + "\nSend `!new` to try again.",
                threadTs, cancellationToken);
            return;
        }

        var title = IsGenericTitle(session.Title)
            ? GetTitleFromPrompt(text) : session.Title;
        var rootMessageId = string.IsNullOrWhiteSpace(pending.RootMessageId) ? threadTs : pending.RootMessageId;
        _sessionThreads[session.Id] = new ThreadLink(threadTs,
            _activePlatform == "Discord" ? _discordChannelId : _channelId, session.Directory, session.Agent, title,
            DateTime.UtcNow, InitialPromptPublished: true, RootMessageIsTitle: true,
            Platform: _activePlatform, RootMessageId: rootMessageId);
        RememberSlackSubmittedMessage(session.Id, text);
        await SaveThreadLinksAsync();
        SessionNotificationsChanged?.Invoke();
        SessionStarted?.Invoke(session with { Title = title });
        await UpdateRootMessageAsync(threadTs, title, cancellationToken);
        await SaveThreadLinksAsync();
        var selectedName = string.IsNullOrWhiteSpace(repository.Name)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path)) : repository.Name;
        await PostMessageAsync(_botToken, _channelId,
            $"✅ Started a new OpenCode session in *{EscapeSlack(selectedName)}*. Session: `{EscapeSlack(session.Id)}`",
            threadTs, cancellationToken);

        try
        {
            await _openCodeActivity.SendPromptAsync(session.Id, session.Directory, text, cancellationToken, "plan");
            await PostMessageAsync(_botToken, _channelId,
                "✅ OpenCode accepted your task and started the session in Plan mode.", threadTs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Could not send the task to the new session: " + EscapeSlack(exception.Message), threadTs, cancellationToken);
        }
    }

    private async Task LoadThreadLinksAsync(string platform, string channelId)
    {
        string? json = null;
        var filePath = Path.Combine(FileSystem.AppDataDirectory, ThreadLinksFileName);
        if (File.Exists(filePath))
            json = await File.ReadAllTextAsync(filePath);
        else
        {
            try { json = await SecureStorage.Default.GetAsync(ThreadLinksStorageKey); }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not read legacy Slack thread links from secure storage; they will be recreated as needed.");
            }
        }
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var links = JsonSerializer.Deserialize<Dictionary<string, ThreadLink>>(json);
            if (links is null)
                return;
            foreach (var pair in links.Where(pair => pair.Value.ChannelId == channelId && pair.Value.Platform == platform)
                         .OrderByDescending(pair => pair.Value.CreatedAt).Take(100))
                if (pair.Key.StartsWith("ses_", StringComparison.Ordinal))
                    _sessionThreads[pair.Key] = pair.Value;
        }
        catch (JsonException) { }
    }

    private async Task SaveThreadLinksAsync()
    {
        await _threadLinksStorageGate.WaitAsync();
        try
        {
            var links = _sessionThreads.OrderByDescending(pair => pair.Value.CreatedAt).Take(100)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var filePath = Path.Combine(FileSystem.AppDataDirectory, ThreadLinksFileName);
            var temporaryPath = filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(links));
            File.Move(temporaryPath, filePath, overwrite: true);
            try { SecureStorage.Default.Remove(ThreadLinksStorageKey); }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Could not remove legacy Slack thread links from secure storage.");
            }
        }
        finally { _threadLinksStorageGate.Release(); }
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

    private async Task<string> PostMessageAsync(string botToken, string channelId, string text,
        string? threadTs, CancellationToken cancellationToken)
    {
        if (_activePlatform == "Discord")
        {
            var discordChannel = _discordChannelId;
            if (!string.IsNullOrWhiteSpace(threadTs) &&
                (_sessionThreads.Values.Any(link => link.Platform == "Discord" && link.ThreadTs == threadTs) ||
                 _pendingNewSessions.ContainsKey(threadTs)))
                discordChannel = threadTs;
            using var discordRequest = new HttpRequestMessage(HttpMethod.Post,
                $"channels/{Uri.EscapeDataString(discordChannel)}/messages");
            discordRequest.Headers.Authorization = new AuthenticationHeaderValue("Bot", _discordBotToken);
            discordRequest.Content = JsonContent.Create(new
            {
                content = ConvertSlackFormatting(text),
                allowed_mentions = new { parse = Array.Empty<string>() }
            });
            using var discordResponse = await DiscordHttp.SendAsync(discordRequest, cancellationToken);
            using var discordJson = await ReadDiscordResponseAsync(discordResponse, cancellationToken);
            return discordJson.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Discord did not return the notification message ID.");
        }
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

    private async Task RunDiscordGatewayLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), cancellationToken);
                await ReceiveDiscordEventsAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                _logger.LogWarning("Discord Gateway connection failed ({ExceptionType}); retrying after {RetryDelaySeconds} seconds.",
                    exception.GetType().Name, delay.TotalSeconds);
                SetConnectionState(false, "Discord connection unavailable: " + exception.Message);
            }
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveDiscordEventsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long? sequence = null;
        var delay = TimeSpan.FromSeconds(2);
        using var gatewaySendGate = new SemaphoreSlim(1, 1);
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
                    throw new InvalidDataException("Discord gateway event exceeded the allowed size.");
            } while (!result.EndOfMessage);

            using var json = JsonDocument.Parse(message.ToArray());
            var root = json.RootElement;
            var op = root.GetProperty("op").GetInt32();
            if (root.TryGetProperty("s", out var sequenceValue) && sequenceValue.ValueKind == JsonValueKind.Number)
                sequence = sequenceValue.GetInt64();
            if (op == 10)
            {
                var heartbeatMs = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                var identify = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    op = 2,
                    d = new
                    {
                        token = _discordBotToken,
                        intents = 33281,
                        properties = new { os = Environment.OSVersion.Platform.ToString(), browser = "CodeBadger", device = "CodeBadger" }
                    }
                });
                await socket.SendAsync(new ArraySegment<byte>(identify), WebSocketMessageType.Text, true, cancellationToken);
                _ = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(heartbeatMs));
                    while (await timer.WaitForNextTickAsync(cancellationToken))
                    {
                        var heartbeat = JsonSerializer.SerializeToUtf8Bytes(new { op = 1, d = sequence });
                        await gatewaySendGate.WaitAsync(cancellationToken);
                        try { await socket.SendAsync(new ArraySegment<byte>(heartbeat), WebSocketMessageType.Text, true, cancellationToken); }
                        finally { gatewaySendGate.Release(); }
                    }
                }, cancellationToken);
                continue;
            }
            if (op == 1)
            {
                var heartbeat = JsonSerializer.SerializeToUtf8Bytes(new { op = 1, d = sequence });
                await socket.SendAsync(new ArraySegment<byte>(heartbeat), WebSocketMessageType.Text, true, cancellationToken);
                continue;
            }
            if (op == 7)
                return;
            if (op == 0 && root.TryGetProperty("t", out var readyName) && readyName.GetString() == "READY")
            {
                SetConnectionState(true, "Connected to Discord Gateway.");
                delay = TimeSpan.FromSeconds(2);
                continue;
            }
            if (op != 0 || !root.TryGetProperty("t", out var eventName) || eventName.GetString() != "MESSAGE_CREATE" ||
                !root.TryGetProperty("d", out var data))
                continue;
            SetConnectionState(true, "Connected to Discord Gateway.");
            delay = TimeSpan.FromSeconds(2);
            if (data.TryGetProperty("author", out var author) && author.TryGetProperty("bot", out var bot) && bot.GetBoolean())
                continue;
            var channel = data.TryGetProperty("channel_id", out var channelValue) ? channelValue.GetString() : null;
            var user = data.TryGetProperty("author", out author) && author.TryGetProperty("id", out var userValue) ? userValue.GetString() : null;
            var text = data.TryGetProperty("content", out var contentValue) ? contentValue.GetString() : null;
            var messageId = data.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (channel != _discordChannelId && !_pendingNewSessions.ContainsKey(channel ?? string.Empty) &&
                !_sessionThreads.Values.Any(link => link.Platform == "Discord" && link.ThreadTs == channel))
                continue;
            if (!string.Equals(user, _discordAllowedUserId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(messageId))
                continue;
            var threadId = channel == _discordChannelId ? null : channel;
            var rootMessageId = data.TryGetProperty("message_reference", out var reference) &&
                reference.TryGetProperty("message_id", out var referencedMessage) ? referencedMessage.GetString() : messageId;
            var eventJson = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                event_id = messageId,
                @event = new { channel = _discordChannelId, user = _discordAllowedUserId, text, ts = messageId, thread_ts = threadId, root_ts = rootMessageId }
            }));
            using (eventJson)
            {
                var eventRoot = eventJson.RootElement;
                _ = HandleMessageEventSafelyAsync(eventRoot.Clone(), eventRoot.GetProperty("event").Clone(), cancellationToken);
            }
        }
    }

    private async Task<string> CreateDiscordThreadAsync(string rootMessageId, string title,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"channels/{Uri.EscapeDataString(_discordChannelId)}/messages/{Uri.EscapeDataString(rootMessageId)}/threads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _discordBotToken);
        var threadName = NormalizeTitle(title);
        if (threadName.Length > 100)
            threadName = threadName[..100];
        request.Content = JsonContent.Create(new { name = threadName, auto_archive_duration = 1440 });
        using var response = await DiscordHttp.SendAsync(request, cancellationToken);
        using var json = await ReadDiscordResponseAsync(response, cancellationToken);
        return json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Discord did not return the created thread ID.");
    }

    private static async Task<JsonDocument> ReadDiscordResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("message", out var message) ? message.GetString() : response.ReasonPhrase;
            json.Dispose();
            throw new InvalidOperationException("Discord API error: " + detail);
        }
        return json;
    }

    private static string ConvertSlackFormatting(string text)
    {
        var decoded = text.Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"(?<!\*)\*([^*\r\n]+)\*(?!\*)", "**$1**");
    }

    private static bool IsDiscordSnowflake(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);

    private static string RequireToken(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value :
            throw new InvalidOperationException($"Slack token must start with {prefix}.");

    private static string EscapeSlack(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private void SetConnectionState(bool connected, string status)
    {
        if (connected)
            _logger.LogInformation("{NotificationPlatform} connection state changed: connected.", _activePlatform);
        else
            _logger.LogWarning("{NotificationPlatform} connection state changed: disconnected. {ConnectionStatus}", _activePlatform, status);
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
        SetConnectionState(false, $"{_activePlatform} connection stopped.");
        _seenReplies.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _configurationGate.WaitAsync();
        try { await StopConnectionAsync(); }
        finally { _configurationGate.Release(); _configurationGate.Dispose(); }
    }
}
