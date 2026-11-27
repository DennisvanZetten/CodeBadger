using System.Diagnostics;
using System.Text.Json;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

/// <summary>Runs CodeBadger composer turns through the selected agent's documented CLI protocol.</summary>
public sealed class CodeBadgerCliAgentRunner(ILogger<CodeBadgerCliAgentRunner> logger)
{
    public sealed record AgentReply(string SessionId, string Text);

    public async Task<AgentReply> SendPromptAsync(string agentTool, string repositoryPath, string? sessionId,
        string role, string prompt, IReadOnlyList<string>? images, CancellationToken cancellationToken,
        Func<string, Task>? onText = null, string? model = null,
        Action<Process>? onProcessStarted = null)
    {
        var agent = AgentTools.Normalize(agentTool);
        var existingSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        var attachments = new List<string>();
        try
        {
            if ((images ?? []).Any(image => !image.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) || image.Length > 160_000))
                throw new ArgumentException("Only JPEG images up to 120 KB are supported.", nameof(images));
            if (agent == AgentTools.Hermes && (images?.Count ?? 0) > 0)
                throw new NotSupportedException("Hermes CLI image attachments are not supported by the current Hermes CLI protocol adapter.");
            if (agent != AgentTools.Codex)
                foreach (var image in images ?? [])
                    if (image.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase))
                        attachments.Add(WriteImageAttachment(image));
            var processStart = CreateStartInfo(agent, repositoryPath, existingSessionId, role, attachments, model);
            var initialSessionId = agent == AgentTools.ClaudeCode && existingSessionId is null
                ? Guid.NewGuid().ToString()
                : existingSessionId;
            if (agent == AgentTools.ClaudeCode && existingSessionId is null)
            {
                processStart.ArgumentList.Add("--session-id");
                processStart.ArgumentList.Add(initialSessionId!);
            }
            ConfigureWindowsScriptCommand(processStart);
            using var process = new Process { StartInfo = processStart, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {AgentTools.GetDisplayName(agent)}.");
            onProcessStarted?.Invoke(process);

            using var killOnCancel = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });

            if (agent == AgentTools.Codex)
            {
                var codexReply = await RunCodexAppServerTurnAsync(process, repositoryPath, existingSessionId,
                    role, prompt, attachments, model, onText, cancellationToken);
                logger.LogInformation("Completed CodeBadger composer turn through {Agent} in {RepositoryPath}.",
                    agent, repositoryPath);
                return codexReply;
            }

            var outputTask = ReadOutputAsync(agent, process.StandardOutput, cancellationToken, onText);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var promptAlreadyContainsRole = prompt.StartsWith("CodeBadger role:", StringComparison.Ordinal);
            var instruction = role == CodeBadgerRoles.Worker
                ? $"CodeBadger role: {role}. Complete the requested work in the repository and report the result.\n\n"
                : $"CodeBadger role: {role}. Analyze and respond without modifying repository files.\n\n";
            var normalizedPrompt = promptAlreadyContainsRole ? prompt : instruction + prompt;
            var fullPrompt = normalizedPrompt + (attachments.Count == 0 ? string.Empty :
                "\n\nInspect the attached image file(s) for context:\n" + string.Join("\n", attachments));
            if (agent == AgentTools.ClaudeCode)
            {
                await process.StandardInput.WriteLineAsync(fullPrompt.AsMemory(), cancellationToken);
            }
            else
                await process.StandardInput.WriteAsync(fullPrompt.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(FormatFailure(agent, process.ExitCode, output.Text, error));
            if (string.IsNullOrWhiteSpace(output.Text))
                throw new InvalidOperationException($"{AgentTools.GetDisplayName(agent)} completed without a text response.");

            var resultSessionId = output.SessionId;
            if (string.IsNullOrWhiteSpace(resultSessionId))
                resultSessionId = initialSessionId ?? existingSessionId;
            if (string.IsNullOrWhiteSpace(resultSessionId))
                throw new InvalidOperationException($"{AgentTools.GetDisplayName(agent)} did not return a resumable session ID.");

            logger.LogInformation("Completed CodeBadger composer turn through {Agent} in {RepositoryPath}.",
                agent, repositoryPath);
            return new AgentReply(resultSessionId, output.Text.Trim());
        }
        finally
        {
            foreach (var attachment in attachments)
                try { File.Delete(attachment); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string agent, string directory, string? sessionId,
        string role, IReadOnlyList<string> images, string? model)
    {
        var start = new ProcessStartInfo
        {
            FileName = AgentTools.GetCommand(agent),
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Preserve the selected agent's existing config, MCP servers, hooks and skills.
        // Only the narrow command flags needed for non-interactive operation are added.

        switch (agent)
        {
            case AgentTools.ClaudeCode:
                start.ArgumentList.Add("--print");
                start.ArgumentList.Add("--input-format");
                start.ArgumentList.Add("stream-json");
                start.ArgumentList.Add("--output-format");
                start.ArgumentList.Add("stream-json");
                start.ArgumentList.Add("--verbose");
                start.ArgumentList.Add("--include-partial-messages");
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    start.ArgumentList.Add("--resume");
                    start.ArgumentList.Add(sessionId);
                }
                if (!string.IsNullOrWhiteSpace(model))
                {
                    start.ArgumentList.Add("--model");
                    start.ArgumentList.Add(model);
                }
                if (role == CodeBadgerRoles.Worker)
                {
                    start.ArgumentList.Add("--permission-mode");
                    start.ArgumentList.Add("acceptEdits");
                    start.ArgumentList.Add("--permission-prompts");
                    start.ArgumentList.Add("none");
                }
                else
                {
                    start.ArgumentList.Add("--permission-mode");
                    start.ArgumentList.Add("plan");
                }
                break;
            case AgentTools.Codex:
                start.ArgumentList.Add("app-server");
                if (!string.IsNullOrWhiteSpace(model))
                    start.Environment["CODEBADGER_CODEX_MODEL"] = model;
                break;
            case AgentTools.Hermes:
                start.ArgumentList.Add("chat");
                start.ArgumentList.Add("--query-file");
                start.ArgumentList.Add("-");
                start.ArgumentList.Add("--format");
                start.ArgumentList.Add("stream-json");
                if (!string.IsNullOrWhiteSpace(model))
                {
                    start.ArgumentList.Add("--model");
                    start.ArgumentList.Add(model);
                }
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    start.ArgumentList.Add("--resume");
                    start.ArgumentList.Add(sessionId);
                }
                break;
            default:
                throw new NotSupportedException($"{AgentTools.GetDisplayName(agent)} does not use a CLI connector.");
        }

        return start;
    }

    private static void ConfigureWindowsScriptCommand(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = ResolveWindowsExecutable(start.FileName);
        if (executable is null) return;
        var extension = Path.GetExtension(executable);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = executable;
            return;
        }

        var args = start.ArgumentList.ToArray();
        start.ArgumentList.Clear();
        start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("call " + QuoteForCmd(executable) + string.Concat(args.Select(argument => " " + QuoteForCmd(argument))));
    }

    private static string? ResolveWindowsExecutable(string command)
    {
        var extensions = Path.HasExtension(command)
            ? new[] { string.Empty }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directories = Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar)
            ? new[] { Path.GetDirectoryName(command) ?? string.Empty }
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in directories)
            foreach (var extension in extensions)
            {
                var candidate = Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar)
                    ? command + extension
                    : Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static string QuoteForCmd(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static async Task<AgentReply> RunCodexAppServerTurnAsync(Process process, string repositoryPath,
        string? sessionId, string role, string prompt, IReadOnlyList<string> images, string? model,
        Func<string, Task>? onText, CancellationToken cancellationToken)
    {
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var writer = process.StandardInput;
        var reader = process.StandardOutput;

        async Task SendAsync(object message)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        async Task<JsonDocument> ReadAsync()
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return JsonDocument.Parse(line); }
                catch (JsonException) { }
            }
            throw new InvalidOperationException("Codex app-server closed before completing the request.");
        }

        async Task<JsonDocument> ReadResponseAsync(int expectedId)
        {
            while (true)
            {
                using var document = await ReadAsync();
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Codex app-server error: " + error.ToString());
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expectedId)
                    return JsonDocument.Parse(root.GetRawText());
            }
        }

        async Task<JsonElement> SendRequestAsync(int id, string method, object parameters)
        {
            await SendAsync(new { method, id, @params = parameters });
            using var response = await ReadResponseAsync(id);
            if (!response.RootElement.TryGetProperty("result", out var result))
                throw new InvalidOperationException($"Codex app-server returned no result for {method}.");
            return result.Clone();
        }

        await SendAsync(new
        {
            method = "initialize", id = 0,
            @params = new { clientInfo = new { name = "codebadger", title = "CodeBadger", version = "1.0" } }
        });
        using (var initialized = await ReadResponseAsync(0)) { }
        await SendAsync(new { method = "initialized", @params = new { } });

        var availableModels = await SendRequestAsync(3, "model/list", new { limit = 200, includeHidden = false });
        var listedModels = availableModels.TryGetProperty("data", out var modelArray) && modelArray.ValueKind == JsonValueKind.Array
            ? modelArray.EnumerateArray().ToArray() : [];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = listedModels.FirstOrDefault(candidate => candidate.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)
                is var defaultModel && defaultModel.ValueKind == JsonValueKind.Object ? GetString(defaultModel, "id") : null;
        }
        else
        {
            var requestedModel = listedModels.FirstOrDefault(candidate =>
                string.Equals(GetString(candidate, "id"), model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetString(candidate, "model"), model, StringComparison.OrdinalIgnoreCase));
            if (requestedModel.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException($"Codex model '{model}' is not available to this account.");
            model = GetString(requestedModel, "id");
        }

        JsonElement thread;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var threadParams = new Dictionary<string, object?>
            {
                ["cwd"] = repositoryPath,
                ["approvalPolicy"] = "never",
                ["sandbox"] = role == CodeBadgerRoles.Worker ? "workspace-write" : "read-only",
                ["serviceName"] = "CodeBadger"
            };
            if (!string.IsNullOrWhiteSpace(model)) threadParams["model"] = model;
            await SendAsync(new { method = "thread/start", id = 1, @params = threadParams });
        }
        else
        {
            await SendAsync(new { method = "thread/resume", id = 1, @params = new { threadId = sessionId, excludeTurns = true } });
        }
        using (var threadResponse = await ReadResponseAsync(1))
        {
            if (!threadResponse.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("thread", out thread))
                throw new InvalidOperationException("Codex app-server did not return a thread.");
            thread = thread.Clone();
        }
        var threadId = GetString(thread, "id");
        if (threadId.Length == 0)
            throw new InvalidOperationException("Codex app-server returned an empty thread ID.");

        var fullPrompt = BuildPrompt(role, prompt, images);
        var input = new List<object> { new { type = "text", text = fullPrompt } };
        foreach (var image in images)
            input.Add(new { type = "localImage", path = image });
        var turnParams = new Dictionary<string, object?> { ["threadId"] = threadId, ["input"] = input };
        if (!string.IsNullOrWhiteSpace(model)) turnParams["model"] = model;
        await SendAsync(new { method = "turn/start", id = 2, @params = turnParams });

        string? turnId = null;
        var completed = false;
        var responseText = new System.Text.StringBuilder();
        var seenMessageItems = new HashSet<string>(StringComparer.Ordinal);
        while (!completed)
        {
            using var document = await ReadAsync();
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Codex app-server error: " + error.ToString());
            if (root.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == 2 &&
                root.TryGetProperty("result", out var turnResult) && turnResult.TryGetProperty("turn", out var startedTurn))
                turnId = GetString(startedTurn, "id");

            var method = GetString(root, "method");
            if (!root.TryGetProperty("params", out var parameters)) continue;
            if (method == "item/agentMessage/delta")
            {
                var delta = GetString(parameters, "delta");
                if (delta.Length > 0)
                {
                    responseText.Append(delta);
                    if (onText is not null) await onText(delta);
                }
            }
            else if (method == "item/completed" && parameters.TryGetProperty("item", out var item) &&
                     GetString(item, "type") == "agent_message")
            {
                var itemId = GetString(item, "id");
                if (itemId.Length == 0 || !seenMessageItems.Add(itemId)) continue;
                var message = GetString(item, "text");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    var freshText = message.StartsWith(responseText.ToString(), StringComparison.Ordinal)
                        ? message[responseText.Length..] : message;
                    if (freshText.Length > 0)
                    {
                        responseText.Append(freshText);
                        if (onText is not null) await onText(freshText);
                    }
                }
            }
            else if (method == "turn/completed" && parameters.TryGetProperty("turn", out var finishedTurn) &&
                     (turnId is null || GetString(finishedTurn, "id") == turnId))
            {
                completed = true;
                if (GetString(finishedTurn, "status") != "completed")
                    throw new InvalidOperationException("Codex turn did not complete: " + finishedTurn.ToString());
            }
            else if (method == "item/permissions/requestApproval" && root.TryGetProperty("id", out var serverRequestId))
            {
                await SendAsync(new
                {
                    id = serverRequestId,
                    result = new { decision = "decline", reason = "CodeBadger does not support interactive approval responses for this Codex request." }
                });
            }
            else if (method == "tool/requestUserInput" && root.TryGetProperty("id", out var inputRequestId))
            {
                await SendAsync(new
                {
                    id = inputRequestId,
                    result = new { answers = new { } }
                });
            }
        }

        writer.Close();
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(FormatFailure(AgentTools.Codex, process.ExitCode, responseText.ToString(), stderr));
        if (responseText.Length == 0)
            throw new InvalidOperationException("Codex returned no response. " + stderr.Trim());
        return new AgentReply(threadId, responseText.ToString().Trim());
    }

    private static string BuildPrompt(string role, string prompt, IReadOnlyList<string> images)
    {
        var instruction = role == CodeBadgerRoles.Worker
            ? $"CodeBadger role: {role}. Complete the requested work in the repository and report the result.\n\n"
            : $"CodeBadger role: {role}. Analyze and respond without modifying repository files.\n\n";
        return instruction + prompt;
    }

    private static string WriteImageAttachment(string dataUrl)
    {
        const string prefix = "data:image/jpeg;base64,";
        if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || dataUrl.Length > 160_000)
            throw new ArgumentException("Only JPEG images up to 120 KB are supported.", nameof(dataUrl));
        var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
        var path = Path.Combine(Path.GetTempPath(), $"codebadger-clipboard-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<(string Text, string? SessionId)> ReadOutputAsync(string agent,
        StreamReader reader, CancellationToken cancellationToken, Func<string, Task>? onText)
    {
        var text = new System.Text.StringBuilder();
        string? sessionId = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (agent == AgentTools.ClaudeCode)
                {
                    sessionId ??= GetString(root, "session_id");
                    if (GetString(root, "type") == "result")
                        await AppendAsync(text, GetString(root, "result"), null, replace: true);
                    else if (GetString(root, "type") == "assistant" && root.TryGetProperty("message", out var message) &&
                             message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var block in content.EnumerateArray())
                            if (GetString(block, "type") == "text") await AppendAsync(text, GetString(block, "text"), onText);
                }
                else if (agent == AgentTools.Codex)
                {
                    sessionId ??= GetString(root, "thread_id");
                    if (root.TryGetProperty("thread", out var thread))
                        sessionId ??= GetString(thread, "id");
                    var item = root.TryGetProperty("item", out var itemValue) ? itemValue : default;
                    if (GetString(item, "type") == "agent_message") await AppendAsync(text, GetString(item, "text"), onText);
                    if (GetString(root, "type") == "turn.failed")
                        await AppendAsync(text, GetString(root, "error"), onText);
                }
                else
                {
                    sessionId ??= GetString(root, "session_id");
                    var type = GetString(root, "type");
                    if (type == "text") await AppendAsync(text, GetString(root, "text"), onText);
                    if (type == "system" && GetString(root, "subtype") == "init")
                        sessionId ??= GetString(root, "session_id");
                    if (type == "result")
                    {
                        var finalText = GetString(root, "text");
                        if (!string.IsNullOrWhiteSpace(finalText))
                        {
                            text.Clear();
                            await AppendAsync(text, finalText, null, replace: true);
                        }
                        sessionId ??= GetString(root, "session_id");
                    }
                }
            }
            catch (JsonException)
            {
                await AppendAsync(text, line, onText);
            }
        }
        return (text.ToString(), sessionId);
    }

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static async Task AppendAsync(System.Text.StringBuilder target, string? value,
        Func<string, Task>? onText, bool replace = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (replace) target.Clear();
        var delta = target.Length == 0 ? value : "\n" + value;
        target.Append(delta);
        if (onText is not null) await onText(delta);
    }

    private static string FormatFailure(string agent, int exitCode, string output, string error)
    {
        var detail = !string.IsNullOrWhiteSpace(error) ? error.Trim() : output.Trim();
        if (detail.Length > 1500) detail = detail[..1500] + "…";
        return $"{AgentTools.GetDisplayName(agent)} failed with exit code {exitCode}" +
            (detail.Length == 0 ? "." : $": {detail}");
    }
}
