using System.Diagnostics;
using System.Text.Json;

namespace CodeBadger.Services;

public sealed class OpenCodeActivityService
{
    public sealed record ActiveSession(string Id, string Directory, string Agent, string Title);
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

    public async Task SendPromptAsync(string sessionId, string directory, string text, CancellationToken cancellationToken,
        string? agent = null)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000)
            throw new ArgumentException("Reply must contain 1–32,000 characters.", nameof(text));
        var payload = agent is "build" or "plan"
            ? JsonSerializer.Serialize(new { text, delivery = "steer", agent })
            : JsonSerializer.Serialize(new { text, delivery = "steer" });
        using var response = await RequestAsync($"/api/session/{sessionId}/prompt", cancellationToken,
            HttpMethod.Post, payload, directory);
        if (response is null)
            throw new InvalidOperationException("Could not submit the reply to the OpenCode session.");
    }

    public async Task<ActiveSession> CreateSessionAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new ArgumentException("Repository directory does not exist.", nameof(directory));

        using var response = await RequestAsync("/api/session", cancellationToken,
            HttpMethod.Post, JsonSerializer.Serialize(new { location = new { directory }, agent = "plan" }), directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idValue.GetString()))
            throw new InvalidOperationException("Could not create a new OpenCode session.");

        var id = idValue.GetString()!;
        return new ActiveSession(id, NormalizePath(directory) ?? directory, "plan",
            data.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                ? titleValue.GetString() ?? string.Empty : string.Empty);
    }

    public async Task SwitchAgentAsync(string sessionId, string directory, string agent, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        if (agent is not ("build" or "plan"))
            throw new ArgumentException("Only the build and plan agents can be selected from Slack.", nameof(agent));
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

    private static async Task<JsonDocument?> RequestAsync(string path, CancellationToken cancellationToken,
        HttpMethod? method = null, string? body = null, string? workingDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
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
                RedirectStandardError = true
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
            if (!process.Start())
                return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0)
                return null;
            var json = await output;
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (JsonException) { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
