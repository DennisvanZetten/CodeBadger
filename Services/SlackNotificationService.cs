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
    private const string MutedSessionsStorageKey = "codebadger-slack-muted-sessions";
    private readonly LauncherSettingsService _settingsService;
    private readonly OpenCodeActivityService _openCodeActivity;
    private readonly GitBranchService _gitBranchService;
    private readonly AiCommitMessageService _aiCommitMessageService;
    private readonly IdeaListService _ideaListService;
    private readonly ILogger<SlackNotificationService> _logger;
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://slack.com/api/") };
    private readonly ConcurrentDictionary<string, ThreadLink> _sessionThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingNewSession> _pendingNewSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommitMessage> _pendingCommitMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingInteractionThread> _pendingInteractionThreads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _announcedInteractions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _seenReplies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _mutedSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private CancellationTokenSource? _connectionCancellation;
    private Task? _connectionTask;
    private string _botToken = string.Empty;
    private string _appToken = string.Empty;
    private string _channelId = string.Empty;
    private string _allowedUserId = string.Empty;

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
    public sealed record SessionNotification(string Id, string Title, string Agent, string Directory, bool IsMuted);
    private sealed record ThreadLink(string ThreadTs, string ChannelId, string Directory, string Agent, string Title, DateTime CreatedAt);
    private sealed class PendingNewSession(IReadOnlyList<RepositoryEntry> repositories)
    {
        public IReadOnlyList<RepositoryEntry> Repositories { get; } = repositories;
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
        try
        {
            var result = await PostMessageAsync(_botToken, _channelId, message, threadTs, cancellationToken);
            if (threadTs is null && result is not null)
            {
                _sessionThreads[session.Id] = new ThreadLink(result, _channelId, session.Directory, session.Agent, title, DateTime.UtcNow);
                await SaveThreadLinksAsync();
                SessionNotificationsChanged?.Invoke();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not post completion for OpenCode session {SessionId} to Slack.", session.Id);
            throw;
        }
    }

    public bool CanSendNotifications => IsConnected && !string.IsNullOrWhiteSpace(_botToken) &&
        !string.IsNullOrWhiteSpace(_channelId) && !string.IsNullOrWhiteSpace(_allowedUserId);

    public IReadOnlyList<SessionNotification> GetSessionNotifications(string directory, bool repositoryMuted = false)
    {
        if (repositoryMuted || !CanSendNotifications)
            return [];
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

            if (!_sessionThreads.TryGetValue(pair.Key, out var link) || link.ChannelId != _channelId)
            {
                var title = string.IsNullOrWhiteSpace(session.Title) ? "OpenCode session" : session.Title;
                var threadTs = await PostMessageAsync(_botToken, _channelId,
                    $"⏳ *OpenCode wacht op je antwoord: {EscapeSlack(title)}*\n" +
                    $"Agent: `{EscapeSlack(session.Agent)}` · Sessie: `{EscapeSlack(session.Id)}`",
                    null, cancellationToken);
                link = new ThreadLink(threadTs, _channelId, session.Directory, session.Agent, title, DateTime.UtcNow);
                _sessionThreads[session.Id] = link;
                await SaveThreadLinksAsync();
                SessionNotificationsChanged?.Invoke();
                SessionLinked?.Invoke(session);
            }

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
                        FormatPendingInteraction(interaction), link.ThreadTs, cancellationToken);
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

    private static string FormatPendingInteraction(OpenCodeActivityService.PendingInteraction interaction)
    {
        if (interaction.Kind == "permission")
        {
            var resources = interaction.Resources.Count == 0 ? string.Empty :
                "\nResources:\n" + string.Join("\n", interaction.Resources.Select(resource => "• " + EscapeSlack(resource)));
            return $"⏸️ *OpenCode vraagt toestemming:* {EscapeSlack(interaction.Details)}\n" +
                $"Request ID: `{EscapeSlack(interaction.Id)}`{resources}\n" +
                $"Antwoord met `!allow {EscapeSlack(interaction.Id)}` (eenmalig), `!always {EscapeSlack(interaction.Id)}` (altijd) " +
                $"of `!deny {EscapeSlack(interaction.Id)} [reden]`.";
        }

        var fields = interaction.Fields.Count == 0 ? "(geen zichtbare velden)" : string.Join("\n", interaction.Fields.Select(field =>
        {
            var options = field.Options.Count == 0 ? string.Empty : " Opties: " +
                string.Join(", ", field.Options.Select(option => $"{EscapeSlack(option.Value)} ({EscapeSlack(option.Label)})"));
            return $"• `{EscapeSlack(field.Key)}` — {EscapeSlack(string.IsNullOrWhiteSpace(field.Title) ? field.Key : field.Title)}" +
                (field.Required ? " (verplicht)" : string.Empty) + options;
        }));
        return $"❓ *OpenCode vraagt om informatie:* {EscapeSlack(interaction.Title)}\n{fields}\n" +
            $"Antwoord met `!answer {EscapeSlack(interaction.Id)} sleutel=waarde; sleutel2=waarde`. " +
            "Meerdere antwoorden scheid je met puntkomma's; meerkeuzevelden accepteren waarden gescheiden door `|`.";
    }

    private async Task<bool> HandlePendingInteractionReplyAsync(string threadTs, string text,
        CancellationToken cancellationToken)
    {
        if (!_pendingInteractionThreads.TryGetValue(threadTs, out var pending))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(text,
            "^!(allow|always|deny|answer)\\s+(\\S+)(?:\\s+(.*))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success)
            return false;

        var command = match.Groups[1].Value.ToLowerInvariant();
        var id = match.Groups[2].Value;
        var body = match.Groups[3].Value.Trim();
        var kind = command == "answer" ? "form" : "permission";
        var interaction = pending.Interactions.FirstOrDefault(item => item.Kind == kind && item.Id == id);
        if (interaction is null)
        {
            await PostMessageAsync(_botToken, _channelId,
                "⚠️ Die vraag is niet meer open, of de ID hoort niet bij deze thread. Gebruik de ID uit de meest recente vraag.",
                threadTs, cancellationToken);
            return true;
        }

        try
        {
            if (kind == "permission")
            {
                var decision = command switch { "allow" => "once", "always" => "always", _ => "reject" };
                await _openCodeActivity.ReplyToPermissionAsync(pending.Session.Id, pending.Session.Directory,
                    id, decision, body, cancellationToken);
                await PostMessageAsync(_botToken, _channelId,
                    decision == "reject" ? "✅ Toestemmingsverzoek afgewezen." :
                        decision == "always" ? "✅ Toestemming altijd verleend." : "✅ Eenmalige toestemming verleend.",
                    threadTs, cancellationToken);
                return true;
            }

            var answers = ParseFormAnswers(body, interaction.Fields);
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
            object parsed = field.Type switch
            {
                "boolean" when bool.TryParse(value, out var boolean) => boolean,
                "integer" when long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var integer) => integer,
                "number" when double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
                "multiselect" => value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                _ => value
            };
            if (field.Type is "boolean" or "integer" or "number" && parsed is string)
                throw new InvalidOperationException($"Ongeldige waarde voor veld '{key}'.");
            if (field.Options.Count > 0)
            {
                var permitted = field.Type == "multiselect" && parsed is string[] selected
                    ? selected : parsed is string single ? [single] : [];
                foreach (var selectedValue in permitted)
                    if (!field.Options.Any(option => option.Value == selectedValue))
                        throw new InvalidOperationException($"'{selectedValue}' is geen geldige optie voor '{key}'.");
            }
            values[key] = parsed;
        }
        foreach (var field in fields.Where(field => field.Required))
            if (!values.ContainsKey(field.Key))
                throw new InvalidOperationException($"Verplicht veld '{field.Key}' ontbreekt.");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.Clone();
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
            _ = HandleMessageEventSafelyAsync(payload.Clone(), slackEvent.Clone(), cancellationToken);
        }
    }

    private async Task HandleMessageEventSafelyAsync(JsonElement payload, JsonElement slackEvent,
        CancellationToken cancellationToken)
    {
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
        var text = textValue.GetString()!.Trim();
        var threadTs = slackEvent.TryGetProperty("thread_ts", out var threadValue) ? threadValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(threadTs))
        {
            if (text.Equals("!help", StringComparison.OrdinalIgnoreCase))
            {
                await PostMessageAsync(_botToken, _channelId,
                "*Slack commands*\n• `!new` — start a new OpenCode session; then choose a repository and send a task in its thread.\n• `!todo <title>` — add an item to the CodeBadger ideas and tasks list. Optional type prefixes: `idea:`, `wish:`, `task:`, or `bug:`.\n• `!help` — show this command list.",
                    null, cancellationToken);
                return;
            }

            if (text.StartsWith("!todo", StringComparison.OrdinalIgnoreCase))
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

        if (text.StartsWith("!todo", StringComparison.OrdinalIgnoreCase))
        {
            await AddIdeaFromSlackAsync(text, threadTs, cancellationToken);
            return;
        }

        var linkedSession = _sessionThreads.FirstOrDefault(pair => pair.Value.ThreadTs == threadTs && pair.Value.ChannelId == _channelId);
        if (string.IsNullOrWhiteSpace(linkedSession.Key))
            return;
        if (text.Equals("!help", StringComparison.OrdinalIgnoreCase))
        {
            await PostMessageAsync(_botToken, _channelId,
                "*Session commands*\n• `!build <task>` — switch to Build and send the task.\n• `!plan <task>` — switch to Plan and send the task.\n• `!allow <id>` / `!always <id>` / `!deny <id> [reason]` — answer an OpenCode permission request.\n• `!answer <id> key=value; key2=value` — answer an OpenCode form. Use `|` between multiselect values.\n• `!commit` — stage and commit all repository changes.\n• `!help` — show this command list.\n\nAny other reply is sent to the current session.",
                threadTs, cancellationToken);
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
        var title = command.Length > "!todo".Length ? command["!todo".Length..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160)
        {
            await PostMessageAsync(_botToken, _channelId,
                "Usage: `!todo <title>` (up to 160 characters). Optional prefixes: `idea:`, `wish:`, `task:`, or `bug:`.",
                threadTs, cancellationToken);
            return;
        }

        var type = IdeaType.Task;
        foreach (var candidate in Enum.GetValues<IdeaType>())
        {
            var prefix = candidate.ToString().ToLowerInvariant() + ":";
            if (!title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            type = candidate;
            title = title[prefix.Length..].Trim();
            break;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            await PostMessageAsync(_botToken, _channelId, "Add a title after the item type.", threadTs, cancellationToken);
            return;
        }

        var item = await _ideaListService.AddAsync(title, string.Empty, type, cancellationToken);
        await PostMessageAsync(_botToken, _channelId,
            $"✅ Added *{EscapeSlack(item.Title)}* to the CodeBadger ideas and tasks list ({type.ToString().ToLowerInvariant()}).",
            threadTs, cancellationToken);
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
                "Commit from Slack is only available for a configured, non-sandboxed OpenCode repository.", threadTs, cancellationToken);
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
            var generated = await _aiCommitMessageService.GenerateAsync(promptSettings, diff.Output, branch.Branch);
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
        var threadTs = await PostMessageAsync(_botToken, _channelId,
            "*Start a new OpenCode session*\nReply in this thread with a repository number:\n" + string.Join("\n", lines),
            null, cancellationToken);
        _pendingNewSessions[threadTs] = new PendingNewSession(repositories);
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

        var title = string.IsNullOrWhiteSpace(session.Title) ? text : session.Title;
        _sessionThreads[session.Id] = new ThreadLink(threadTs, _channelId, session.Directory, session.Agent, title, DateTime.UtcNow);
        await SaveThreadLinksAsync();
        SessionNotificationsChanged?.Invoke();
        SessionStarted?.Invoke(session with { Title = title });
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
