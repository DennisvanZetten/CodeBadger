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
    string? Exception);

/// <summary>Persists categorized application logs and publishes new entries to live viewers.</summary>
public sealed class ApplicationLogService : ILoggerProvider
{
    private const int MaximumVisibleEntries = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly ConcurrentQueue<ApplicationLogEntry> _entries = new();
    private readonly ConcurrentDictionary<string, CategoryLogger> _loggers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly LauncherSettingsService _settingsService;
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

    public async Task WriteAsync(string loggerCategory, LogLevel level, string message, Exception? exception = null)
    {
        var category = ApplicationLogCategories.FromLoggerCategory(loggerCategory);
        LauncherSettings settings;
        try { settings = await GetSettingsAsync(); }
        catch { return; }
        var minimum = settings.GetLogLevel(category);
        if (minimum == LogLevel.None || level < minimum)
            return;

        EnsureLoaded(settings.LogRetentionDays);
        if (!string.Equals(category, ApplicationLogCategories.Git, StringComparison.Ordinal))
        {
            message = System.Text.RegularExpressions.Regex.Replace(message,
                @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
            message = System.Text.RegularExpressions.Regex.Replace(message,
                @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        }
        var entry = new ApplicationLogEntry(DateTimeOffset.Now, category, level, message,
            exception?.ToString());
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
                            _entries.Enqueue(entry);
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
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _ = owner.WriteAsync(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
