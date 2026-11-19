using System.Text.Json;
using System.Text.Json.Nodes;
using CodeBadger.Models;

namespace CodeBadger.Services;

/// <summary>
/// Reads host OpenCode config without rewriting it. Only explicitly supported
/// API-key chat providers are adapted. Credentials remain host-only; OAuth,
/// custom runtime code and file substitutions are not silently copied.
/// </summary>
public sealed class SandboxOpenCodeProviderService(AgentConfigService configs)
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public async Task<string> GetDefaultModelAsync(string repositoryPath)
    {
        var config = await LoadMergedAsync(repositoryPath);
        return config["model"]?.GetValue<string>() ?? string.Empty;
    }

    public async Task<HostProvider> ResolveAsync(string repositoryPath, string modelReference)
    {
        var config = await LoadMergedAsync(repositoryPath);
        string reference = string.IsNullOrWhiteSpace(modelReference) ? config["model"]?.GetValue<string>() ?? "" : modelReference.Trim();
        int separator = reference.IndexOf('/');
        if (separator < 1 || separator == reference.Length - 1 || reference.Contains('#') || reference.Any(char.IsControl))
            throw new InvalidOperationException("Select an existing OpenCode provider/model without a variant.");
        string providerId = reference[..separator];
        string modelId = reference[(separator + 1)..];
        if ((config["providers"] as JsonObject)?[providerId] is not JsonObject provider)
            throw new InvalidOperationException("This provider is not explicitly configured in OpenCode. Built-in OAuth accounts are not supported by this bridge yet.");
        string package = provider["package"]?.GetValue<string>() ?? string.Empty;
        if (package is not ("aisdk:@ai-sdk/openai-compatible" or "@opencode/ai/providers/openai-compatible" or "@opencode/ai/providers/openai/chat"))
            throw new InvalidOperationException("The configured provider runtime is not supported by the sandbox bridge yet.");
        if (provider["headers"] is not null || provider["body"] is not null)
            throw new InvalidOperationException("Custom provider headers and body overrides require a dedicated sandbox adapter.");
        var settings = provider["settings"] as JsonObject
            ?? throw new InvalidOperationException("OpenCode provider settings are missing.");
        string baseUrl = ResolveHostValue(settings["baseURL"]?.GetValue<string>() ?? "");
        string key = ResolveHostValue(settings["apiKey"]?.GetValue<string>() ?? "");
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("The provider requires a valid HTTPS base URL without query or embedded credentials.");
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("The configured provider API key is unavailable. Credentials have not been copied to the sandbox.");
        if ((provider["models"] as JsonObject)?[modelId] is not JsonObject model)
            throw new InvalidOperationException("Select a model defined in the existing OpenCode provider configuration.");
        if (model["headers"] is not null || model["body"] is not null || model["package"] is not null || model["settings"] is not null)
            throw new InvalidOperationException("Model-specific runtime overrides require a dedicated sandbox adapter.");
        string remoteModel = model["modelID"]?.GetValue<string>() ?? modelId;
        if (string.IsNullOrWhiteSpace(remoteModel) || remoteModel.Any(char.IsControl))
            throw new InvalidOperationException("Invalid model ID in OpenCode configuration.");
        return new HostProvider(reference, endpoint, remoteModel, key);
    }

    private async Task<JsonObject> LoadMergedAsync(string repositoryPath)
    {
        if (!Path.IsPathFullyQualified(repositoryPath)) throw new ArgumentException("An absolute repository path is required.");
        var merged = Parse(await configs.LoadAsync(AgentTools.OpenCode));
        var directories = new Stack<string>();
        var current = new DirectoryInfo(repositoryPath);
        while (current is not null) { directories.Push(current.FullName); current = current.Parent; }
        var ordered = directories.ToArray();
        // Mirrors V2 discovery: direct configs first, .opencode configs after.
        foreach (bool nested in new[] { false, true })
        foreach (string directory in ordered)
        {
            string root = nested ? Path.Combine(directory, ".opencode") : directory;
            string jsonc = Path.Combine(root, "opencode.jsonc");
            string json = Path.Combine(root, "opencode.json");
            string? path = File.Exists(jsonc) ? jsonc : File.Exists(json) ? json : null;
            if (path is not null) Merge(merged, Parse(await File.ReadAllTextAsync(path)));
        }
        return merged;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: JsonOptions) as JsonObject
        ?? throw new InvalidDataException("Invalid OpenCode configuration object.");

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonObject next && target[pair.Key] is JsonObject existing) Merge(existing, next);
            else target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static string ResolveHostValue(string value)
    {
        if (value.StartsWith("{env:", StringComparison.Ordinal) && value.EndsWith('}') && value.Count(character => character == '{') == 1)
            return Environment.GetEnvironmentVariable(value[5..^1]) ?? string.Empty;
        if (value.Contains("{env:", StringComparison.Ordinal) || value.Contains("{file:", StringComparison.Ordinal))
            throw new InvalidOperationException("This credential substitution is not supported by the sandbox adapter yet.");
        return value;
    }

    // Not a record: autogenerated ToString must never expose credentials.
    public sealed class HostProvider
    {
        public string Reference { get; }
        public Uri Endpoint { get; }
        public string Model { get; }
        internal string ApiKey { get; }
        internal HostProvider(string reference, Uri endpoint, string model, string apiKey)
        { Reference = reference; Endpoint = endpoint; Model = model; ApiKey = apiKey; }
        public override string ToString() => Reference;
    }
}
