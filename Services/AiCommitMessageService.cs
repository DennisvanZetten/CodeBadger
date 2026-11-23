using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed class AiCommitMessageService
{
    private const int ConflictResolutionMaxOutputTokens = 32768;
    private readonly LocalizationService _localization;
    private readonly ILogger<AiCommitMessageService> _logger;

    public AiCommitMessageService(LocalizationService localization, ILogger<AiCommitMessageService> logger)
    {
        _localization = localization;
        _logger = logger;
    }

    public async Task<AiCommitMessageResult> GenerateAsync(LauncherSettings settings, string diff, string? branchName = null,
        string? customInstruction = null, string? repositoryPath = null)
    {
        using var repositoryScope = CreateRepositoryScope(repositoryPath);
        return await GenerateAsync(settings, diff, branchName, settings.AiModel, commitMessage: true, customInstruction);
    }

    public async Task<AiCommitMessageResult> ResolveConflictAsync(LauncherSettings settings, string conflictContent, string? repositoryPath = null)
    {
        using var repositoryScope = CreateRepositoryScope(repositoryPath);
        if (string.IsNullOrWhiteSpace(conflictContent))
            return new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("The conflict has no content to resolve."));

        var prompt = new StringBuilder("Resolve the Git conflict(s) in this file and return the complete resolved file as plain text. Keep all text outside conflict markers exactly as it is. Within each conflict, prefer preserving compatible edits from both sides; if edits directly disagree and cannot both be applied, use the incoming/right side's version. Remove all conflict markers. Do not wrap the result in JSON, Markdown, code fences, or an explanation.");
        prompt.Append("\n\nConflict file content:\n").Append(conflictContent);
        var response = await GenerateAsync(settings, prompt.ToString(), null, settings.AiConflictModel, commitMessage: false, null);
        return response.Success
            ? response with { Title = response.Title.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') }
            : response;
    }

    private async Task<AiCommitMessageResult> GenerateAsync(LauncherSettings settings, string promptText, string? branchName,
        string model, bool commitMessage, string? customInstruction)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            return new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("Please stage at least one change before generating a commit message."));

        var provider = settings.AiProvider;
        var endpoint = ResolveCompletionEndpoint(settings, commitMessage ? null : model);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("Enter a valid provider URL."));

        try
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _logger.LogInformation("Starting AI {Operation} with provider {Provider} and model {Model}. Input excerpt: {InputExcerpt}",
                commitMessage ? "commit-message generation" : "conflict resolution", provider, model, CreateLogExcerpt(promptText));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var request = commitMessage
                ? CreateRequest(provider, uri, settings, LimitInput(promptText, settings.AiMaxInputTokens), branchName, customInstruction)
                : CreateConflictRequest(provider, uri, settings, promptText, model);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AI provider {Provider} returned HTTP {StatusCode} for {Operation} after {ElapsedMilliseconds} ms.",
                    provider, (int)response.StatusCode, commitMessage ? "commit-message generation" : "conflict resolution",
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                return new AiCommitMessageResult(false, string.Empty, string.Empty,
                    _localization.T("Provider returned status {0} for {1}: {2}", (int)response.StatusCode, uri.AbsolutePath, ExtractError(responseBody)));
            }

            if (commitMessage)
            {
                var (title, description) = ExtractMessage(provider, responseBody);
                var result = string.IsNullOrWhiteSpace(title)
                    ? new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("The provider returned no commit title."))
                    : new AiCommitMessageResult(true, title.Trim().Trim('`'), description.Trim().Trim('`'), string.Empty);
                if (result.Success)
                    _logger.LogInformation("AI commit-message result excerpt: {ResultExcerpt}",
                        CreateLogExcerpt(title + (string.IsNullOrWhiteSpace(description) ? string.Empty : "\n" + description)));
                LogAiResult(result.Success, provider, model, commitMessage, startedAt, title.Length + description.Length);
                return result;
            }

            var resolvedContent = ExtractConflictContent(provider, responseBody);
            var conflictResult = string.IsNullOrWhiteSpace(resolvedContent)
                ? new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("The AI provider returned no resolved content."))
                : new AiCommitMessageResult(true, resolvedContent, string.Empty, string.Empty);
            if (conflictResult.Success)
                _logger.LogInformation("AI conflict-resolution result excerpt: {ResultExcerpt}", CreateLogExcerpt(resolvedContent));
            LogAiResult(conflictResult.Success, provider, model, commitMessage, startedAt, resolvedContent.Length);
            return conflictResult;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("AI provider {Provider} timed out during {Operation}.", provider,
                commitMessage ? "commit-message generation" : "conflict resolution");
            return new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("The AI provider connection timed out."));
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("AI provider {Provider} connection failed during {Operation}: {ErrorMessage}", provider,
                commitMessage ? "commit-message generation" : "conflict resolution", exception.Message);
            return new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("Connection to the AI provider failed: {0}", exception.Message));
        }
        catch (JsonException exception)
        {
            _logger.LogWarning("AI provider {Provider} returned an invalid response during {Operation}: {ErrorMessage}", provider,
                commitMessage ? "commit-message generation" : "conflict resolution", exception.Message);
            return new AiCommitMessageResult(false, string.Empty, string.Empty, _localization.T("Invalid AI provider response: {0}", exception.Message));
        }
    }

    private IDisposable? CreateRepositoryScope(string? repositoryPath) =>
        string.IsNullOrWhiteSpace(repositoryPath)
            ? null
            : _logger.BeginScope(new Dictionary<string, object?> { ["RepositoryPath"] = repositoryPath });

    private void LogAiResult(bool success, string provider, string model, bool commitMessage, long startedAt, int outputCharacters)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (success)
            _logger.LogInformation("AI {Operation} with provider {Provider} and model {Model} completed in {ElapsedMilliseconds} ms; generated {OutputCharacters} characters.",
                commitMessage ? "commit-message generation" : "conflict resolution", provider, model, elapsed, outputCharacters);
        else
            _logger.LogWarning("AI {Operation} with provider {Provider} and model {Model} returned no usable result after {ElapsedMilliseconds} ms.",
                commitMessage ? "commit-message generation" : "conflict resolution", provider, model, elapsed);
    }

    private static string CreateLogExcerpt(string text)
    {
        const int maximumLength = 4000;
        var excerpt = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        excerpt = System.Text.RegularExpressions.Regex.Replace(excerpt,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return excerpt.Length <= maximumLength ? excerpt : excerpt[..maximumLength] + "… [truncated]";
    }

    public async Task<AiCommitMessageResult> TestConnectionAsync(LauncherSettings settings)
    {
        var result = await GetAvailableModelsAsync(settings);
        return result.Success
            ? new AiCommitMessageResult(true, _localization.T("Connection successful. {0} models found.", result.Models.Count), string.Empty, string.Empty)
            : new AiCommitMessageResult(false, string.Empty, string.Empty, result.Error);
    }

    private static HttpRequestMessage CreateRequest(string provider, Uri endpoint, LauncherSettings settings, string diff,
        string? branchName, string? customInstruction)
    {
        var prompt = new StringBuilder("Generate a Git commit title and description for the staged changes. Respond only as JSON with string properties \"title\" and \"description\". The title must be concise, imperative mood, and at most 72 characters. The description may be empty.");
        prompt.Append("\n\nCurrent branch name: ").Append(string.IsNullOrWhiteSpace(branchName) ? "(unavailable)" : branchName.Trim());
        if (!string.IsNullOrWhiteSpace(customInstruction))
            prompt.Append("\n\nAdditional user instructions:\n").Append(customInstruction.Trim());
        prompt.Append("\n\nStaged diff:\n").Append(diff);
        var userPrompt = prompt.ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.AiApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Content = JsonContent(new
            {
                model = settings.AiModel,
                max_tokens = 256,
                messages = new[] { new { role = "user", content = userPrompt } }
            });
        }
        else if (provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(endpoint);
            builder.Query = string.IsNullOrWhiteSpace(builder.Query)
                ? $"key={Uri.EscapeDataString(settings.AiApiKey)}"
                : $"{builder.Query.TrimStart('?')}&key={Uri.EscapeDataString(settings.AiApiKey)}";
            request.RequestUri = builder.Uri;
            request.Content = JsonContent(new
            {
                contents = new[] { new { parts = new[] { new { text = userPrompt } } } },
                generationConfig = new { maxOutputTokens = 256 }
            });
        }
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = JsonContent(new
            {
                model = settings.AiModel,
                stream = false,
                messages = new[] { new { role = "user", content = userPrompt } }
            });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
            {
                if (provider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation("api-key", settings.AiApiKey);
                else
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey);
            }
            request.Content = JsonContent(new
            {
                model = settings.AiModel,
                max_tokens = 256,
                messages = new[] { new { role = "user", content = userPrompt } }
            });
        }

        return request;
    }

    private static HttpRequestMessage CreateConflictRequest(string provider, Uri endpoint, LauncherSettings settings, string prompt, string model)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.AiApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Content = JsonContent(new { model, max_tokens = ConflictResolutionMaxOutputTokens, messages = new[] { new { role = "user", content = prompt } } });
        }
        else if (provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(endpoint);
            builder.Query = string.IsNullOrWhiteSpace(builder.Query)
                ? $"key={Uri.EscapeDataString(settings.AiApiKey)}"
                : $"{builder.Query.TrimStart('?')}&key={Uri.EscapeDataString(settings.AiApiKey)}";
            request.RequestUri = builder.Uri;
            request.Content = JsonContent(new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { maxOutputTokens = ConflictResolutionMaxOutputTokens } });
        }
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = JsonContent(new { model, stream = false, messages = new[] { new { role = "user", content = prompt } }, options = new { num_predict = ConflictResolutionMaxOutputTokens } });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
            {
                if (provider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation("api-key", settings.AiApiKey);
                else
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey);
            }
            request.Content = JsonContent(new { model, max_tokens = ConflictResolutionMaxOutputTokens, messages = new[] { new { role = "user", content = prompt } } });
        }
        return request;
    }

    public async Task<AiModelListResult> GetAvailableModelsAsync(LauncherSettings settings)
    {
        var provider = settings.AiProvider;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation("Loading available models from AI provider {Provider}.", provider);
        var endpoint = ResolveModelsEndpoint(settings);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new AiModelListResult(false, [], _localization.T("Enter a valid provider URL."));

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddAuthHeaders(request, provider, settings.AiApiKey);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AI provider {Provider} returned HTTP {StatusCode} while listing models.", provider, (int)response.StatusCode);
                return new AiModelListResult(false, [],
                    _localization.T("Provider returned status {0}: {1}", (int)response.StatusCode, ExtractError(responseBody)));
            }

            var models = ParseModelList(provider, responseBody);
            _logger.LogInformation("Loaded {ModelCount} models from AI provider {Provider} in {ElapsedMilliseconds} ms.",
                models.Count, provider, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return models.Count == 0
                ? new AiModelListResult(false, [], _localization.T("The connection succeeded, but the provider returned no models."))
                : new AiModelListResult(true, models, string.Empty);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("AI provider {Provider} model listing timed out.", provider);
            return new AiModelListResult(false, [], _localization.T("The AI provider connection timed out."));
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("AI provider {Provider} model listing failed ({ExceptionType}).", provider, exception.GetType().Name);
            return new AiModelListResult(false, [], _localization.T("Connection to the AI provider failed: {0}", exception.Message));
        }
        catch (JsonException exception)
        {
            _logger.LogWarning("AI provider {Provider} model listing returned invalid JSON ({ExceptionType}).", provider, exception.GetType().Name);
            return new AiModelListResult(false, [], _localization.T("Invalid models response: {0}", exception.Message));
        }
    }

    private static string ResolveCompletionEndpoint(LauncherSettings settings, string? modelOverride = null)
    {
        if (settings.AiProvider.Equals("Custom URL", StringComparison.OrdinalIgnoreCase))
            return NormalizeOpenAiCompatibleUrl(settings.AiCustomUrl, "chat/completions");

        if (settings.AiProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return "https://api.anthropic.com/v1/messages";
        if (settings.AiProvider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
            return $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(modelOverride ?? settings.AiModel)}:generateContent";
        if (settings.AiProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            return "http://localhost:11434/api/chat";
        if (settings.AiProvider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.AiCustomUrl) || string.IsNullOrWhiteSpace(modelOverride) ||
                !Uri.TryCreate(settings.AiCustomUrl, UriKind.Absolute, out var azureUri))
                return settings.AiCustomUrl;
            var segments = azureUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var deploymentIndex = Array.FindIndex(segments, segment => segment.Equals("deployments", StringComparison.OrdinalIgnoreCase));
            if (deploymentIndex < 0 || deploymentIndex + 1 >= segments.Length)
                return settings.AiCustomUrl;
            segments[deploymentIndex + 1] = Uri.EscapeDataString(modelOverride);
            var builder = new UriBuilder(azureUri) { Path = "/" + string.Join('/', segments) };
            return builder.Uri.ToString();
        }
        return "https://api.openai.com/v1/chat/completions";
    }

    private static string ResolveModelsEndpoint(LauncherSettings settings)
    {
        var provider = settings.AiProvider;
        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return "https://api.anthropic.com/v1/models";
        if (provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
            return $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(settings.AiApiKey)}";
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            return "http://localhost:11434/api/tags";
        if (provider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(settings.AiCustomUrl, UriKind.Absolute, out var azureUri))
                return settings.AiCustomUrl;
            var apiVersion = System.Web.HttpUtility.ParseQueryString(azureUri.Query).Get("api-version");
            return $"{azureUri.GetLeftPart(UriPartial.Authority)}/openai/models?api-version={Uri.EscapeDataString(apiVersion ?? "2024-10-21")}";
        }

        if (provider.Equals("Custom URL", StringComparison.OrdinalIgnoreCase))
            return NormalizeOpenAiCompatibleUrl(settings.AiCustomUrl, "models");

        return "https://api.openai.com/v1/models";
    }

    private static string NormalizeOpenAiCompatibleUrl(string url, string endpointName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var path = uri.AbsolutePath.TrimEnd('/');
        var completionSuffix = "/chat/completions";
        if (path.EndsWith(completionSuffix, StringComparison.OrdinalIgnoreCase))
        {
            if (endpointName.Equals("chat/completions", StringComparison.OrdinalIgnoreCase))
                return uri.ToString();
            path = path[..^completionSuffix.Length];
        }
        else if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/models".Length];
        }

        var builder = new UriBuilder(uri)
        {
            Path = $"{path}/{endpointName}",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static void AddAuthHeaders(HttpRequestMessage request, string provider, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;
        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase) &&
                 !provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (provider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation("api-key", apiKey);
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private static IReadOnlyList<string> ParseModelList(string provider, string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        IEnumerable<string?> names;
        if (provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
            names = root.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("name").GetString());
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            names = root.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("name").GetString());
        else
            names = root.GetProperty("data").EnumerateArray().Select(model => model.GetProperty("id").GetString());

        return names.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase) && name!.StartsWith("models/", StringComparison.Ordinal)
                ? name[7..]
                : name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private string LimitInput(string diff, int maxInputTokens)
    {
        var tokenLimit = Math.Clamp(maxInputTokens, 256, 200000);
        var charLimit = tokenLimit * 4;
        if (diff.Length <= charLimit)
            return diff;
        return diff[..charLimit] + "\n\n[" + _localization.T("Diff shortened because of the configured input token limit.") + "]";
    }

    private static (string Title, string Description) ExtractMessage(string provider, string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        string text;
        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            text = root.GetProperty("content").EnumerateArray().FirstOrDefault().GetProperty("text").GetString() ?? string.Empty;
        else if (provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
            text = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            text = root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        else
            text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;

        try
        {
            using var message = JsonDocument.Parse(text);
            var title = message.RootElement.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? string.Empty : string.Empty;
            var description = message.RootElement.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty;
            return (title, description);
        }
        catch (JsonException)
        {
            var lines = text.Split('\n', 2, StringSplitOptions.TrimEntries);
            return (lines[0].Trim().Trim('`'), lines.Length > 1 ? lines[1].Trim() : string.Empty);
        }
    }

    private static string ExtractConflictContent(string provider, string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var text = provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            ? root.GetProperty("content").EnumerateArray().FirstOrDefault().GetProperty("text").GetString() ?? string.Empty
            : provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase)
                ? root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty
            : provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                    ? root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty
                    : root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Conflict prompt requests plain text; some models still serialize the requested shape.
        }
        return StripMarkdownFence(text);
    }

    private static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0 || closingFence <= firstLineEnd)
            return trimmed;
        return trimmed[(firstLineEnd + 1)..closingFence].Trim('\r', '\n');
    }

    private static string ExtractError(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.String ? error.GetString() ?? response : error.ToString();
        }
        catch (JsonException)
        {
            // The provider may return a plain text error.
        }
        return response.Length > 500 ? response[..500] : response;
    }
}

public sealed record AiCommitMessageResult(bool Success, string Title, string Description, string Error);
public sealed record AiModelListResult(bool Success, IReadOnlyList<string> Models, string Error);
