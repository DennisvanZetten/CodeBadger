using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RepoLauncher.Models;

namespace RepoLauncher.Services;

public sealed class AiCommitMessageService
{
    public async Task<AiCommitMessageResult> GenerateAsync(LauncherSettings settings, string diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
            return new AiCommitMessageResult(false, string.Empty, "Stage minstens één wijziging voordat je een commitbericht genereert.");

        var provider = settings.AiProvider;
        var endpoint = ResolveCompletionEndpoint(settings);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new AiCommitMessageResult(false, string.Empty, "Vul een geldige provider-URL in.");

        var boundedDiff = LimitInput(diff, settings.AiMaxInputTokens);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var request = CreateRequest(provider, uri, settings, boundedDiff);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return new AiCommitMessageResult(false, string.Empty, $"Provider gaf {(int)response.StatusCode} voor {uri.AbsolutePath}: {ExtractError(responseBody)}");

            var message = ExtractMessage(provider, responseBody);
            return string.IsNullOrWhiteSpace(message)
                ? new AiCommitMessageResult(false, string.Empty, "De provider gaf geen commitbericht terug.")
                : new AiCommitMessageResult(true, message.Trim().Trim('`'), string.Empty);
        }
        catch (TaskCanceledException)
        {
            return new AiCommitMessageResult(false, string.Empty, "Verbinding met de AI-provider duurde te lang.");
        }
        catch (HttpRequestException exception)
        {
            return new AiCommitMessageResult(false, string.Empty, $"Verbinding met de AI-provider mislukt: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return new AiCommitMessageResult(false, string.Empty, $"Ongeldige response van AI-provider: {exception.Message}");
        }
    }

    public async Task<AiCommitMessageResult> TestConnectionAsync(LauncherSettings settings)
    {
        var result = await GetAvailableModelsAsync(settings);
        return result.Success
            ? new AiCommitMessageResult(true, $"Verbinding werkt. {result.Models.Count} modellen gevonden.", string.Empty)
            : new AiCommitMessageResult(false, string.Empty, result.Error);
    }

    private static HttpRequestMessage CreateRequest(string provider, Uri endpoint, LauncherSettings settings, string diff)
    {
        var prompt = new StringBuilder("Write one concise Git commit message for the staged changes. Use imperative mood, no quotes, no markdown, at most 72 characters.");
        if (!string.IsNullOrWhiteSpace(settings.AiCustomInstruction))
            prompt.Append("\n\nAdditional user instructions:\n").Append(settings.AiCustomInstruction.Trim());
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

    public async Task<AiModelListResult> GetAvailableModelsAsync(LauncherSettings settings)
    {
        var provider = settings.AiProvider;
        var endpoint = ResolveModelsEndpoint(settings);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new AiModelListResult(false, [], "Vul een geldige provider-URL in.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddAuthHeaders(request, provider, settings.AiApiKey);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return new AiModelListResult(false, [], $"Provider gaf {(int)response.StatusCode}: {ExtractError(responseBody)}");

            var models = ParseModelList(provider, responseBody);
            return models.Count == 0
                ? new AiModelListResult(false, [], "De verbinding is gelukt, maar de provider heeft geen modellen teruggegeven.")
                : new AiModelListResult(true, models, string.Empty);
        }
        catch (TaskCanceledException)
        {
            return new AiModelListResult(false, [], "Verbinding met de AI-provider duurde te lang.");
        }
        catch (HttpRequestException exception)
        {
            return new AiModelListResult(false, [], $"Verbinding met de AI-provider mislukt: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return new AiModelListResult(false, [], $"Ongeldige modellen-response: {exception.Message}");
        }
    }

    private static string ResolveCompletionEndpoint(LauncherSettings settings)
    {
        if (settings.AiProvider.Equals("Custom URL", StringComparison.OrdinalIgnoreCase))
            return NormalizeOpenAiCompatibleUrl(settings.AiCustomUrl, "chat/completions");

        if (settings.AiProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return "https://api.anthropic.com/v1/messages";
        if (settings.AiProvider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
            return $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(settings.AiModel)}:generateContent";
        if (settings.AiProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            return "http://localhost:11434/api/chat";
        if (settings.AiProvider.Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase))
            return settings.AiCustomUrl;
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

    private static string LimitInput(string diff, int maxInputTokens)
    {
        var tokenLimit = Math.Clamp(maxInputTokens, 256, 200000);
        var charLimit = tokenLimit * 4;
        if (diff.Length <= charLimit)
            return diff;
        return diff[..charLimit] + "\n\n[Diff ingekort vanwege de ingestelde input-tokenlimiet.]";
    }

    private static string ExtractMessage(string provider, string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return root.GetProperty("content").EnumerateArray().FirstOrDefault().GetProperty("text").GetString() ?? string.Empty;
        if (provider.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase))
            return root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            return root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        return root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
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

public sealed record AiCommitMessageResult(bool Success, string Message, string Error);
public sealed record AiModelListResult(bool Success, IReadOnlyList<string> Models, string Error);
