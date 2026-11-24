using System.Collections.Concurrent;
using System.Text.Json;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public static class ApplicationLogCategories
{
    public const string General = "General";
    public const string Git = "Git";
    public const string Notifications = "Notifications";
    public const string Ai = "AI";

    public static readonly string[] All = [General, Git, Notifications, Ai];

    public static string FromLoggerCategory(string? category) => category switch
    {
        "CodeBadger.Git" or "Git" => Git,
        "CodeBadger.Notifications" or "Notifications" => Notifications,
        "CodeBadger.AI" or "AI" => Ai,
        _ when category?.EndsWith("GitBranchService", StringComparison.Ordinal) == true => Git,
        _ when category?.EndsWith("GitHostService", StringComparison.Ordinal) == true => Git,
        _ when category?.EndsWith("SlackNotificationService", StringComparison.Ordinal) == true => Notifications,
        _ when category?.EndsWith("OpenCodeActivityService", StringComparison.Ordinal) == true => Ai,
        _ when category?.EndsWith("WindowsTerminalSessionService", StringComparison.Ordinal) == true => Ai,
        _ when category?.EndsWith("AiCommitMessageService", StringComparison.Ordinal) == true => Ai,
        _ when category?.EndsWith("WindowsSandboxSessionService", StringComparison.Ordinal) == true => Ai,
        _ when category?.EndsWith("SandboxNetworkBroker", StringComparison.Ordinal) == true => Ai,
        _ when category?.EndsWith("SandboxProviderBridge", StringComparison.Ordinal) == true => Ai,
        _ => General
    };
}

public sealed record ApplicationLogEntry(
    DateTimeOffset Timestamp,
    string Category,
    LogLevel Level,
    string Message,
    string? Exception,
    string? Repository = null);

