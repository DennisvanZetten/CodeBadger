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
            try
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                var root = JsonNode.Parse(json);
                MigrateLegacyTicketSettings(root);
                settings = root?.Deserialize<LauncherSettings>(JsonOptions) ?? new LauncherSettings();
            }
            catch (JsonException)
            {
                return new LauncherSettings();
            }

            _localization.SetLanguage(settings.Language);
            settings.Language = _localization.CurrentLanguage;
            settings.Theme = LauncherThemes.Get(settings.Theme).Id;
            settings.CompletionSound = CompletionSounds.Normalize(settings.CompletionSound);
            settings.QuestionSound = CompletionSounds.Normalize(settings.QuestionSound);
            settings.ToastDurationSeconds = Math.Clamp(settings.ToastDurationSeconds, 1, 10);
            settings.SandboxMaxFileMb = Math.Clamp(settings.SandboxMaxFileMb, 1, 512);
            settings.SandboxMaxTotalMb = Math.Clamp(settings.SandboxMaxTotalMb, 1, 2048);
            settings.TaskBranchPrefix = NormalizeBranchPrefix(settings.TaskBranchPrefix);
            foreach (var repository in settings.Repositories)
            {
                repository.AgentTool = AgentTools.Normalize(repository.AgentTool);
                repository.TicketSystem = TicketSystems.Normalize(repository.TicketSystem);
                repository.TicketMcpServerName ??= string.Empty;
            }
            settings.AiApiKey = await SecureStorage.Default.GetAsync(AiApiKeyStorageKey) ?? string.Empty;
            settings.SlackBotToken = await SecureStorage.Default.GetAsync(SlackBotTokenStorageKey) ?? string.Empty;
            settings.SlackAppToken = await SecureStorage.Default.GetAsync(SlackAppTokenStorageKey) ?? string.Empty;
            settings.SlackAllowedUserId ??= string.Empty;
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
            settings.CompletionSound = CompletionSounds.Normalize(settings.CompletionSound);
            settings.QuestionSound = CompletionSounds.Normalize(settings.QuestionSound);
            settings.ToastDurationSeconds = Math.Clamp(settings.ToastDurationSeconds, 1, 10);
            settings.SandboxMaxFileMb = Math.Clamp(settings.SandboxMaxFileMb, 1, 512);
            settings.SandboxMaxTotalMb = Math.Clamp(settings.SandboxMaxTotalMb, 1, 2048);
            settings.TaskBranchPrefix = NormalizeBranchPrefix(settings.TaskBranchPrefix);
            foreach (var repository in settings.Repositories)
            {
                repository.AgentTool = AgentTools.Normalize(repository.AgentTool);
                repository.TicketSystem = TicketSystems.Normalize(repository.TicketSystem);
                repository.TicketMcpServerName = repository.TicketMcpServerName?.Trim() ?? string.Empty;
            }
            settings.NotificationChannel = "Slack";
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await using (var stream = File.Create(_settingsPath))
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);

            if (string.IsNullOrWhiteSpace(settings.AiApiKey))
                SecureStorage.Default.Remove(AiApiKeyStorageKey);
            else
                await SecureStorage.Default.SetAsync(AiApiKeyStorageKey, settings.AiApiKey);

            if (string.IsNullOrWhiteSpace(settings.SlackBotToken))
                SecureStorage.Default.Remove(SlackBotTokenStorageKey);
            else
                await SecureStorage.Default.SetAsync(SlackBotTokenStorageKey, settings.SlackBotToken);

            if (string.IsNullOrWhiteSpace(settings.SlackAppToken))
                SecureStorage.Default.Remove(SlackAppTokenStorageKey);
            else
                await SecureStorage.Default.SetAsync(SlackAppTokenStorageKey, settings.SlackAppToken);

            _localization.SetLanguage(settings.Language);
        }
        finally
        {
            _settingsLock.Release();
        }

        SettingsSaved?.Invoke();
    }

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

            if (legacyTicketSystem is not null && !repository.ContainsKey("TicketMcpServerName"))
            {
                repository["TicketSystem"] = legacyTicketSystem;
                var legacyServerName = legacyTicketSystem switch
                {
                    "Jira" => "codebadger-jira",
                    "GitHub Issues" => "codebadger-github",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(legacyServerName))
                    repository["TicketMcpServerName"] = legacyServerName;
            }
            repository.Remove("JiraMcpHosting");
            repository.Remove("JiraMcpUrl");
        }
    }

    private static string NormalizeBranchPrefix(string? value)
    {
        var prefix = (value ?? string.Empty).Trim().Trim('/');
        return string.IsNullOrWhiteSpace(prefix) ? "feature" : prefix;
    }
}
