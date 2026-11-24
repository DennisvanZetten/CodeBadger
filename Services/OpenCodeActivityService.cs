using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed class OpenCodeActivityService
{
    private readonly ILogger<OpenCodeActivityService> _logger;
    private readonly ConcurrentDictionary<string, byte> _shareShapeWarnings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _promptAttachmentFiles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _activityRequestGate = new(1, 1);
    private volatile bool _activityEndpointFailed;

    public OpenCodeActivityService(ILogger<OpenCodeActivityService> logger) => _logger = logger;

    public sealed record ActiveSession(string Id, string Directory, string Agent, string Title,
        string RepositoryName = "");
    public sealed record AvailableModel(string Reference, string Name, string Status, bool Enabled,
        double InputCost, double OutputCost, bool HasPublishedCost, int Context, int OutputLimit);
    public sealed record AvailableAgent(string Id, string Name, string Mode);
    public sealed record SessionContextUsage(long Tokens, string ModelReference);
    public sealed record ShareSession(string Id, string Title, bool IsTerminalSession = false,
        bool IsActive = false, long? LastViewedAt = null);
    public sealed record SessionTextMessage(string Id, string Role, string Text);
    public sealed record FormField(string Key, string Title, string Description, string Type, bool Required,
        IReadOnlyList<(string Value, string Label)> Options, bool Custom);
    public sealed record PendingInteraction(string Id, string Kind, string Title, string Details,
        IReadOnlyList<FormField> Fields, IReadOnlyList<string> Resources);
    public sealed record ActivitySnapshot(HashSet<string> ActiveDirectories, HashSet<string> QuestionDirectories,
        Dictionary<string, ActiveSession> Sessions, Dictionary<string, IReadOnlyList<PendingInteraction>> PendingInteractions);

    // Only the managed OpenCode service is queried. Other tools, standalone
    // instances and explicitly selected servers do not expose the same state here.
    public static bool SupportsCommand(string command)
    {
        var trimmed = command.Trim();
        var end = trimmed.StartsWith('"') ? trimmed.IndexOf('"', 1) : trimmed.IndexOfAny([' ', '\t']);
        if (trimmed.StartsWith('"') && end < 0)
            return false;
        var executable = trimmed.StartsWith('"') ? trimmed[1..end] : end < 0 ? trimmed : trimmed[..end];
        var arguments = end < 0 ? string.Empty : trimmed[(end + 1)..];
        return Path.GetFileNameWithoutExtension(executable).Equals("opencode", StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains("--standalone", StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains("--server", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ActivitySnapshot?> GetActivityAsync(CancellationToken cancellationToken,
        IReadOnlyCollection<string>? directories = null)
    {
        if (_activityEndpointFailed)
            return null;
        try
        {
            if (!await _activityRequestGate.WaitAsync(0, cancellationToken))
                return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
        var activeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questionDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeSessions = new Dictionary<string, ActiveSession>(StringComparer.Ordinal);
        var pendingInteractions = new Dictionary<string, IReadOnlyList<PendingInteraction>>(StringComparer.Ordinal);
        using var active = await RequestAsync("/api/session/active", cancellationToken);
        if (active is null || !active.RootElement.TryGetProperty("data", out var sessions) || sessions.ValueKind != JsonValueKind.Object)
            return null;

        var ids = sessions.EnumerateObject().Select(session => session.Name)
            .Where(id => id.StartsWith("ses_", StringComparison.Ordinal) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .ToArray();
        var responses = await Task.WhenAll(ids.Select(async id =>
        {
            var sessionResponse = await RequestAsync($"/api/session/{id}", cancellationToken);
            if (sessionResponse is null || !sessionResponse.RootElement.TryGetProperty("data", out var sessionData) ||
                !sessionData.TryGetProperty("location", out var sessionLocation) ||
                !sessionLocation.TryGetProperty("directory", out var sessionDirectory) ||
                sessionDirectory.ValueKind != JsonValueKind.String || sessionDirectory.GetString() is not { Length: > 0 } path ||
                NormalizePath(path) is not { } normalized ||
                (directories is not null && !directories.Any(candidate =>
                    string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))))
            {
                sessionResponse?.Dispose();
                return (Session: (JsonDocument?)null, Permissions: (JsonDocument?)null, Forms: (JsonDocument?)null);
            }
            return (Session: sessionResponse,
                Permissions: await RequestAsync($"/api/session/{id}/permission", cancellationToken),
                Forms: await RequestAsync($"/api/session/{id}/form", cancellationToken));
        }));
        try
        {
            for (var index = 0; index < responses.Length; index++)
            {
                var (sessionResponse, permissionResponse, formResponse) = responses[index];
                if (sessionResponse is null || !sessionResponse.RootElement.TryGetProperty("data", out var session)
                    || !session.TryGetProperty("location", out var location)
                    || !location.TryGetProperty("directory", out var directory)
                    || directory.ValueKind != JsonValueKind.String
                    || directory.GetString() is not { Length: > 0 } path
                    || NormalizePath(path) is not { } normalized)
                    return null;

                activeDirectories.Add(normalized);
                var agent = session.TryGetProperty("agent", out var agentValue) && agentValue.ValueKind == JsonValueKind.String
                    ? agentValue.GetString() ?? string.Empty : string.Empty;
                var title = session.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                    ? titleValue.GetString() ?? string.Empty : string.Empty;
                var sessionId = session.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                    ? idValue.GetString() ?? string.Empty : string.Empty;
                if (sessionId.Length == 0)
                    sessionId = ids[index];
                activeSessions[sessionId] = new ActiveSession(sessionId, normalized, agent, title);
                var interactions = new List<PendingInteraction>();
                if (permissionResponse?.RootElement.TryGetProperty("data", out var permissions) == true &&
                    permissions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var permission in permissions.EnumerateArray())
                    {
                        var id = GetString(permission, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;
                        var action = GetString(permission, "action");
                        var message = GetString(permission, "message");
                        var resources = GetStringArray(permission, "resources");
                        var details = string.IsNullOrWhiteSpace(message) ? action : $"{action}: {message}";
                        interactions.Add(new PendingInteraction(id, "permission", "Permission requested",
                            details, [], resources));
                    }
                }
                if (formResponse?.RootElement.TryGetProperty("data", out var forms) == true &&
                    forms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var form in forms.EnumerateArray())
                    {
                        var id = GetString(form, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;
                        var fields = new List<FormField>();
                        if (form.TryGetProperty("fields", out var formFields) && formFields.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var field in formFields.EnumerateArray())
                            {
                                var key = GetString(field, "key");
                                if (string.IsNullOrWhiteSpace(key) ||
                                    field.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                                    continue;
                                var options = new List<(string Value, string Label)>();
                                if (field.TryGetProperty("options", out var optionValues) && optionValues.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var option in optionValues.EnumerateArray())
                                    {
                                        var value = GetString(option, "value");
                                        var label = GetString(option, "label");
                                        if (!string.IsNullOrWhiteSpace(value))
                                            options.Add((value, string.IsNullOrWhiteSpace(label) ? value : label));
                                    }
                                }
                                var fieldType = GetString(field, "type");
                                var supportsCustom = (fieldType is "string" or "multiselect") &&
                                    (!field.TryGetProperty("custom", out var custom) || custom.ValueKind != JsonValueKind.False);
                                fields.Add(new FormField(key, GetString(field, "title"), GetString(field, "description"),
                                    fieldType, field.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
                                    options, supportsCustom));
                            }
                        }
                        interactions.Add(new PendingInteraction(id, "form", GetString(form, "title"), string.Empty,
                            fields, []));
                    }
                }
                if (interactions.Count > 0)
                {
                    questionDirectories.Add(normalized);
                    pendingInteractions[sessionId] = interactions;
                }
            }
            return new ActivitySnapshot(activeDirectories, questionDirectories, activeSessions, pendingInteractions);
        }
        finally
        {
            foreach (var (sessionResponse, permissionResponse, formResponse) in responses)
            {
                sessionResponse?.Dispose();
                permissionResponse?.Dispose();
                formResponse?.Dispose();
            }
        }
        }
        finally
        {
            _activityRequestGate.Release();
        }
    }

    public void MarkActivityEndpointFailed() => _activityEndpointFailed = true;
    public void ResetActivityEndpointFailure() => _activityEndpointFailed = false;

    public async Task<string?> GetLatestCompletedAnswerAsync(string sessionId, string directory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return null;
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=desc&limit=20", cancellationToken,
            workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var message in messages.EnumerateArray())
        {
            var info = message.TryGetProperty("info", out var infoValue) && infoValue.ValueKind == JsonValueKind.Object
                ? infoValue : message;
            var roleProperty = info.TryGetProperty("role", out var roleValue) ? roleValue :
                info.TryGetProperty("type", out var typeValue) ? typeValue : default;
            if (roleProperty.ValueKind != JsonValueKind.String || roleProperty.GetString() != "assistant")
                continue;

            if (info.TryGetProperty("finish", out var finish) && finish.ValueKind == JsonValueKind.String &&
                finish.GetString() is not ("stop" or "length"))
                continue;
            if (info.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object &&
                time.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.Null)
                continue;

            var parts = message.TryGetProperty("parts", out var partsValue) && partsValue.ValueKind == JsonValueKind.Array
                ? partsValue
                : info.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.Array
                    ? contentValue : default;
            if (parts.ValueKind != JsonValueKind.Array)
                continue;

            var text = string.Join("\n", parts.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var partType) && partType.GetString() == "text")
                .Select(part => part.TryGetProperty("text", out var partText) ? partText.GetString() : null)
                .Where(partText => !string.IsNullOrWhiteSpace(partText))).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Length > 3500 ? text[..3500] + "\n…" : text;
        }
        return null;
    }

    public async Task<string?> GetCompletedAnswerForPromptAsync(string sessionId, string directory,
        string promptMessageId, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId) || !IsValidMessageId(promptMessageId))
            return null;
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=desc&limit=100", cancellationToken,
            workingDirectory: directory);
        if (response?.RootElement.TryGetProperty("data", out var messages) != true || messages.ValueKind != JsonValueKind.Array)
            return null;

        var answers = new List<(long Created, string Text)>();
        foreach (var message in messages.EnumerateArray())
        {
            var info = message.TryGetProperty("info", out var infoValue) && infoValue.ValueKind == JsonValueKind.Object
                ? infoValue : message.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.Object
                    ? messageValue : message;
            if (!TryGetPropertyIgnoreCase(info, "role", out var role) || role.ValueKind != JsonValueKind.String ||
                role.GetString() != "assistant" || !TryGetPropertyIgnoreCase(info, "parentID", out var parentId) ||
                parentId.ValueKind != JsonValueKind.String || parentId.GetString() != promptMessageId)
                continue;

            if (TryGetPropertyIgnoreCase(info, "error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                continue;
            var finish = GetStringIgnoreCase(info, "finish");
            if (!string.IsNullOrEmpty(finish) && finish is not ("stop" or "length"))
                continue;
            if (TryGetPropertyIgnoreCase(info, "time", out var time) && time.ValueKind == JsonValueKind.Object &&
                (!TryGetPropertyIgnoreCase(time, "completed", out var completed) ||
                 completed.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
                continue;

            var parts = message.TryGetProperty("parts", out var partsValue) && partsValue.ValueKind == JsonValueKind.Array
                ? partsValue
                : TryGetPropertyIgnoreCase(info, "content", out var content) && content.ValueKind == JsonValueKind.Array
                    ? content : default;
            if (parts.ValueKind != JsonValueKind.Array)
                continue;
            var text = string.Join("\n", parts.EnumerateArray()
                .Where(part => TryGetPropertyIgnoreCase(part, "type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "text")
                .Select(part => TryGetPropertyIgnoreCase(part, "text", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            if (text.Length == 0)
                continue;

            var created = TryGetPropertyIgnoreCase(info, "time", out time) && time.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoreCase(time, "created", out var createdValue) && createdValue.TryGetInt64(out var timestamp)
                ? timestamp : 0;
            answers.Add((created, text));
        }

        if (answers.Count == 0)
            return null;
        var combined = string.Join("\n\n", answers.OrderBy(answer => answer.Created).Select(answer => answer.Text));
        return combined.Length > 20000 ? combined[..20000] + "\n…" : combined;
    }

    public async Task<SessionContextUsage?> GetLatestContextUsageAsync(string sessionId, string directory,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return null;
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=desc&limit=100", cancellationToken,
            workingDirectory: directory);
        if (response?.RootElement.TryGetProperty("data", out var messages) != true || messages.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var message in messages.EnumerateArray())
        {
            var info = message.TryGetProperty("info", out var infoValue) && infoValue.ValueKind == JsonValueKind.Object
                ? infoValue : message.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.Object
                    ? messageValue : message;
            if ((!TryGetPropertyIgnoreCase(info, "role", out var role) && !TryGetPropertyIgnoreCase(info, "type", out role)) ||
                role.ValueKind != JsonValueKind.String || role.GetString() is not ("assistant"))
                continue;
            if (TryGetPropertyIgnoreCase(info, "error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                continue;
            if (TryGetPropertyIgnoreCase(info, "time", out var time) && time.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoreCase(time, "completed", out var completed) && completed.ValueKind == JsonValueKind.Null)
                continue;
            if (!TryGetPropertyIgnoreCase(info, "tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                continue;

            var total = GetInt64(tokens, "input") + GetInt64(tokens, "output") + GetInt64(tokens, "reasoning");
            if (TryGetPropertyIgnoreCase(tokens, "cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                total += GetInt64(cache, "read") + GetInt64(cache, "write");
            if (total <= 0)
                continue;

            var provider = GetStringIgnoreCase(info, "providerID");
            var model = GetStringIgnoreCase(info, "modelID");
            if ((provider.Length == 0 || model.Length == 0) &&
                TryGetPropertyIgnoreCase(info, "model", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
            {
                if (provider.Length == 0) provider = GetStringIgnoreCase(modelInfo, "providerID");
                if (model.Length == 0) model = GetStringIgnoreCase(modelInfo, "id");
            }
            return new SessionContextUsage(total,
                provider.Length > 0 && model.Length > 0 ? $"{provider}/{model}" : string.Empty);
        }
        return null;
    }

    public async Task<string> GenerateSessionHandoffSummaryAsync(string sessionId, string directory,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        if (await IsSessionActiveAsync(sessionId, directory, cancellationToken) != false)
            throw new InvalidOperationException("OpenCode could not confirm that the agent is idle before creating a handoff summary.");
        var request = new
        {
            prompt = "Create a concise handoff summary for continuing this task in a fresh agent session. " +
                "Preserve the user's goal, important decisions and constraints, relevant files, completed work, " +
                "remaining steps, and unresolved questions. Do not claim work is complete unless the session confirms it. " +
                "Use clear Markdown and keep only information needed to continue."
        };
        using var response = await RequestAsync($"/api/session/{sessionId}/generate", cancellationToken,
            HttpMethod.Post, JsonSerializer.Serialize(request), directory, timeoutSeconds: 120);
        if (response?.RootElement.TryGetProperty("data", out var data) != true ||
            !data.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(text.GetString()))
            throw new InvalidOperationException("OpenCode could not generate a handoff summary for the session.");
        return text.GetString()!;
    }

    public async Task AddSessionHandoffSummaryAsync(string sessionId, string directory, string summary,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId) || string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("A valid session and non-empty handoff summary are required.");
        var payload = JsonSerializer.Serialize(new
        {
            text = "Session handoff summary from the previous agent session:\n\n" + summary,
            description = "CodeBadger session handoff",
            resume = false
        });
        using var response = await RequestAsync($"/api/session/{sessionId}/synthetic", cancellationToken,
            HttpMethod.Post, payload, directory);
        if (response is null)
            throw new InvalidOperationException("OpenCode could not add the handoff summary to the new session.");
    }

    private static bool IsValidMessageId(string messageId) =>
        messageId.StartsWith("msg_", StringComparison.Ordinal) &&
        messageId.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string GetStringIgnoreCase(JsonElement element, string property) =>
        TryGetPropertyIgnoreCase(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static long GetInt64(JsonElement element, string property) =>
        TryGetPropertyIgnoreCase(element, property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) && number > 0 ? number : 0;

    public async Task<IReadOnlyList<SessionTextMessage>> GetRecentUserMessagesAsync(string sessionId, string directory,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return [];
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=desc&limit=100", cancellationToken,
            workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<SessionTextMessage>();
        foreach (var message in messages.EnumerateArray().Reverse())
        {
            var info = message.TryGetProperty("info", out var infoValue) && infoValue.ValueKind == JsonValueKind.Object
                ? infoValue : message;
            var roleProperty = info.TryGetProperty("role", out var roleValue) ? roleValue :
                info.TryGetProperty("type", out var typeValue) ? typeValue : default;
            if (roleProperty.ValueKind != JsonValueKind.String || roleProperty.GetString() != "user")
                continue;
            var id = info.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var parts = message.TryGetProperty("parts", out var partsValue) && partsValue.ValueKind == JsonValueKind.Array
                ? partsValue
                : info.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.Array
                    ? contentValue : default;
            if (parts.ValueKind != JsonValueKind.Array)
                continue;
            var text = string.Join("\n", parts.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var partType) && partType.GetString() == "text")
                .Select(part => part.TryGetProperty("text", out var partText) ? partText.GetString() : null)
                .Where(partText => !string.IsNullOrWhiteSpace(partText))).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new SessionTextMessage(id, "user", text));
        }
        return result;
    }

    public async Task<SessionTextMessage?> GetInitialUserMessageAsync(string sessionId, string directory,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return null;
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=asc&limit=1", cancellationToken,
            workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var message in messages.EnumerateArray())
        {
            var info = message.TryGetProperty("info", out var infoValue) && infoValue.ValueKind == JsonValueKind.Object
                ? infoValue : message;
            var role = info.TryGetProperty("role", out var roleValue) ? roleValue :
                info.TryGetProperty("type", out var typeValue) ? typeValue : default;
            if (role.ValueKind != JsonValueKind.String || role.GetString() != "user")
                continue;
            var id = info.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString() : null;
            var parts = message.TryGetProperty("parts", out var partsValue) && partsValue.ValueKind == JsonValueKind.Array
                ? partsValue
                : info.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.Array
                    ? contentValue : default;
            if (string.IsNullOrWhiteSpace(id) || parts.ValueKind != JsonValueKind.Array)
                continue;
            var text = string.Join("\n", parts.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var partType) && partType.GetString() == "text")
                .Select(part => part.TryGetProperty("text", out var partText) ? partText.GetString() : null)
                .Where(partText => !string.IsNullOrWhiteSpace(partText))).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return new SessionTextMessage(id, "user", text);
        }
        return null;
    }

    public async Task<IReadOnlyList<SessionTextMessage>> GetShareableSessionMessagesAsync(string sessionId, string directory,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return [];
        using var session = await RequestAsync($"/api/session/{sessionId}", cancellationToken, workingDirectory: directory);
        if (session?.RootElement.TryGetProperty("data", out var sessionData) != true ||
            !sessionData.TryGetProperty("location", out var location) ||
            !location.TryGetProperty("directory", out var sessionDirectory) || sessionDirectory.ValueKind != JsonValueKind.String ||
            !string.Equals(NormalizePath(sessionDirectory.GetString()!), NormalizePath(directory), StringComparison.OrdinalIgnoreCase))
            return [];
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=asc&limit=200", cancellationToken,
            workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<SessionTextMessage>();
        var roleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var userContentKinds = new HashSet<string>(StringComparer.Ordinal);
        var observedMessageTypes = new HashSet<string>(StringComparer.Ordinal);
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.EnumerateArray())
        {
            CollectShape(message, observedKeys, observedMessageTypes);
            var info = message.TryGetProperty("info", out var infoValue) && infoValue.ValueKind == JsonValueKind.Object
                ? infoValue
                : message.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.Object
                    ? messageValue : message;
            var roleName = ReadRole(info, message);
            if (roleName is not ("user" or "assistant"))
                continue;
            roleCounts[roleName] = roleCounts.GetValueOrDefault(roleName) + 1;

            var id = info.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString()
                : message.TryGetProperty("id", out var outerId) && outerId.ValueKind == JsonValueKind.String
                    ? outerId.GetString() : null;
            var content = TryGetPropertyIgnoreCase(info, "content", out var contentValue) ? contentValue :
                TryGetPropertyIgnoreCase(message, "content", out var messageContent) ? messageContent : default;
            if (roleName == "user")
                userContentKinds.Add(content.ValueKind.ToString());
            // OpenCode user messages store the submitted prompt in info.content as a string.
            // Assistant messages use a parts array. Handle both forms, plus array content in
            // case an API version returns normalized message parts for user messages too.
            if (roleName == "user")
            {
                var userText = ExtractUserMessageText(message, info).Trim();
                if (!string.IsNullOrWhiteSpace(id) && userText.Length > 0)
                {
                    result.Add(new SessionTextMessage(id!, "user",
                        userText.Length > 20000 ? userText[..20000] + "\n…" : userText));
                    continue;
                }
            }

            var parts = message.TryGetProperty("parts", out var partsValue) && partsValue.ValueKind == JsonValueKind.Array
                ? partsValue
                : content.ValueKind == JsonValueKind.Array ? content : default;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (parts.ValueKind != JsonValueKind.Array)
                continue;

            var text = string.Join("\n", parts.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var partType) && partType.ValueKind == JsonValueKind.String &&
                    partType.GetString() == "text")
                .Select(part => part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String
                    ? partText.GetString() : null)
                .Where(partText => !string.IsNullOrWhiteSpace(partText))).Trim();
            if (text.Length > 0)
                result.Add(new SessionTextMessage(id!, roleName, text.Length > 20000 ? text[..20000] + "\n…" : text));
        }

        if (!result.Any(message => message.Role == "user") &&
            _shareShapeWarnings.TryAdd(sessionId, 0))
            _logger.LogWarning("Could not extract user prompts from OpenCode session {SessionId}. Recognized roles: {Roles}; message type values: {MessageTypes}; user content JSON kinds: {ContentKinds}; JSON keys: {Keys}. Prompt text was not logged.",
                sessionId, string.Join(",", roleCounts.Select(pair => $"{pair.Key}:{pair.Value}")),
                string.Join(",", observedMessageTypes), string.Join(",", userContentKinds), string.Join(",", observedKeys));
        return result;
    }

    private static string? ExtractUserContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
            return string.Join("\n", content.EnumerateArray().Select(ExtractUserContentText)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (content.ValueKind != JsonValueKind.Object)
            return null;

        if (content.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
            type.GetString() is "text" or "input_text" && content.TryGetProperty("text", out var textValue) &&
            textValue.ValueKind == JsonValueKind.String)
            return textValue.GetString();
        if (content.TryGetProperty("text", out var plainText) && plainText.ValueKind == JsonValueKind.String)
            return plainText.GetString();
        if (content.TryGetProperty("parts", out var parts))
            return ExtractUserContentText(parts);
        if (content.TryGetProperty("content", out var nestedContent))
            return ExtractUserContentText(nestedContent);
        foreach (var key in new[] { "prompt", "input", "message", "value", "user", "query" })
            if (content.TryGetProperty(key, out var nested))
            {
                var extracted = ExtractUserContentText(nested);
                if (!string.IsNullOrWhiteSpace(extracted))
                    return extracted;
            }
        return null;
    }

    private static void CollectShape(JsonElement element, HashSet<string> keys, HashSet<string> types)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                CollectShape(child, keys, types);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in element.EnumerateObject())
        {
            keys.Add(property.Name);
            if (property.Name is "type" or "role" && property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { Length: > 0 and <= 40 } value)
                types.Add(value);
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectShape(property.Value, keys, types);
        }
    }

    private static string? ReadRole(JsonElement info, JsonElement message)
    {
        foreach (var element in new[] { info, message })
        {
            foreach (var key in new[] { "role", "type" })
            {
                if (TryGetPropertyIgnoreCase(element, key, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is "user" or "assistant")
                    return value.GetString();
            }
            foreach (var nestedKey in new[] { "message", "info" })
                if (TryGetPropertyIgnoreCase(element, nestedKey, out var nested) && nested.ValueKind == JsonValueKind.Object &&
                    TryGetPropertyIgnoreCase(nested, "role", out var nestedRole) && nestedRole.ValueKind == JsonValueKind.String &&
                    nestedRole.GetString() is "user" or "assistant")
                    return nestedRole.GetString();
        }
        return null;
    }

    private static string ExtractUserMessageText(JsonElement message, JsonElement info)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in new[] { info, message })
            CollectUserText(element, values, seen);
        return string.Join("\n", values);
    }

    private static void CollectUserText(JsonElement element, List<string> values, HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                CollectUserText(child, values, seen);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            var isTextField = property.Name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("prompt", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("input", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("message", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("parts", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("value", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                              property.Name.Equals("query", StringComparison.OrdinalIgnoreCase);
            if (!isTextField)
                continue;

            var extracted = ExtractUserContentText(property.Value);
            if (!string.IsNullOrWhiteSpace(extracted) && seen.Add(extracted.Trim()))
                values.Add(extracted.Trim());
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        }
        value = default;
        return false;
    }

    public async Task<IReadOnlyList<ShareSession>?> GetRepositorySessionsAsync(
        string directory, CancellationToken cancellationToken, string? preferredSessionId = null)
    {
        if (NormalizePath(directory) is null || !Directory.Exists(directory))
            return [];
        using var response = await RequestAsync("/api/session?limit=30", cancellationToken, workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var sessions) ||
            sessions.ValueKind != JsonValueKind.Array)
            return null;
        var activeSessionIds = new HashSet<string>(StringComparer.Ordinal);
        using (var active = await RequestAsync("/api/session/active", cancellationToken, workingDirectory: directory))
        {
            if (active?.RootElement.TryGetProperty("data", out var activeSessions) == true && activeSessions.ValueKind == JsonValueKind.Object)
                foreach (var property in activeSessions.EnumerateObject())
                    activeSessionIds.Add(property.Name);
        }
        var candidates = sessions.EnumerateArray()
            .Where(session => session.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                IsValidSessionId(id.GetString() ?? string.Empty))
            .Select(session => new ShareSession(GetString(session, "id"), GetString(session, "title")))
            .ToArray();

        // A TUI terminal can be showing a session that is not in the recent-page
        // window returned by /api/session. Resolve that exact known ID first and
        // promote it, rather than silently defaulting to a different recent session.
        if (!string.IsNullOrWhiteSpace(preferredSessionId) && IsValidSessionId(preferredSessionId) &&
            candidates.All(candidate => candidate.Id != preferredSessionId))
        {
            using var preferred = await RequestAsync($"/api/session/{preferredSessionId}", cancellationToken, workingDirectory: directory);
            if (IsSessionInDirectory(preferred, directory))
            {
                candidates = candidates.Prepend(new ShareSession(preferredSessionId,
                    GetString(preferred!.RootElement.GetProperty("data"), "title"), IsTerminalSession: true)).ToArray();
            }
        }

        var matching = new List<ShareSession>();
        foreach (var session in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var details = await RequestAsync($"/api/session/{session.Id}", cancellationToken, workingDirectory: directory);
            if (!IsSessionInDirectory(details, directory))
                continue;
            var data = details!.RootElement.GetProperty("data");
            var viewedAt = data.TryGetProperty("time", out var time) &&
                           time.TryGetProperty("viewed", out var viewed) && viewed.TryGetInt64(out var viewedValue)
                ? viewedValue : (long?)null;
            matching.Add(session with { IsActive = activeSessionIds.Contains(session.Id), LastViewedAt = viewedAt });
        }
        if (!string.IsNullOrWhiteSpace(preferredSessionId))
        {
            var preferredIndex = matching.FindIndex(session => session.Id == preferredSessionId);
            if (preferredIndex >= 0)
                matching[preferredIndex] = matching[preferredIndex] with { IsTerminalSession = true };
        }
        return matching.OrderByDescending(session => session.IsTerminalSession)
            .ThenByDescending(session => session.LastViewedAt ?? 0)
            .ThenByDescending(session => session.IsActive)
            .ToArray();
    }

    private static bool IsSessionInDirectory(JsonDocument? response, string directory) =>
        response?.RootElement.TryGetProperty("data", out var data) == true &&
        data.TryGetProperty("location", out var location) &&
        location.TryGetProperty("directory", out var path) && path.ValueKind == JsonValueKind.String &&
        string.Equals(NormalizePath(path.GetString()!), NormalizePath(directory), StringComparison.OrdinalIgnoreCase);

    public async Task<bool> HasPendingInteractionAsync(string sessionId, string directory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return false;
        using var permissions = await RequestAsync($"/api/session/{sessionId}/permission", cancellationToken, workingDirectory: directory);
        using var forms = await RequestAsync($"/api/session/{sessionId}/form", cancellationToken, workingDirectory: directory);
        return HasItems(permissions) || HasItems(forms);
    }

    public async Task<bool?> IsSessionActiveAsync(string sessionId, string directory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return null;
        using var response = await RequestAsync("/api/session/active", cancellationToken, workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var sessions) ||
            sessions.ValueKind != JsonValueKind.Object)
            return null;
        return sessions.TryGetProperty(sessionId, out _);
    }

    public async Task ReplyToPermissionAsync(string sessionId, string directory, string requestId, string decision,
        string? message, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId) || string.IsNullOrWhiteSpace(requestId) ||
            requestId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Invalid OpenCode permission request ID.", nameof(requestId));
        if (decision is not ("once" or "always" or "reject"))
            throw new ArgumentException("Permission decision must be once, always, or reject.", nameof(decision));
        var payload = JsonSerializer.Serialize(new { decision, message });
        using var response = await RequestAsync($"/api/session/{sessionId}/permission/{requestId}/reply",
            cancellationToken, HttpMethod.Post, payload, directory);
        if (response is null)
            throw new InvalidOperationException("Could not submit the permission decision to OpenCode.");
    }

    public async Task ReplyToFormAsync(string sessionId, string directory, string formId, JsonElement answers,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId) || string.IsNullOrWhiteSpace(formId) ||
            formId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Invalid OpenCode form ID.", nameof(formId));
        if (answers.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Form answers must be a JSON object.", nameof(answers));
        var payload = JsonSerializer.Serialize(new { answer = answers });
        using var response = await RequestAsync($"/api/session/{sessionId}/form/{formId}/reply",
            cancellationToken, HttpMethod.Post, payload, directory);
        if (response is null)
            throw new InvalidOperationException("Could not submit the form answer to OpenCode.");
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
            : [];

    private static bool HasItems(JsonDocument? response) => response is not null &&
        response.RootElement.TryGetProperty("data", out var items) &&
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;

    public async Task<string?> SendPromptAsync(string sessionId, string directory, string text, CancellationToken cancellationToken,
        string? agent = null, IReadOnlyList<string>? images = null)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000)
            throw new ArgumentException("Reply must contain 1–32,000 characters.", nameof(text));
        _logger.LogInformation("Submitting prompt to OpenCode session {SessionId} with agent {Agent}. Prompt excerpt: {PromptExcerpt}",
            sessionId, agent ?? "current", CreateLogExcerpt(text));
        if (!string.IsNullOrWhiteSpace(agent))
            await SwitchAgentAsync(sessionId, directory, agent, cancellationToken);
        var attachmentPaths = new List<string>();
        var files = new List<object>();
        try
        {
            foreach (var (image, index) in (images ?? []).Where(IsSupportedImageDataUrl).Select((image, index) => (image, index)))
            {
                var comma = image.IndexOf(',');
                var bytes = Convert.FromBase64String(image[(comma + 1)..]);
                var filePath = Path.Combine(Path.GetTempPath(), $"codebadger-clipboard-{Guid.NewGuid():N}.jpg");
                await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
                attachmentPaths.Add(filePath);
                files.Add(new { uri = new Uri(filePath).AbsoluteUri, name = $"clipboard-image-{index + 1}.jpg" });
            }
        }
        catch
        {
            DeleteTemporaryAttachments(attachmentPaths);
            throw;
        }

        var payload = JsonSerializer.Serialize(files.Count > 0
            ? (object)new { text, delivery = "steer", files = files.ToArray() }
            : new { text, delivery = "steer" });
        try
        {
            using var response = await RequestAsync($"/api/session/{sessionId}/prompt", cancellationToken,
                HttpMethod.Post, payload, directory);
            if (response is null)
                throw new InvalidOperationException("Could not submit the reply to the OpenCode session.");
            var promptMessageId = response.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : response.RootElement.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object &&
                      data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                      info.TryGetProperty("id", out id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString() : null;
            if (!string.IsNullOrWhiteSpace(promptMessageId) && !IsValidMessageId(promptMessageId))
                promptMessageId = null;
            if (attachmentPaths.Count > 0)
            {
                var pending = _promptAttachmentFiles.GetOrAdd(sessionId, _ => new ConcurrentBag<string>());
                foreach (var path in attachmentPaths)
                    pending.Add(path);
            }
            _logger.LogInformation("Prompt submitted to OpenCode session {SessionId}. Prompt content: {PromptContent}",
                sessionId, ExtractPromptExcerpt(payload));
            return promptMessageId;
        }
        catch
        {
            DeleteTemporaryAttachments(attachmentPaths);
            throw;
        }
    }

    private static bool IsSupportedImageDataUrl(string image) =>
        image.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) && image.Length <= 160_000;

    public async Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(string directory, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync("/api/model", cancellationToken, workingDirectory: directory);
        if (response?.RootElement.TryGetProperty("data", out var models) != true || models.ValueKind != JsonValueKind.Array)
            return [];

        return models.EnumerateArray().Select(model =>
        {
            var provider = GetString(model, "providerID");
            var id = GetString(model, "id");
            var cost = model.TryGetProperty("cost", out var costValue) && costValue.ValueKind == JsonValueKind.Array
                ? costValue.EnumerateArray().FirstOrDefault() : default;
            var limit = model.TryGetProperty("limit", out var limitValue) && limitValue.ValueKind == JsonValueKind.Object
                ? limitValue : default;
            return new AvailableModel(
                $"{provider}/{id}",
                GetString(model, "name"),
                GetString(model, "status"),
                model.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                GetNumber(cost, "input"), GetNumber(cost, "output"),
                cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty("input", out _) && cost.TryGetProperty("output", out _),
                GetInt32(limit, "context"), GetInt32(limit, "output"));
        }).Where(model => model.Reference.Contains('/') && model.Name.Length > 0)
            .OrderBy(model => model.Reference, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAvailableAgentsAsync(string directory, CancellationToken cancellationToken)
        => (await GetAvailableAgentOptionsAsync(directory, cancellationToken)).Select(agent => agent.Id).ToArray();

    public async Task<IReadOnlyList<AvailableAgent>> GetAvailableAgentOptionsAsync(string directory, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync("/api/agent", cancellationToken, workingDirectory: directory);
        if (response?.RootElement.TryGetProperty("data", out var agents) != true || agents.ValueKind != JsonValueKind.Array)
            return new[] { new AvailableAgent("build", "Build", "primary"), new AvailableAgent("plan", "Plan", "primary") };
        return agents.EnumerateArray().Select(agent => new AvailableAgent(
                GetString(agent, "id"), GetString(agent, "name"), GetString(agent, "mode")))
            .Where(agent => agent.Id.Length > 0).DistinctBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(agent => agent.Mode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string?> GetDefaultAgentIdAsync(string directory, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync("/api/config", cancellationToken, workingDirectory: directory);
        if (response?.RootElement.TryGetProperty("data", out var entries) != true || entries.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                !string.Equals(path.GetString(), "default_agent", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
    }

    public async Task<ActiveSession> CreateOrchestrationSessionAsync(string directory, string role, string title,
        string? parentSessionId, string? modelReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new ArgumentException("Repository directory does not exist.", nameof(directory));
        if (!string.IsNullOrWhiteSpace(parentSessionId) && !IsValidSessionId(parentSessionId))
            throw new ArgumentException("Invalid parent OpenCode session ID.", nameof(parentSessionId));

        var payload = new Dictionary<string, object?> { ["title"] = title, ["agent"] = role };
        if (string.IsNullOrWhiteSpace(parentSessionId))
            payload["location"] = new { directory };
        else
            payload["parentID"] = parentSessionId;
        if (TryParseModelReference(modelReference, out var model))
            payload["model"] = model;

        using var response = await RequestAsync("/api/session", cancellationToken, HttpMethod.Post,
            JsonSerializer.Serialize(payload), directory);
        if (response?.RootElement.TryGetProperty("data", out var data) != true ||
            !data.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            !IsValidSessionId(idValue.GetString() ?? string.Empty))
            throw new InvalidOperationException("Could not create a linked OpenCode session.");

        var id = idValue.GetString()!;
        _logger.LogInformation("Created linked OpenCode session {SessionId} for role {Role} in repository {RepositoryDirectory}.",
            id, role, directory);
        return new ActiveSession(id, NormalizePath(directory) ?? directory, role, title);
    }

    public async Task WaitForSessionAsync(string sessionId, string directory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        try
        {
            using var response = await RequestAsync($"/api/experimental/session/{sessionId}/wait", cancellationToken,
                HttpMethod.Post, workingDirectory: directory, timeoutSeconds: 300);
            cancellationToken.ThrowIfCancellationRequested();
            if (response is null)
                throw new InvalidOperationException("OpenCode could not confirm that the agent session has finished.");
            // The opencode API CLI prints no JSON for this successful HTTP 204 response.
        }
        finally
        {
            if (_promptAttachmentFiles.TryRemove(sessionId, out var attachments))
                DeleteTemporaryAttachments(attachments);
        }
    }

    private static void DeleteTemporaryAttachments(IEnumerable<string> attachments)
    {
        foreach (var path in attachments)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task InterruptSessionAsync(string sessionId, string directory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        using var response = await RequestAsync($"/api/session/{sessionId}/interrupt", cancellationToken,
            HttpMethod.Post, workingDirectory: directory);
        if (response is null)
            throw new InvalidOperationException("Could not interrupt the OpenCode session.");
    }

    public async Task SwitchModelAsync(string sessionId, string directory, string modelReference,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId) || !TryParseModelReference(modelReference, out var model))
            throw new ArgumentException("A valid session ID and provider/model reference are required.");
        using var response = await RequestAsync($"/api/session/{sessionId}/model", cancellationToken,
            HttpMethod.Post, JsonSerializer.Serialize(new { model }), directory);
        if (response is null)
            throw new InvalidOperationException("Could not select the requested model for the OpenCode session.");
    }

    private static bool TryParseModelReference(string? reference, out object model)
    {
        model = new { };
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var separator = reference.IndexOf('/');
        if (separator < 1 || separator == reference.Length - 1) return false;
        model = new { providerID = reference[..separator], id = reference[(separator + 1)..] };
        return true;
    }

    private static double GetNumber(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;

    private static int GetInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    public async Task<ActiveSession> CreateSessionAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new ArgumentException("Repository directory does not exist.", nameof(directory));

        _logger.LogInformation("Creating OpenCode session for repository directory {RepositoryDirectory}.", directory);
        using var response = await RequestAsync("/api/session", cancellationToken,
            HttpMethod.Post, JsonSerializer.Serialize(new { location = new { directory }, agent = "plan" }), directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idValue.GetString()))
            throw new InvalidOperationException("Could not create a new OpenCode session.");

        var id = idValue.GetString()!;
        var created = new ActiveSession(id, NormalizePath(directory) ?? directory, "plan",
            data.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                ? titleValue.GetString() ?? string.Empty : string.Empty);
        _logger.LogInformation("Created OpenCode session {SessionId} for repository directory {RepositoryDirectory}.", id, directory);
        return created;
    }

    public async Task SwitchAgentAsync(string sessionId, string directory, string agent, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(agent) || agent.Length > 128 ||
            agent.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            throw new ArgumentException("Invalid OpenCode agent ID.", nameof(agent));
        _logger.LogInformation("Switching OpenCode session {SessionId} to agent {Agent}.", sessionId, agent);
        using var response = await RequestAsync($"/api/session/{sessionId}/agent", cancellationToken,
            HttpMethod.Post, JsonSerializer.Serialize(new { agent }), directory);
        if (response is null)
            throw new InvalidOperationException($"Could not switch the OpenCode session to {agent}.");
    }

    private static bool IsValidSessionId(string sessionId) =>
        sessionId.StartsWith("ses_", StringComparison.Ordinal) &&
        sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static string? NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private async Task<JsonDocument?> RequestAsync(string path, CancellationToken cancellationToken,
        HttpMethod? method = null, string? body = null, string? workingDirectory = null, int timeoutSeconds = 5)
    {
        using var repositoryScope = !string.IsNullOrWhiteSpace(workingDirectory)
            ? _logger.BeginScope(new Dictionary<string, object?> { ["RepositoryPath"] = workingDirectory })
            : null;
        if (!OperatingSystem.IsWindows())
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        string executable;
        try { executable = OpenCodeExecutableLocator.Find(); }
        catch (FileNotFoundException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                    ? workingDirectory : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("api");
        process.StartInfo.ArgumentList.Add(method == HttpMethod.Post ? "post" : "get");
        process.StartInfo.ArgumentList.Add(path);
        if (body is not null)
        {
            process.StartInfo.ArgumentList.Add("--data");
            process.StartInfo.ArgumentList.Add(body);
        }
        try
        {
            var startedAt = Stopwatch.GetTimestamp();
            var methodName = method == HttpMethod.Post ? "POST" : "GET";
            if (method == HttpMethod.Post)
            {
                var requestExcerpt = path.EndsWith("/prompt", StringComparison.Ordinal) && body is not null
                    ? ExtractPromptExcerpt(body) : "[request payload omitted]";
                _logger.LogInformation("Calling managed OpenCode API {HttpMethod} {ApiPath}. Request excerpt: {RequestExcerpt}",
                    methodName, path, requestExcerpt);
            }
            else
                _logger.LogInformation("Calling managed OpenCode API {HttpMethod} {ApiPath}.", methodName, path);
            if (!process.Start())
            {
                _logger.LogWarning("Could not start managed OpenCode process for API request {ApiPath}.", path);
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Managed OpenCode API {ApiPath} exited with code {ExitCode} after {ElapsedMilliseconds} ms.",
                    path, process.ExitCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                return null;
            }
            var json = await output;
            if (method == HttpMethod.Post)
            {
                const int maximumResponseLogLength = 12000;
                var responseExcerpt = json.Length <= maximumResponseLogLength ? json : json[..maximumResponseLogLength] + "… [truncated]";
                _logger.LogInformation("Managed OpenCode API {ApiPath} response body: {ResponseBody}", path, responseExcerpt);
            }
            if (method != HttpMethod.Post)
            {
                const int maximumResponseLogLength = 12000;
                var responseExcerpt = json.Length <= maximumResponseLogLength ? json : json[..maximumResponseLogLength] + "… [truncated]";
                _logger.LogInformation("Managed OpenCode API {ApiPath} response body: {ResponseBody}", path, responseExcerpt);
            }
            _logger.LogInformation("Managed OpenCode API {ApiPath} completed successfully in {ElapsedMilliseconds} ms.",
                path, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Managed OpenCode API request {ApiPath} timed out; the next activity poll will retry.", path);
            return null;
        }
        catch (System.Net.Http.HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound &&
                                                                   path.Equals("/api/session/active", StringComparison.Ordinal))
        {
            _activityEndpointFailed = true;
            _logger.LogWarning("Installed OpenCode no longer supports the activity API endpoint; activity polling has been disabled for this app launch.");
            return null;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _logger.LogWarning("Could not start managed OpenCode API request {ApiPath} ({ExceptionType}).", path, exception.GetType().Name);
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning("Managed OpenCode API request {ApiPath} returned invalid JSON ({ExceptionType}).", path, exception.GetType().Name);
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static string ExtractPromptExcerpt(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? CreateLogExcerpt(text.GetString() ?? string.Empty)
                : "[prompt unavailable]";
        }
        catch (JsonException) { return "[prompt unavailable]"; }
    }

    private static string CreateLogExcerpt(string text)
    {
        const int maximumLength = 12000;
        var excerpt = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        excerpt = System.Text.RegularExpressions.Regex.Replace(excerpt,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return excerpt.Length <= maximumLength ? excerpt : excerpt[..maximumLength] + "… [truncated]";
    }
}
