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
        var endpoint = ResolveEndpoint(settings);
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
                return new AiCommitMessageResult(false, string.Empty, $"Provider gaf {(int)response.StatusCode}: {ExtractError(responseBody)}");

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
        var result = await GenerateAsync(settings, "diff --git a/example.txt b/example.txt\n+Add a sample line\n");
        return result.Success
            ? new AiCommitMessageResult(true, "Verbinding en model werken.", string.Empty)
            : result;
    }

    private static HttpRequestMessage CreateRequest(string provider, Uri endpoint, LauncherSettings settings, string diff)
    {
        var prompt = $"Write one concise Git commit message for the staged changes. Use imperative mood, no quotes, no markdown, at most 72 characters.\n\nStaged diff:\n{diff}";
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.AiApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Content = JsonContent(new
            {
                model = settings.AiModel,
                max_tokens = 256,
                messages = new[] { new { role = "user", content = prompt } }
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
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { maxOutputTokens = 256 }
            });
        }
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = JsonContent(new
            {
                model = settings.AiModel,
                stream = false,
                messages = new[] { new { role = "user", content = prompt } }
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
                messages = new[] { new { role = "user", content = prompt } }
            });
        }

        return request;
    }

    private static string ResolveEndpoint(LauncherSettings settings)
    {
        if (settings.AiProvider.Equals("Custom URL", StringComparison.OrdinalIgnoreCase))
            return settings.AiCustomUrl;

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
