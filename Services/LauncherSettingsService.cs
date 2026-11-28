using System.Text.Json;
using System.Text.Json.Nodes;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed class LauncherSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
    private readonly LocalizationService _localization;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private const string AiApiKeyStorageKey = "codebadger-ai-api-key";
    private const string SlackBotTokenStorageKey = "codebadger-slack-bot-token";
    private const string SlackAppTokenStorageKey = "codebadger-slack-app-token";
    private const string DiscordBotTokenStorageKey = "codebadger-discord-bot-token";
    public event Action? SettingsSaved;

    public LauncherSettingsService(LocalizationService localization) => _localization = localization;

    public async Task<LauncherSettings> LoadAsync()
    {
        await _settingsLock.WaitAsync();
        try
        {
            if (!File.Exists(_settingsPath))
                return new LauncherSettings();

            LauncherSettings settings;
            string? debugAiApiKey = null;
#if MACCATALYST && DEBUG
            string? debugSlackBotToken = null;
            string? debugSlackAppToken = null;
            string? debugDiscordBotToken = null;
#endif
            try
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                var root = JsonNode.Parse(json);
#if MACCATALYST && DEBUG
                debugAiApiKey = root?[nameof(LauncherSettings.AiApiKey)]?.GetValue<string>();
                debugSlackBotToken = root?[nameof(LauncherSettings.SlackBotToken)]?.GetValue<string>();
                debugSlackAppToken = root?[nameof(LauncherSettings.SlackAppToken)]?.GetValue<string>();
                debugDiscordBotToken = root?[nameof(LauncherSettings.DiscordBotToken)]?.GetValue<string>();
#endif
                MigrateLoggingDefaults(root);
                MigrateLegacyTicketSettings(root);
                MigrateLegacyAiCommitInstruction(root);
                MigrateCodeBadgerModelPreferences(root);
                settings = root?.Deserialize<LauncherSettings>(JsonOptions) ?? new LauncherSettings();
            }
            catch (JsonException)
            {
                return new LauncherSettings();
            }

            _localization.SetLanguage(settings.Language);
            settings.Language = _localization.CurrentLanguage;
            settings.Theme = LauncherThemes.Get(settings.Theme).Id;
            settings.MacOsScalePercent = NormalizeMacOsScalePercent(settings.MacOsScalePercent);
            settings.CompletionSound = CompletionSounds.Normalize(settings.CompletionSound);
            settings.QuestionSound = CompletionSounds.Normalize(settings.QuestionSound);
            settings.ToastDurationSeconds = Math.Clamp(settings.ToastDurationSeconds, 1, 10);
            settings.CodeBadgerChatScale = NormalizeCodeBadgerChatScale(settings.CodeBadgerChatScale);
            settings.CodeBadgerPasteCollapseThreshold = NormalizeCodeBadgerPasteCollapseThreshold(settings.CodeBadgerPasteCollapseThreshold);
            NormalizeLogSettings(settings);
            settings.GitOverviewStartSize = NormalizeGitOverviewStartSize(settings.GitOverviewStartSize);
            settings.AiConflictModel = string.IsNullOrWhiteSpace(settings.AiConflictModel) ? settings.AiModel : settings.AiConflictModel;
            settings.CodeBadgerTerminalModel = settings.CodeBadgerTerminalModel?.Trim() ?? string.Empty;
            settings.SandboxMaxFileMb = Math.Clamp(settings.SandboxMaxFileMb, 1, 512);
            settings.SandboxMaxTotalMb = Math.Clamp(settings.SandboxMaxTotalMb, 1, 2048);
            settings.TaskBranchPrefix = NormalizeBranchPrefix(settings.TaskBranchPrefix);
            settings.CodeBadgerAgentPreferences ??= CodeBadgerRoles.CreateAgentDefaults();
            CodeBadgerRoles.NormalizeAgentPreferences(settings.CodeBadgerAgentPreferences);
            foreach (var repository in settings.Repositories)
            {
                repository.AgentTool = AgentTools.Normalize(repository.AgentTool);
                repository.CodeBadgerOrchestrationMode = CodeBadgerOrchestrationModes.Normalize(repository.CodeBadgerOrchestrationMode);
                repository.TicketSystem = TicketSystems.Normalize(repository.TicketSystem);
                repository.TicketMcpServerName ??= string.Empty;
                repository.TicketProjectKey ??= string.Empty;
                ClearLegacyTicketMcpServer(repository);
                repository.TaskStoryTemplate ??= RepositoryEntry.DefaultTaskStoryTemplate;
                repository.AiCustomInstruction ??= string.Empty;
                repository.HiddenStashCommitHashes ??= [];
            }
#if MACCATALYST && DEBUG
            // Temporary local-development fallback: ad-hoc Mac Catalyst builds do not
            // have the provisioning profile required for Keychain access groups.
            settings.AiApiKey = debugAiApiKey ?? string.Empty;
#else
            settings.AiApiKey = await SecureStorage.Default.GetAsync(AiApiKeyStorageKey) ?? string.Empty;
#endif
#if MACCATALYST && DEBUG
            // Temporary local-development fallback: ad-hoc Mac Catalyst builds do not
            // have the provisioning profile required for Keychain access groups.
            settings.SlackBotToken = debugSlackBotToken ?? string.Empty;
            settings.SlackAppToken = debugSlackAppToken ?? string.Empty;
            settings.DiscordBotToken = debugDiscordBotToken ?? string.Empty;
#else
            settings.SlackBotToken = await SecureStorage.Default.GetAsync(SlackBotTokenStorageKey) ?? string.Empty;
            settings.SlackAppToken = await SecureStorage.Default.GetAsync(SlackAppTokenStorageKey) ?? string.Empty;
            settings.DiscordBotToken = await SecureStorage.Default.GetAsync(DiscordBotTokenStorageKey) ?? string.Empty;
#endif
            settings.SlackAllowedUserId ??= string.Empty;
            settings.DiscordChannelId ??= string.Empty;
            settings.DiscordGuildId ??= string.Empty;
            settings.DiscordAllowedUserId ??= string.Empty;
            return settings;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        await _settingsLock.WaitAsync();
        try
        {
            settings.Language = LocalizationService.NormalizeLanguage(settings.Language);
            settings.Theme = LauncherThemes.Get(settings.Theme).Id;
            settings.MacOsScalePercent = NormalizeMacOsScalePercent(settings.MacOsScalePercent);
            settings.CompletionSound = CompletionSounds.Normalize(settings.CompletionSound);
            settings.QuestionSound = CompletionSounds.Normalize(settings.QuestionSound);
            settings.ToastDurationSeconds = Math.Clamp(settings.ToastDurationSeconds, 1, 10);
            settings.CodeBadgerChatScale = NormalizeCodeBadgerChatScale(settings.CodeBadgerChatScale);
            settings.CodeBadgerPasteCollapseThreshold = NormalizeCodeBadgerPasteCollapseThreshold(settings.CodeBadgerPasteCollapseThreshold);
            NormalizeLogSettings(settings);
            settings.GitOverviewStartSize = NormalizeGitOverviewStartSize(settings.GitOverviewStartSize);
            settings.AiConflictModel = string.IsNullOrWhiteSpace(settings.AiConflictModel) ? settings.AiModel : settings.AiConflictModel;
            settings.CodeBadgerTerminalModel = settings.CodeBadgerTerminalModel?.Trim() ?? string.Empty;
            settings.SandboxMaxFileMb = Math.Clamp(settings.SandboxMaxFileMb, 1, 512);
            settings.SandboxMaxTotalMb = Math.Clamp(settings.SandboxMaxTotalMb, 1, 2048);
            settings.TaskBranchPrefix = NormalizeBranchPrefix(settings.TaskBranchPrefix);
            foreach (var repository in settings.Repositories)
            {
                repository.AgentTool = AgentTools.Normalize(repository.AgentTool);
                repository.CodeBadgerOrchestrationMode = CodeBadgerOrchestrationModes.Normalize(repository.CodeBadgerOrchestrationMode);
                repository.TicketSystem = TicketSystems.Normalize(repository.TicketSystem);
                repository.TicketMcpServerName = repository.TicketMcpServerName?.Trim() ?? string.Empty;
                repository.TicketProjectKey = repository.TicketProjectKey?.Trim() ?? string.Empty;
                ClearLegacyTicketMcpServer(repository);
                repository.TaskStoryTemplate ??= RepositoryEntry.DefaultTaskStoryTemplate;
                repository.AiCustomInstruction = repository.AiCustomInstruction?.Trim() ?? string.Empty;
            }
            settings.CodeBadgerAgentPreferences ??= CodeBadgerRoles.CreateAgentDefaults();
            CodeBadgerRoles.NormalizeAgentPreferences(settings.CodeBadgerAgentPreferences);
            settings.NotificationChannel = settings.NotificationChannel is "Slack" or "Discord"
                ? settings.NotificationChannel : "Slack";
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var settingsJson = JsonSerializer.SerializeToNode(settings, JsonOptions) as JsonObject
                ?? throw new InvalidOperationException("Could not serialize launcher settings.");
#if MACCATALYST && DEBUG
            // Insecure by design and limited to local Debug builds. Replace this with
            // Keychain storage once Developer ID signing and provisioning are available.
            settingsJson[nameof(LauncherSettings.AiApiKey)] = settings.AiApiKey;
            // Match the AI key fallback above: ad-hoc Debug builds lack the Keychain
            // access-group provisioning used by SecureStorage.
            settingsJson[nameof(LauncherSettings.SlackBotToken)] = settings.SlackBotToken;
            settingsJson[nameof(LauncherSettings.SlackAppToken)] = settings.SlackAppToken;
            settingsJson[nameof(LauncherSettings.DiscordBotToken)] = settings.DiscordBotToken;
#endif
            await using (var stream = File.Create(_settingsPath))
                await JsonSerializer.SerializeAsync(stream, settingsJson, JsonOptions);

#if !MACCATALYST || !DEBUG
            if (string.IsNullOrWhiteSpace(settings.AiApiKey))
                SecureStorage.Default.Remove(AiApiKeyStorageKey);
            else
                await SecureStorage.Default.SetAsync(AiApiKeyStorageKey, settings.AiApiKey);
#endif

#if !MACCATALYST || !DEBUG
            if (string.IsNullOrWhiteSpace(settings.SlackBotToken))
                SecureStorage.Default.Remove(SlackBotTokenStorageKey);
            else
                await SecureStorage.Default.SetAsync(SlackBotTokenStorageKey, settings.SlackBotToken);

            if (string.IsNullOrWhiteSpace(settings.SlackAppToken))
                SecureStorage.Default.Remove(SlackAppTokenStorageKey);
            else
                await SecureStorage.Default.SetAsync(SlackAppTokenStorageKey, settings.SlackAppToken);

            if (string.IsNullOrWhiteSpace(settings.DiscordBotToken))
                SecureStorage.Default.Remove(DiscordBotTokenStorageKey);
            else
                await SecureStorage.Default.SetAsync(DiscordBotTokenStorageKey, settings.DiscordBotToken);
#endif

            _localization.SetLanguage(settings.Language);
        }
        finally
        {
            _settingsLock.Release();
        }

        SettingsSaved?.Invoke();
    }

    private static double NormalizeCodeBadgerChatScale(double scale) =>
        Math.Round(Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.7, 1.5), 1);

    private static int NormalizeCodeBadgerPasteCollapseThreshold(int threshold) =>
        Math.Clamp(threshold, 0, 100_000);

    private static int NormalizeMacOsScalePercent(int scalePercent) =>
        Math.Clamp(scalePercent, 100, 160);

    private static void MigrateLegacyTicketSettings(JsonNode? root)
    {
        if (root?["Repositories"] is not JsonArray repositories)
            return;

        foreach (var repository in repositories.OfType<JsonObject>())
        {
            var legacyTicketSystem = repository["TicketSystem"]?.GetValue<string>();
            if (!repository.ContainsKey("TicketRequired") && legacyTicketSystem is not null)
            {
                repository["TicketRequired"] = !string.Equals(legacyTicketSystem, "None", StringComparison.OrdinalIgnoreCase);
            }

            repository.Remove("JiraMcpHosting");
            repository.Remove("JiraMcpUrl");
        }
    }

    private static void MigrateLegacyAiCommitInstruction(JsonNode? root)
    {
        if (root is not JsonObject settings || settings["AiCustomInstruction"] is not JsonValue legacyInstruction ||
            !legacyInstruction.TryGetValue<string>(out var instruction))
            return;

        if (settings["Repositories"] is JsonArray repositories)
        {
            foreach (var repository in repositories.OfType<JsonObject>())
            {
                if (!repository.ContainsKey("AiCustomInstruction"))
                    repository["AiCustomInstruction"] = instruction;
            }
        }

        settings.Remove("AiCustomInstruction");
    }

    private static void MigrateCodeBadgerModelPreferences(JsonNode? root)
    {
        if (root is not JsonObject settings)
            return;

        if (string.IsNullOrWhiteSpace(settings["CodeBadgerTerminalModel"]?.GetValue<string>()))
        {
            var agentPreferences = (settings["CodeBadgerAgentPreferences"] as JsonArray)?.OfType<JsonObject>().ToArray();
            var agentTerminalModel = agentPreferences?
                .FirstOrDefault(preference => string.Equals(preference["AgentTool"]?.GetValue<string>(), AgentTools.OpenCode, StringComparison.Ordinal))?
                ["TerminalModel"]?.GetValue<string>();
            agentTerminalModel = string.IsNullOrWhiteSpace(agentTerminalModel)
                ? agentPreferences?.Select(preference => preference["TerminalModel"]?.GetValue<string>())
                    .FirstOrDefault(model => !string.IsNullOrWhiteSpace(model))
                : agentTerminalModel;
            settings["CodeBadgerTerminalModel"] = agentTerminalModel ??
                settings["CodeBadgerTerminalModel"]?.DeepClone() ?? JsonValue.Create(string.Empty);
        }

        if (settings["CodeBadgerAgentPreferences"] is JsonArray)
            return;

        var rolePreferences = settings["CodeBadgerRolePreferences"] as JsonArray;
        JsonArray? ModelsForRole(string role) => rolePreferences?.OfType<JsonObject>()
            .FirstOrDefault(preference => string.Equals(preference["Role"]?.GetValue<string>(), role, StringComparison.Ordinal))?
            ["PreferredModels"] as JsonArray;

        var roleArray = rolePreferences is null ? CodeBadgerRoles.CreateDefaults() :
            rolePreferences.Deserialize<List<CodeBadgerRolePreference>>() ?? CodeBadgerRoles.CreateDefaults();
        CodeBadgerRoles.Normalize(roleArray);
        var openCode = new JsonObject
        {
            ["AgentTool"] = AgentTools.OpenCode,
            ["SingleAgentPreferredModels"] = CloneArray(settings["CodeBadgerSingleAgentPreferredModels"] as JsonArray ?? ModelsForRole(CodeBadgerRoles.Worker)),
            ["PlanPreferredModels"] = CloneArray(settings["CodeBadgerPlanPreferredModels"] as JsonArray ?? ModelsForRole(CodeBadgerRoles.Planner)),
            ["BuildPreferredModels"] = CloneArray(settings["CodeBadgerBuildPreferredModels"] as JsonArray ?? ModelsForRole(CodeBadgerRoles.Worker)),
            ["RolePreferences"] = System.Text.Json.JsonSerializer.SerializeToNode(roleArray, JsonOptions)
        };
        settings["CodeBadgerAgentPreferences"] = new JsonArray
        {
            openCode,
            CreateAgentPreference(AgentTools.ClaudeCode),
            CreateAgentPreference(AgentTools.Hermes),
            CreateAgentPreference(AgentTools.Codex)
        };
    }

    private static JsonObject CreateAgentPreference(string agent) => new()
    {
        ["AgentTool"] = agent,
        ["SingleAgentPreferredModels"] = new JsonArray(),
        ["PlanPreferredModels"] = new JsonArray(),
        ["BuildPreferredModels"] = new JsonArray(),
        ["RolePreferences"] = System.Text.Json.JsonSerializer.SerializeToNode(CodeBadgerRoles.CreateDefaults(), JsonOptions)
    };

    private static JsonArray CloneArray(JsonArray? source)
    {
        var copy = new JsonArray();
        if (source is not null)
            foreach (var item in source)
                copy.Add(item?.DeepClone());
        return copy;
    }

    private static void ClearLegacyTicketMcpServer(RepositoryEntry repository)
    {
        if (repository.TicketMcpServerName is "codebadger-jira" or "codebadger-github")
            repository.TicketMcpServerName = string.Empty;
    }

    private static string NormalizeBranchPrefix(string? value)
    {
        var prefix = (value ?? string.Empty).Trim().Trim('/');
        return string.IsNullOrWhiteSpace(prefix) ? "feature" : prefix;
    }

    private static void NormalizeLogSettings(LauncherSettings settings)
    {
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 3650);
        settings.LoggingSettingsVersion = 1;
        settings.GeneralLogLevel = LauncherSettings.NormalizeLogLevel(settings.GeneralLogLevel, "Warning");
        settings.GitLogLevel = LauncherSettings.NormalizeLogLevel(settings.GitLogLevel, "Warning");
        settings.NotificationsLogLevel = LauncherSettings.NormalizeLogLevel(settings.NotificationsLogLevel, "Warning");
        settings.AiLogLevel = LauncherSettings.NormalizeLogLevel(settings.AiLogLevel, "Warning");
    }

    private static void MigrateLoggingDefaults(JsonNode? root)
    {
        if (root is not JsonObject settings || settings["LoggingSettingsVersion"] is not null)
            return;

        // Earlier builds shipped with Information as the default. Normalize the
        // legacy defaults once; subsequent user changes are retained on saves.
        foreach (var key in new[] { "GeneralLogLevel", "GitLogLevel", "NotificationsLogLevel", "AiLogLevel" })
        {
            if (settings[key] is not JsonValue storedLevel ||
                !storedLevel.TryGetValue<string>(out var level) ||
                string.Equals(level, "Information", StringComparison.OrdinalIgnoreCase))
                settings[key] = "Warning";
        }
        settings["LoggingSettingsVersion"] = 1;
    }

    private static string NormalizeGitOverviewStartSize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "small" => "Small",
        "medium" => "Medium",
        _ => "Large"
    };
}