/// <summary>Persists categorized application logs and publishes new entries to live viewers.</summary>
public sealed class ApplicationLogService : ILoggerProvider
{
    private const int MaximumVisibleEntries = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Replacement)[] SecretPatterns =
    [
        (new(@"(?i)(https?://)[^/@\s]+:[^/@\s]+@", System.Text.RegularExpressions.RegexOptions.Compiled), "$1[redacted]@"),
        (new("(?i)([\\\"']?(?:authorization|proxy-authorization)[\\\"']?\\s*[:=]\\s*[\\\"']?)(?:(?:bearer|basic)\\s+)?[^\\s,;}\\\"']+", System.Text.RegularExpressions.RegexOptions.Compiled), "$1[redacted]"),
        (new("(?i)(\\b(?:bearer|basic)\\s+)[A-Za-z0-9._~+/-]+=*", System.Text.RegularExpressions.RegexOptions.Compiled), "$1[redacted]"),
        (new("(?i)([\\\"']?(?:x-api-key|api[_-]?key|key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|secret|token|credential|auth|api[_-]?secret)[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s,;}&\\\"']+", System.Text.RegularExpressions.RegexOptions.Compiled), "$1[redacted]"),
        (new("(?i)([\\\"']?(?:authorization|proxy-authorization)[\\\"']?\\s*[:=]\\s*[\\\"']?)(?:\\\\\\\"|[^\\s,;}])+", System.Text.RegularExpressions.RegexOptions.Compiled), "$1[redacted]"),
        (new(@"(?i)([?&](?:key|token|ticket|access_token|api_key|apikey|client_secret|password|secret|sig|signature|auth)=)[^&#\s]+", System.Text.RegularExpressions.RegexOptions.Compiled), "$1[redacted]"),
        (new(@"\b(?:xox[baprs]-[A-Za-z0-9-]{10,}|xapp-[A-Za-z0-9-]{10,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|xai-[A-Za-z0-9_-]{20,})\b", System.Text.RegularExpressions.RegexOptions.Compiled), "[redacted]"),
        (new(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", System.Text.RegularExpressions.RegexOptions.Compiled), "[redacted]")
    ];
    private readonly ConcurrentQueue<ApplicationLogEntry> _entries = new();
    private readonly ConcurrentDictionary<string, CategoryLogger> _loggers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly LauncherSettingsService _settingsService;
    private static readonly AsyncLocal<LogScope?> CurrentScope = new();
    private LauncherSettings? _cachedSettings;
    private string? _loadedPath;

    public ApplicationLogService(LauncherSettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsSaved += InvalidateSettings;
    }

    public event Action<ApplicationLogEntry>? EntryWritten;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new CategoryLogger(this, category));

    public IReadOnlyList<ApplicationLogEntry> GetEntries(string category, int maximum = 1000)
    {
        EnsureLoaded();
        return _entries.Reverse()
            .Where(entry => string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(maximum, 1, MaximumVisibleEntries))
            .Reverse()
            .ToArray();
    }

    public async Task WriteAsync(string loggerCategory, LogLevel level, string message, Exception? exception = null, string? repository = null)
    {
        var category = ApplicationLogCategories.FromLoggerCategory(loggerCategory);
        LauncherSettings settings;
        try { settings = await GetSettingsAsync(); }
        catch { return; }
        var minimum = settings.GetLogLevel(category);
        if (minimum == LogLevel.None || level < minimum)
            return;

        EnsureLoaded(settings.LogRetentionDays);
        message = RedactSecrets(message, settings);
        var entry = new ApplicationLogEntry(DateTimeOffset.Now, category, level, message,
            exception is null ? null : RedactSecrets(exception.ToString(), settings), repository);
        _entries.Enqueue(entry);
        while (_entries.Count > MaximumVisibleEntries)
            _entries.TryDequeue(out _);

        try
        {
            await _writeGate.WaitAsync();
            try
            {
                var path = GetLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (IOException)
        {
            // Logging failures must not interrupt normal application work.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging failures must not interrupt normal application work.
        }

        try { EntryWritten?.Invoke(entry); }
        catch { /* A log viewer must never interrupt the application. */ }
    }

    public void Clear()
    {
        _entries.Clear();
        try
        {
            var directory = GetLogDirectory();
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "codebadger-*.jsonl"))
                    File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void PruneExpired(int retentionDays)
    {
        var directory = GetLogDirectory();
        if (!Directory.Exists(directory))
            return;

        var cutoff = DateTime.Now.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        foreach (var file in Directory.EnumerateFiles(directory, "codebadger-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() { }

    private static string RedactSecrets(string text)
    {
        foreach (var (pattern, replacement) in SecretPatterns)
            text = pattern.Replace(text, replacement);
        return text;
    }

    private static string RedactSecrets(string text, LauncherSettings settings)
    {
        text = RedactSecrets(text);
        foreach (var secret in new[] { settings.AiApiKey, settings.SlackBotToken, settings.SlackAppToken, settings.DiscordBotToken }
                     .Where(secret => !string.IsNullOrWhiteSpace(secret))
                     .Distinct(StringComparer.Ordinal))
        {
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal);
            text = text.Replace(System.Text.Json.JsonSerializer.Serialize(secret).Trim('"'), "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    private async Task<LauncherSettings> GetSettingsAsync()
    {
        if (_cachedSettings is { } cached)
            return cached;

        await _settingsGate.WaitAsync();
        try
        {
            return _cachedSettings ??= await _settingsService.LoadAsync();
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private void InvalidateSettings() => _cachedSettings = null;

    private static string? GetRepository<TState>(TState? state)
    {
        for (var scope = CurrentScope.Value; scope is not null; scope = scope.Parent)
        {
            if (GetRepositoryFromState(scope.State) is { } scopedRepository)
                return scopedRepository;
        }
        return GetRepositoryFromState(state);
    }

    private static string? GetRepositoryFromState<TState>(TState? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
            return null;
        foreach (var (key, value) in properties)
        {
            if (value is not string text || string.IsNullOrWhiteSpace(text)) continue;
            if (key.Equals("Repository", StringComparison.OrdinalIgnoreCase) || key.Equals("RepositoryName", StringComparison.OrdinalIgnoreCase))
                return text;
            if (key.Equals("RepositoryPath", StringComparison.OrdinalIgnoreCase) || key.Equals("WorkingDirectory", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(Path.TrimEndingDirectorySeparator(text));
        }
        return null;
    }

    private sealed record LogScope(object State, LogScope? Parent);

    private sealed class ScopeHandle(LogScope scope) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, scope))
                CurrentScope.Value = scope.Parent;
        }
    }

    private void EnsureLoaded(int retentionDays = 30)
    {
        var path = GetLogPath();
        if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        _loadedPath = path;
        PruneExpired(retentionDays);
        try
        {
            var files = Directory.Exists(GetLogDirectory())
                ? Directory.EnumerateFiles(GetLogDirectory(), "codebadger-*.jsonl").OrderBy(file => file, StringComparer.Ordinal).ToArray()
                : [];
            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file).TakeLast(MaximumVisibleEntries))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ApplicationLogEntry>(line, JsonOptions);
                        if (entry is not null)
                            _entries.Enqueue(entry with
                            {
                                Message = RedactSecrets(entry.Message, _cachedSettings ?? new LauncherSettings()),
                                Exception = entry.Exception is null ? null : RedactSecrets(entry.Exception, _cachedSettings ?? new LauncherSettings())
                            });
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetLogDirectory() => Path.Combine(FileSystem.AppDataDirectory, "logs");

    private static string GetLogPath() => Path.Combine(GetLogDirectory(), $"codebadger-{DateTime.Now:yyyy-MM-dd}.jsonl");

    private sealed class CategoryLogger(ApplicationLogService owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var scope = new LogScope(state, CurrentScope.Value);
            CurrentScope.Value = scope;
            return new ScopeHandle(scope);
        }
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _ = owner.WriteAsync(categoryName, logLevel, formatter(state, exception), exception,
                    GetRepository(state));
        }
    }
}
