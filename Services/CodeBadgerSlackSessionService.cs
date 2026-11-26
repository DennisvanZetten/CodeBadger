using CodeBadger.Models;

namespace CodeBadger.Services;

/// <summary>Runs CodeBadger terminal sessions started by Slack or Discord.</summary>
public sealed class CodeBadgerSlackSessionService(
    CodeBadgerTerminalStateService stateService,
    OpenCodeActivityService openCode,
    LauncherSettingsService settingsService)
{
    public sealed record StartedSession(OpenCodeActivityService.ActiveSession Session, Guid TerminalSessionId,
        string Response);

    public async Task<StartedSession> StartAsync(RepositoryEntry repository, string prompt,
        CancellationToken cancellationToken)
    {
        EnsureSupported(repository);
        var settings = await settingsService.LoadAsync();
        var state = await stateService.LoadAsync(repository.Id, cancellationToken);
        var terminal = new CodeBadgerTerminalSessionState
        {
            Id = Guid.NewGuid(),
            Title = CreateTitle(prompt),
            Mode = "Plan"
        };
        state.Sessions.Add(terminal);
        state.SelectedSessionId = terminal.Id;
        await stateService.SaveAsync(state, cancellationToken);
        string response;
        try
        {
            response = await ReplyCoreAsync(repository, settings, state, terminal, prompt, cancellationToken);
        }
        finally
        {
            await stateService.SaveAsync(state, cancellationToken);
        }
        var orchestrationMode = CodeBadgerOrchestrationModes.Normalize(repository.CodeBadgerOrchestrationMode);
        var finalRole = orchestrationMode == CodeBadgerOrchestrationModes.Full
            ? CodeBadgerRoles.Orchestrator
            : terminal.Mode == "Build" ? CodeBadgerRoles.Worker : CodeBadgerRoles.Planner;
        var sessionKey = orchestrationMode == CodeBadgerOrchestrationModes.SingleAgent ? "SingleAgent" : finalRole;
        var openCodeSessionId = terminal.AgentSessionIds.GetValueOrDefault(sessionKey)
            ?? terminal.AgentSessionIds.Values.FirstOrDefault(value => value.StartsWith("ses_", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("CodeBadger Terminal did not create an OpenCode session.");
        var session = new OpenCodeActivityService.ActiveSession(openCodeSessionId,
            OpenCodeActivityService.NormalizePath(repository.Path) ?? repository.Path, finalRole == CodeBadgerRoles.Worker ? "build" : "plan", terminal.Title,
            string.IsNullOrWhiteSpace(repository.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path)) : repository.Name);
        return new StartedSession(session, terminal.Id, response);
    }

    public async Task<string> ReplyAsync(string directory, Guid terminalSessionId, string prompt,
        CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync();
        var repository = settings.Repositories.FirstOrDefault(candidate =>
            candidate.CodeBadgerTerminalEnabled && AgentTools.Normalize(candidate.AgentTool) == AgentTools.OpenCode &&
            string.Equals(OpenCodeActivityService.NormalizePath(candidate.Path),
                OpenCodeActivityService.NormalizePath(directory), StringComparison.OrdinalIgnoreCase));
        if (repository is null || !Directory.Exists(repository.Path))
            throw new InvalidOperationException("The configured CodeBadger Terminal repository could not be found.");
        var state = await stateService.LoadAsync(repository.Id, cancellationToken);
        var terminal = state.Sessions.FirstOrDefault(candidate => candidate.Id == terminalSessionId)
            ?? throw new InvalidOperationException("The linked CodeBadger Terminal session could not be found.");
        try
        {
            return await ReplyCoreAsync(repository, settings, state, terminal, prompt, cancellationToken);
        }
        finally
        {
            await stateService.SaveAsync(state, cancellationToken);
        }
    }

    private async Task<string> ReplyCoreAsync(RepositoryEntry repository, LauncherSettings settings,
        CodeBadgerTerminalState state, CodeBadgerTerminalSessionState terminal, string prompt,
        CancellationToken cancellationToken)
    {
        EnsureSupported(repository);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000)
            throw new ArgumentException("The task must contain between 1 and 32,000 characters.", nameof(prompt));
        var availableModels = await openCode.GetAvailableModelsAsync(repository.Path, cancellationToken);
        if (availableModels.Count == 0)
            throw new InvalidOperationException("OpenCode did not return any available models for this repository.");
        var preferences = settings.CodeBadgerAgentPreferences ?? CodeBadgerRoles.CreateAgentDefaults();
        CodeBadgerRoles.NormalizeAgentPreferences(preferences);
        var agentPreferences = preferences.First(item => item.AgentTool == AgentTools.OpenCode);
        var role = terminal.Mode == "Build" ? CodeBadgerRoles.Worker : CodeBadgerRoles.Planner;
        var mode = CodeBadgerOrchestrationModes.Normalize(repository.CodeBadgerOrchestrationMode);
        var forceBuild = false;
        var command = System.Text.RegularExpressions.Regex.Match(prompt,
            "^!(build|plan)(?:\\s+(.*))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (command.Success)
        {
            prompt = command.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Add a task after !build or !plan.");
            terminal.Mode = string.Equals(command.Groups[1].Value, "build", StringComparison.OrdinalIgnoreCase) ? "Build" : "Plan";
            forceBuild = string.Equals(command.Groups[1].Value, "build", StringComparison.OrdinalIgnoreCase);
            role = terminal.Mode == "Build" ? CodeBadgerRoles.Worker : CodeBadgerRoles.Planner;
        }

        terminal.Messages.Add(new CodeBadgerTerminalMessage { Role = "user", Text = prompt });
        var replies = new List<string>();
        // Run the configured terminal orchestration using the same persisted role IDs as RepositoryWorkspace.
        if (command.Success)
        {
            replies.Add(await RunRoleAsync(repository, settings, agentPreferences, terminal, role, prompt,
                availableModels, cancellationToken));
        }
        else if (mode == CodeBadgerOrchestrationModes.SingleAgent)
        {
            replies.Add(await RunRoleAsync(repository, settings, agentPreferences, terminal, role, prompt,
                availableModels, cancellationToken));
        }
        else if (mode == CodeBadgerOrchestrationModes.PlanBuild)
        {
            var currentMode = terminal.Mode;
            var plan = terminal.Messages.Take(terminal.Messages.Count - 1)
                .LastOrDefault(message => message.Role == "assistant" && message.AgentRole == CodeBadgerRoles.Planner &&
                    PlanBuildHandoff.IsSameRequest(message.SourceRequest, prompt))?.Text;
            if (currentMode == "Plan" || string.IsNullOrWhiteSpace(plan))
            {
                plan = await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Planner,
                    currentMode == "Plan" ? prompt : "First produce an actionable plan for this implementation request. Do not edit files yet.\n\n" + prompt,
                    availableModels, cancellationToken);
                var planMessage = terminal.Messages.LastOrDefault(message => message.Role == "assistant" && message.AgentRole == CodeBadgerRoles.Planner);
                if (planMessage is not null)
                    planMessage.SourceRequest = prompt;
            }
            if (currentMode == "Build" || forceBuild)
            {
                replies.Add(await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Worker,
                    PlanBuildHandoff.CreateWorkerPrompt(prompt, plan), availableModels, cancellationToken));
                terminal.Mode = "Build";
            }
            else
            {
                replies.Add(plan ?? string.Empty);
            }
            terminal.Mode = "Build";
        }
        else
        {
            var route = await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Router,
                "Classify this user request. Reply with concise JSON containing task_type, needs_research (boolean), needs_implementation (boolean), and summary. Do not perform the task yet.\n\nRequest:\n" + prompt,
                availableModels, cancellationToken);
            var assignments = await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Orchestrator,
                "Act as manager. Break the task into subtasks for planning, research, implementation, and review. Return concise JSON listing subtasks and dependencies. Do not inspect or modify files; CodeBadger dispatches roles. Routing analysis: " + route + "\n\nRequest:\n" + prompt,
                availableModels, cancellationToken);
            var needsResearch = ReadBooleanField(route, "needs_research");
            var needsImplementation = ReadBooleanField(route, "needs_implementation");
            string? research = null;
            if (needsResearch)
                research = await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Rag,
                    "Research relevant repository context, files, and available knowledge sources. Report evidence and paths.\n\n" + prompt,
                    availableModels, cancellationToken);
            var plan = await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Planner,
                "Develop an actionable plan based on the manager's task breakdown. Manager breakdown: " + assignments + "\nRouting analysis: " + route + "\n\nRequest:\n" + prompt,
                availableModels, cancellationToken);
            replies.Add(plan);
            var context = "Request:\n" + prompt + "\n\nPlan:\n" + plan +
                (research is null ? string.Empty : "\n\nResearch findings:\n" + research);
            var build = needsImplementation
                ? await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Worker,
                    "Execute this plan in the repository, update files, run relevant checks, then summarize changed files and test results.\n\n" + context,
                    availableModels, cancellationToken)
                : "No implementation was requested by the triage agent.";
            replies.Add(build);
            var review = await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Critic,
                "Review the plan and any implementation/test report. Reply only with JSON {\"needs_changes\": boolean, \"findings\": [strings]}. Do not edit files.\n\nRequest:\n" + prompt + "\n\nPlan:\n" + plan + "\n\nImplementation report:\n" + build,
                availableModels, cancellationToken);
            if (needsImplementation && ReadBooleanField(review, "needs_changes"))
                replies.Add(await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Worker,
                    "Address these review findings, run relevant checks, and report what changed:\n\n" + ReadStringField(review, "findings"),
                    availableModels, cancellationToken));
            replies.Add(await RunRoleAsync(repository, settings, agentPreferences, terminal, CodeBadgerRoles.Orchestrator,
                "Summarize the completed task, implementation, and review for the user.\n\nRequest:\n" + prompt + "\n\nPlan:\n" + plan + "\n\nImplementation:\n" + build + "\n\nReview:\n" + review,
                availableModels, cancellationToken));
            terminal.Mode = "Build";
        }
        terminal.Title = string.IsNullOrWhiteSpace(terminal.Title) ? CreateTitle(prompt) : terminal.Title;
        state.SelectedSessionId = terminal.Id;
        return string.Join("\n\n", replies.Where(reply => !string.IsNullOrWhiteSpace(reply)));
    }

    private async Task<string> RunRoleAsync(RepositoryEntry repository, LauncherSettings settings,
        CodeBadgerAgentPreferences agentPreferences, CodeBadgerTerminalSessionState terminal, string role,
        string prompt, IReadOnlyList<OpenCodeActivityService.AvailableModel> availableModels,
        CancellationToken cancellationToken)
    {
        var preference = agentPreferences.RolePreferences.FirstOrDefault(item => item.Role == role)
            ?? new CodeBadgerRolePreference { Role = role, AgentType = CodeBadgerRoles.DefaultAgentType(role) };
        var agentOptions = await openCode.GetAvailableAgentOptionsAsync(repository.Path, cancellationToken);
        string? requestedAgent = preference.AgentType switch
        {
            "Plan" => "plan",
            "Build" => "build",
            "Custom" => preference.CustomAgentId,
            _ => await openCode.GetDefaultAgentIdAsync(repository.Path, cancellationToken)
        };
        var selectedAgent = agentOptions.FirstOrDefault(agent =>
            string.Equals(agent.Id, requestedAgent, StringComparison.OrdinalIgnoreCase));
        if (selectedAgent is null && preference.AgentType == "Primary")
            selectedAgent = agentOptions.FirstOrDefault(agent => agent.Mode is "primary" or "all");
        if (selectedAgent is null && preference.AgentType == "Custom")
            selectedAgent = agentOptions.FirstOrDefault(agent => agent.Mode is "subagent" or "all");
        if (selectedAgent is null)
        {
            var fallbackId = role is CodeBadgerRoles.Orchestrator or CodeBadgerRoles.Router or CodeBadgerRoles.Planner
                ? "plan" : "build";
            selectedAgent = agentOptions.FirstOrDefault(agent =>
                string.Equals(agent.Id, fallbackId, StringComparison.OrdinalIgnoreCase));
        }
        var agentId = selectedAgent?.Id ?? requestedAgent;
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException($"Could not resolve the configured OpenCode agent for {role}.");

        var mode = CodeBadgerOrchestrationModes.Normalize(repository.CodeBadgerOrchestrationMode);
        var configuredModels = mode switch
        {
            CodeBadgerOrchestrationModes.SingleAgent => agentPreferences.SingleAgentPreferredModels,
            CodeBadgerOrchestrationModes.PlanBuild when role == CodeBadgerRoles.Worker => agentPreferences.BuildPreferredModels,
            CodeBadgerOrchestrationModes.PlanBuild => agentPreferences.PlanPreferredModels,
            _ => preference.PreferredModels
        };
        var model = availableModels.FirstOrDefault(candidate => candidate.Enabled && candidate.Status == "active" &&
            string.Equals(candidate.Reference, settings.CodeBadgerTerminalModel, StringComparison.OrdinalIgnoreCase))?.Reference;
        model ??= configuredModels.FirstOrDefault(reference => availableModels.Any(candidate => candidate.Enabled &&
            candidate.Status == "active" && string.Equals(candidate.Reference, reference, StringComparison.OrdinalIgnoreCase)));
        model ??= availableModels.FirstOrDefault(candidate => candidate.Enabled && candidate.Status == "active")?.Reference;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("No enabled OpenCode model is available.");

        var key = mode == CodeBadgerOrchestrationModes.SingleAgent ? "SingleAgent" : role;
        if (!terminal.AgentSessionIds.TryGetValue(key, out var sessionId))
        {
            var created = await openCode.CreateOrchestrationSessionAsync(repository.Path, agentId, terminal.Title,
                null, model, cancellationToken);
            sessionId = created.Id;
            terminal.AgentSessionIds[key] = sessionId;
        }
        else
        {
            await openCode.SwitchModelAsync(sessionId, repository.Path, model, cancellationToken);
            await openCode.SwitchAgentAsync(sessionId, repository.Path, agentId, cancellationToken);
        }

        var previousMessages = await openCode.GetSessionMessageIdsAsync(sessionId, repository.Path, cancellationToken)
            ?? throw new InvalidOperationException("Could not read the existing OpenCode session messages.");
        var promptMessageId = await openCode.SendPromptAsync(sessionId, repository.Path, prompt, cancellationToken, agentId);
        if (string.IsNullOrWhiteSpace(promptMessageId))
            throw new InvalidOperationException("OpenCode did not return a message ID for the submitted task.");
        await openCode.WaitForSessionAsync(sessionId, repository.Path, cancellationToken);
        var costs = await openCode.GetCompletedPromptCostsAsync(sessionId, repository.Path, promptMessageId,
            previousMessages, cancellationToken);
        foreach (var cost in costs)
        {
            var costKey = sessionId + ":" + cost.Id;
            if (!terminal.CostMessageIds.Contains(costKey, StringComparer.Ordinal))
            {
                terminal.CostMessageIds.Add(costKey);
                terminal.CostUsd += cost.CostUsd;
            }
        }
        var answer = await WaitForAnswerAsync(sessionId, repository.Path, promptMessageId, previousMessages, cancellationToken);
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("OpenCode finished without a text response for this task.");
        var costLimit = repository.CodeBadgerSessionCostLimitUsd.GetValueOrDefault();
        if (costLimit > 0 && terminal.CostUsd >= costLimit)
            answer += $"\n\nThe CodeBadger session cost limit of ${costLimit:0.00} has been reached.";
        terminal.AgentSessionIds[key] = sessionId;
        terminal.AgentSessionIds[mode == CodeBadgerOrchestrationModes.SingleAgent ? "Model:SingleAgent" : "Model:" + role] = model;
        terminal.Messages.Add(new CodeBadgerTerminalMessage { Role = "assistant", AgentRole = role,
            ModelReference = model, Text = answer });
        return answer;
    }

    private async Task<string?> WaitForAnswerAsync(string sessionId, string directory, string promptMessageId,
        IReadOnlySet<string> previousMessageIds, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var messages = await openCode.GetPromptTextMessagesAsync(sessionId, directory, promptMessageId,
                previousMessageIds, cancellationToken);
            var answer = string.Join("\n\n", messages.Where(message => message.IsFinal).Select(message => message.Text));
            if (!string.IsNullOrWhiteSpace(answer))
                return answer;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return null;
    }

    private static bool ReadBooleanField(string json, string field)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(field, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static string ReadStringField(string json, string field)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(field, out var value))
                return json;
            return value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? string.Join("\n", value.EnumerateArray()
                    .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(item => item.GetString()))
                : value.ToString();
        }
        catch (System.Text.Json.JsonException) { return json; }
    }

    private static void EnsureSupported(RepositoryEntry repository)
    {
        if (!repository.CodeBadgerTerminalEnabled || AgentTools.Normalize(repository.AgentTool) != AgentTools.OpenCode)
            throw new NotSupportedException("CodeBadger Terminal via Slack or Discord is currently supported only when CodeBadger Terminal and OpenCode are enabled in the repository settings.");
        if (repository.OpenCodeSandboxEnabled)
            throw new NotSupportedException("CodeBadger Terminal via Slack or Discord does not support OpenCode sandbox repositories yet.");
        if (!Directory.Exists(repository.Path))
            throw new DirectoryNotFoundException("The configured repository folder does not exist.");
    }

    private static string CreateTitle(string prompt)
    {
        var title = prompt.Split(['\r', '\n'], 2, StringSplitOptions.TrimEntries)[0];
        return title.Length <= 72 ? title : title[..69].TrimEnd() + "…";
    }
}
