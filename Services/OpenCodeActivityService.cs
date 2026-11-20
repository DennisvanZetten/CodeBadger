using System.Diagnostics;
using System.Text.Json;

namespace CodeBadger.Services;

public sealed class OpenCodeActivityService
{
    public sealed record ActiveSession(string Id, string Directory, string Agent, string Title);
    public sealed record ActivitySnapshot(HashSet<string> ActiveDirectories, HashSet<string> QuestionDirectories,
        Dictionary<string, ActiveSession> Sessions);

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
                if (permissionResponse?.RootElement.TryGetProperty("data", out var permissions) == true &&
                    permissions.ValueKind == JsonValueKind.Array && permissions.GetArrayLength() > 0)
                    questionDirectories.Add(normalized);
                if (formResponse?.RootElement.TryGetProperty("data", out var forms) == true &&
                    forms.ValueKind == JsonValueKind.Array && forms.GetArrayLength() > 0)
                    questionDirectories.Add(normalized);
            }
            return new ActivitySnapshot(activeDirectories, questionDirectories, activeSessions);
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
        using var response = await RequestAsync($"/api/session/{sessionId}/message?order=desc&limit=20&type=assistant", cancellationToken,
            workingDirectory: directory);
        if (response is null || !response.RootElement.TryGetProperty("data", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("type", out var type) || type.GetString() != "assistant" ||
                !message.TryGetProperty("finish", out var finish) || finish.GetString() is not ("stop" or "length") ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            var text = string.Join("\n", content.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var partType) && partType.GetString() == "text")
                .Select(part => part.TryGetProperty("text", out var partText) ? partText.GetString() : null)
                .Where(partText => !string.IsNullOrWhiteSpace(partText))).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return await SummarizeAnswerAsync(sessionId, directory, text, cancellationToken);
        }
        return null;
    }

    private static async Task<string> SummarizeAnswerAsync(string sessionId, string directory, string answer,
        CancellationToken cancellationToken)
    {
        var source = answer.Length > 12000 ? answer[..12000] : answer;
        var prompt = "Summarize only the assistant's latest completed answer below for a brief Slack notification. " +
            "Keep important outcomes and actions, use the same language as the answer, and do not add information. " +
            "Return only the summary.\n\nLatest completed answer:\n" + source;
        var body = JsonSerializer.Serialize(new { prompt });
        using var response = await RequestAsync($"/api/session/{sessionId}/generate", cancellationToken,
            HttpMethod.Post, body, directory);
        if (response is not null && response.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("text", out var summary) && summary.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(summary.GetString()))
            return summary.GetString()!.Trim();
        return source.Length > 3500 ? source[..3500] + "\n…" : source;
    }

    public async Task<bool> HasPendingInteractionAsync(string sessionId, string directory, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            return false;
        using var permissions = await RequestAsync($"/api/session/{sessionId}/permission", cancellationToken, workingDirectory: directory);
        using var forms = await RequestAsync($"/api/session/{sessionId}/form", cancellationToken, workingDirectory: directory);
        return HasItems(permissions) || HasItems(forms);
    }

    private static bool HasItems(JsonDocument? response) => response is not null &&
        response.RootElement.TryGetProperty("data", out var items) &&
        items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;

    public async Task SendPromptAsync(string sessionId, string directory, string text, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
            throw new ArgumentException("Invalid OpenCode session ID.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000)
            throw new ArgumentException("Reply must contain 1–32,000 characters.", nameof(text));
        using var response = await RequestAsync($"/api/session/{sessionId}/prompt", cancellationToken,
            HttpMethod.Post, JsonSerializer.Serialize(new { text }), directory);
        if (response is null)
            throw new InvalidOperationException("Could not submit the reply to the OpenCode session.");
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
