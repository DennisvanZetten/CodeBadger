using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

/// <summary>Runs CodeBadger composer turns through the selected agent's documented CLI protocol.</summary>
public sealed class CodeBadgerCliAgentRunner(ILogger<CodeBadgerCliAgentRunner> logger)
{
    public sealed record AgentReply(string SessionId, string Text);
    private static readonly HttpClient CatalogHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private const string HermesCatalogUrl = "https://hermes-agent.nousresearch.com/docs/api/model-catalog.json";

    private static readonly CodeBadgerAgentModel[] ClaudeModelChoices =
    [
        new("default", "Account default", true),
        new("sonnet", "Sonnet"),
        new("opus", "Opus"),
        new("haiku", "Haiku"),
        new("fable", "Fable"),
        new("sonnet[1m]", "Sonnet · extended context"),
        new("opus[1m]", "Opus · extended context"),
        new("opusplan", "Opus Plan · Sonnet Build")
    ];
    private static readonly Dictionary<string, (string? ApiKey, string? BaseUrl)> HermesProviderCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter"] = ("OPENROUTER_API_KEY", "https://openrouter.ai/api/v1"),
        ["anthropic"] = ("ANTHROPIC_API_KEY", "https://api.anthropic.com/v1"),
        ["openai-api"] = ("OPENAI_API_KEY", "https://api.openai.com/v1"),
        ["gemini"] = ("GOOGLE_API_KEY", "https://generativelanguage.googleapis.com/v1beta"),
        ["deepseek"] = ("DEEPSEEK_API_KEY", "https://api.deepseek.com/v1"),
        ["zai"] = ("GLM_API_KEY", "https://api.z.ai/api/paas/v4"),
        ["kimi-coding"] = ("KIMI_API_KEY", "https://api.moonshot.ai/v1"),
        ["minimax"] = ("MINIMAX_API_KEY", "https://api.minimax.io/v1"),
        ["alibaba"] = ("DASHSCOPE_API_KEY", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"),
        ["xiaomi"] = ("XIAOMI_API_KEY", "https://api.xiaomimimo.com/v1"),
        ["tencent-tokenhub"] = ("TOKENHUB_API_KEY", "https://api.tokenhub.tencent.com/v1"),
        ["arcee"] = ("ARCEEAI_API_KEY", "https://api.arcee.ai/api/v1"),
        ["gmi"] = ("GMI_API_KEY", "https://api.gmi-serving.com/v1"),
        ["fireworks"] = ("FIREWORKS_API_KEY", "https://api.fireworks.ai/inference/v1"),
        ["novita"] = ("NOVITA_API_KEY", "https://api.novita.ai/openai/v1"),
        ["nvidia"] = ("NVIDIA_API_KEY", "https://integrate.api.nvidia.com/v1"),
        ["stepfun"] = ("STEPFUN_API_KEY", "https://api.stepfun.com/v1"),
        ["huggingface"] = ("HF_TOKEN", "https://router.huggingface.co/v1"),
        ["ollama-cloud"] = ("OLLAMA_API_KEY", "https://ollama.com/v1"),
        ["nous"] = ("NOUS_API_KEY", "https://inference-api.nousresearch.com/v1")
    };

    public async Task<IReadOnlyList<CodeBadgerAgentModel>> GetAvailableModelsAsync(string agentTool,
        string repositoryPath, CancellationToken cancellationToken)
    {
        var agent = AgentTools.Normalize(agentTool);
        if (agent == AgentTools.ClaudeCode)
            return await GetClaudeModelChoicesAsync(cancellationToken);
        if (agent == AgentTools.Codex)
            return await GetCodexModelChoicesAsync(repositoryPath, cancellationToken);
        if (agent == AgentTools.Hermes)
            return await GetHermesModelChoicesAsync(cancellationToken);
        return [];
    }

    public async Task<CodeBadgerAgentModel?> GetDefaultModelAsync(string agentTool, string repositoryPath,
        CancellationToken cancellationToken)
    {
        var catalog = await GetAvailableModelsAsync(agentTool, repositoryPath, cancellationToken);
        return catalog.FirstOrDefault(model => model.IsDefault) ?? catalog.FirstOrDefault();
    }

    private async Task<IReadOnlyList<CodeBadgerAgentModel>> GetClaudeModelChoicesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, CodeBadgerAgentModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in ClaudeModelChoices)
            result[model.Reference] = model;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settingsPaths = new List<string>
        {
            Path.Combine(home, ".claude", "settings.json"),
            Path.Combine(home, ".claude", "settings.local.json"),
            Path.Combine(home, ".claude.json")
        };
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")))
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!;
            settingsPaths.Insert(0, Path.Combine(configDir, "settings.json"));
            settingsPaths.Insert(1, Path.Combine(configDir, "settings.local.json"));
        }
        foreach (var settingsPath in settingsPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(settingsPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken));
                var availableModels = FindPropertyRecursive(document.RootElement, "availableModels");
                if (availableModels.ValueKind != JsonValueKind.Array) continue;
                var allowlist = availableModels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).ToArray();
                if (allowlist.Length == 0) return [];
                var permitted = result.Values.Where(model => allowlist.Any(allowed => IsClaudeModelAllowed(model.Reference, allowed)))
                    .ToDictionary(model => model.Reference, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in allowlist)
                    if (!string.IsNullOrWhiteSpace(entry) && !permitted.ContainsKey(entry))
                        permitted[entry] = new CodeBadgerAgentModel(entry, entry);
                return permitted.Values.OrderByDescending(item => item.IsDefault)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Could not parse Claude Code's model settings at {SettingsPath}.", settingsPath);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not read Claude Code's model settings at {SettingsPath}.", settingsPath);
            }
        }
        return result.Values.OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsClaudeModelAllowed(string reference, string allowlistEntry)
    {
        if (string.Equals(reference, allowlistEntry, StringComparison.OrdinalIgnoreCase)) return true;
        if (allowlistEntry.Equals("sonnet", StringComparison.OrdinalIgnoreCase))
            return reference.StartsWith("sonnet", StringComparison.OrdinalIgnoreCase);
        if (allowlistEntry.Equals("opus", StringComparison.OrdinalIgnoreCase))
            return reference.StartsWith("opus", StringComparison.OrdinalIgnoreCase);
        if (allowlistEntry.Equals("haiku", StringComparison.OrdinalIgnoreCase))
            return reference.StartsWith("haiku", StringComparison.OrdinalIgnoreCase);
        if (allowlistEntry.Equals("fable", StringComparison.OrdinalIgnoreCase))
            return reference.StartsWith("fable", StringComparison.OrdinalIgnoreCase);
        var prefix = allowlistEntry.EndsWith('*') ? allowlistEntry[..^1] : allowlistEntry;
        return allowlistEntry.EndsWith('*') && reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement FindPropertyRecursive(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var value)) return value;
            foreach (var property in element.EnumerateObject())
            {
                var found = FindPropertyRecursive(property.Value, propertyName);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindPropertyRecursive(item, propertyName);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
            }
        }
        return default;
    }

    private async Task<IReadOnlyList<CodeBadgerAgentModel>> GetCodexModelChoicesAsync(string repositoryPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateAppServerStartInfo(repositoryPath),
            EnableRaisingEvents = true
        };
        if (!process.Start())
            throw new InvalidOperationException("Could not start Codex app-server to load its model catalog.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var writer = process.StandardInput;
            var reader = process.StandardOutput;
            async Task SendAsync(object message)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token);
                await writer.FlushAsync(timeout.Token);
            }
            await SendAsync(new { method = "initialize", id = 0,
                @params = new { clientInfo = new { name = "codebadger", title = "CodeBadger", version = "1.0" } } });
            var initialized = await ReadAppServerResponseAsync(reader, 0, timeout.Token);
            initialized.Dispose();
            await SendAsync(new { method = "initialized", @params = new { } });
            await SendAsync(new { method = "model/list", id = 1, @params = new { limit = 200, includeHidden = false } });
            using var response = await ReadAppServerResponseAsync(reader, 1, timeout.Token);
            if (!response.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];
            return data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new CodeBadgerAgentModel(GetString(item, "id"),
                    string.IsNullOrWhiteSpace(GetString(item, "displayName")) ? GetString(item, "id") : GetString(item, "displayName"),
                    item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True))
                .Where(item => !string.IsNullOrWhiteSpace(item.Reference))
                .OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex model discovery timed out.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
        }
    }

    private async Task<IReadOnlyList<CodeBadgerAgentModel>> GetHermesModelChoicesAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } hermesHome
            ? hermesHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes"), "config.yaml");
        if (!File.Exists(path)) return [];
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        try
        {
            using var reader = new StringReader(await File.ReadAllTextAsync(path, cancellationToken));
            yaml.Load(reader);
            if (yaml.Documents.FirstOrDefault()?.RootNode is not YamlDotNet.RepresentationModel.YamlMappingNode root ||
                !root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("model"), out var modelNode) ||
                modelNode is not YamlDotNet.RepresentationModel.YamlMappingNode model)
                return [];
            var provider = ReadYamlScalar(model, "provider");
            var defaultModel = ReadYamlScalar(model, "default");
            var result = new Dictionary<string, CodeBadgerAgentModel>(StringComparer.OrdinalIgnoreCase);
            AddHermesModel(result, defaultModel, isDefault: true);
            AddHermesCachedModels(result, provider);
            AddYamlModelEntries(result, model.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("models"), out var mainModels)
                ? mainModels : new YamlDotNet.RepresentationModel.YamlSequenceNode());
            var catalogConfig = model;
            if (root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("providers"), out var providersNode) &&
                providersNode is YamlDotNet.RepresentationModel.YamlMappingNode providers &&
                providers.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(provider), out var providerNode) &&
                providerNode is YamlDotNet.RepresentationModel.YamlMappingNode providerConfig)
            {
                catalogConfig = providerConfig;
                AddHermesModel(result, ReadYamlScalar(providerConfig, "default_model"));
                if (providerConfig.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("models"), out var providerModels))
                    AddYamlModelEntries(result, providerModels);
            }

            if (model.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("model_catalog"), out var catalogNode) &&
                catalogNode is YamlDotNet.RepresentationModel.YamlMappingNode modelCatalogConfig &&
                modelCatalogConfig.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("providers"), out var catalogProvidersNode) &&
                catalogProvidersNode is YamlDotNet.RepresentationModel.YamlMappingNode catalogProviders &&
                catalogProviders.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(provider), out var providerCatalogNode))
                AddYamlModelEntries(result, providerCatalogNode);

            if (string.IsNullOrWhiteSpace(provider)) return [];
            await AddHermesProviderModelsAsync(result, provider, catalogConfig, cancellationToken);
            return result.Values.OrderByDescending(item => item.IsDefault).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            logger.LogWarning(exception, "Could not load Hermes model configuration.");
            return [];
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not read Hermes model configuration.");
            return [];
        }
    }

    private static void AddHermesModel(Dictionary<string, CodeBadgerAgentModel> models, string? reference, bool isDefault = false)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        reference = reference.Trim();
        models[reference] = new CodeBadgerAgentModel(reference, reference, isDefault);
    }

    private static void AddYamlModelEntries(Dictionary<string, CodeBadgerAgentModel> models,
        YamlDotNet.RepresentationModel.YamlNode modelsNode)
    {
        if (modelsNode is YamlDotNet.RepresentationModel.YamlSequenceNode sequence)
        {
            foreach (var node in sequence.Children.OfType<YamlDotNet.RepresentationModel.YamlScalarNode>())
                AddHermesModel(models, node.Value);
            return;
        }
        if (modelsNode is not YamlDotNet.RepresentationModel.YamlMappingNode mapping) return;
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlDotNet.RepresentationModel.YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value)) continue;
            var label = pair.Value is YamlDotNet.RepresentationModel.YamlMappingNode definition
                ? ReadYamlScalar(definition, "name") : string.Empty;
            models[key.Value] = new CodeBadgerAgentModel(key.Value,
                string.IsNullOrWhiteSpace(label) ? key.Value : label);
        }
    }

    private async Task AddHermesProviderModelsAsync(Dictionary<string, CodeBadgerAgentModel> models,
        string provider, YamlDotNet.RepresentationModel.YamlMappingNode modelConfig, CancellationToken cancellationToken)
    {
        if (provider is "openrouter" or "nous")
            await AddHermesCuratedModelsAsync(models, provider, cancellationToken);

        var shouldDiscover = !provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("nous", StringComparison.OrdinalIgnoreCase);
        if (RootProviderDiscoveryDisabled(modelConfig)) shouldDiscover = false;
        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ReadYamlScalar(modelConfig, "base_url")))
            shouldDiscover = false;
        if (!shouldDiscover) return;
        var (url, apiKey) = await GetHermesProviderEndpointAsync(provider, modelConfig, cancellationToken);
        if (string.IsNullOrWhiteSpace(url)) return;
        var endpoint = url.TrimEnd('/');
        if (!endpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) endpoint += "/models";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            if (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Add("x-goog-api-key", apiKey);
            using var response = await CatalogHttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var data = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement :
                document.RootElement.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array
                    ? wrapped : default;
            if (data.ValueKind != JsonValueKind.Array) return;
            var declaredModels = new HashSet<string>(models.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (id.Length == 0 || !declaredModels.Contains(id)) continue;
                var defaultModel = models[id].IsDefault;
                var name = GetString(item, "name");
                models[id] = new CodeBadgerAgentModel(id, name.Length > 0 ? name : id, defaultModel);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not query Hermes provider {Provider} model endpoint {Endpoint}.", provider, endpoint);
        }
    }

    private async Task AddHermesCuratedModelsAsync(Dictionary<string, CodeBadgerAgentModel> models,
        string provider, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await CatalogHttpClient.GetAsync(HermesCatalogUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("providers", out var providers) ||
                !providers.TryGetProperty(provider, out var providerCatalog) ||
                !providerCatalog.TryGetProperty("models", out var modelArray) || modelArray.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in modelArray.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (id.Length == 0) continue;
                var description = GetString(item, "description");
                var isDefault = item.TryGetProperty("default", out var defaultValue) && defaultValue.ValueKind == JsonValueKind.True;
                var priorDefault = models.TryGetValue(id, out var existing) && existing.IsDefault;
                models[id] = new CodeBadgerAgentModel(id,
                    string.IsNullOrWhiteSpace(description) ? id : $"{id} · {description}", isDefault || priorDefault);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read the Hermes curated model catalog for {Provider}.", provider);
        }
    }

    private static bool RootProviderDiscoveryDisabled(YamlDotNet.RepresentationModel.YamlMappingNode modelConfig)
    {
        // The provider block is consulted when present; `discover_models: false` means use only the
        // configured model declarations and avoid making a network catalog request.
        return modelConfig.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("discover_models"), out var value) &&
               value is YamlDotNet.RepresentationModel.YamlScalarNode scalar &&
               string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddHermesCachedModels(Dictionary<string, CodeBadgerAgentModel> models, string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        var home = Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } hermesHome
            ? hermesHome : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");
        foreach (var path in new[]
                 {
                     Path.Combine(home, "provider_models_cache.json"),
                     Path.Combine(home, "cache", "model_catalog.json")
                 })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty(provider, out var entry) &&
                    !(root.TryGetProperty("providers", out var providers) && providers.TryGetProperty(provider, out entry)))
                    continue;
                if (entry.TryGetProperty("models", out var modelsValue) && modelsValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in modelsValue.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            AddHermesModel(models, item.GetString());
                        else if (item.ValueKind == JsonValueKind.Object)
                            AddHermesModel(models, GetString(item, "id"), item.TryGetProperty("default", out var marked) && marked.ValueKind == JsonValueKind.True);
                    }
                }
            }
            catch (Exception) { /* A corrupt/stale cache must not prevent the local config fallback. */ }
        }
    }

    private static async Task<(string Url, string? ApiKey)> GetHermesProviderEndpointAsync(string provider,
        YamlDotNet.RepresentationModel.YamlMappingNode modelConfig, CancellationToken cancellationToken)
    {
        var modelBaseUrl = ReadYamlScalar(modelConfig, "base_url");
        if (string.IsNullOrWhiteSpace(modelBaseUrl)) modelBaseUrl = ReadYamlScalar(modelConfig, "api");
        if (!string.IsNullOrWhiteSpace(modelBaseUrl))
        {
            var key = ReadYamlScalar(modelConfig, "api_key");
            if (key.StartsWith("${", StringComparison.Ordinal) && key.EndsWith('}'))
            {
                var keyEnvironmentVariable = key[2..^1];
                key = Environment.GetEnvironmentVariable(keyEnvironmentVariable) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    var keyHome = Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } configuredHome
                        ? configuredHome : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");
                    var keyEnvPath = Path.Combine(keyHome, ".env");
                    if (File.Exists(keyEnvPath))
                        foreach (var line in await File.ReadAllLinesAsync(keyEnvPath, cancellationToken))
                        {
                            var parts = line.Split('=', 2);
                            if (parts.Length == 2 && parts[0].Trim().Equals(keyEnvironmentVariable, StringComparison.OrdinalIgnoreCase))
                            {
                                key = parts[1].Trim().Trim('"', '\'');
                                break;
                            }
                        }
                }
            }
            return (modelBaseUrl, string.IsNullOrWhiteSpace(key) ? null : key);
        }
        var home = Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } hermesHome
            ? hermesHome : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");
        try
        {
            var configPath = Path.Combine(home, "config.yaml");
            var stream = new YamlDotNet.RepresentationModel.YamlStream();
            using (var reader = new StringReader(await File.ReadAllTextAsync(configPath, cancellationToken)))
                stream.Load(reader);
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlDotNet.RepresentationModel.YamlMappingNode root)
                return (string.Empty, null);
            if (root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("providers"), out var providersNode) &&
                providersNode is YamlDotNet.RepresentationModel.YamlMappingNode providers &&
                providers.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(provider), out var providerNode) &&
                providerNode is YamlDotNet.RepresentationModel.YamlMappingNode providerConfig)
            {
                var baseUrl = ReadYamlScalar(providerConfig, "api");
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = ReadYamlScalar(providerConfig, "base_url");
                var key = ReadYamlScalar(providerConfig, "api_key");
                var keyEnv = ReadYamlScalar(providerConfig, "key_env");
                if (key.StartsWith("${", StringComparison.Ordinal) && key.EndsWith('}'))
                {
                    keyEnv = key[2..^1];
                    key = string.Empty;
                }
                if (!string.IsNullOrWhiteSpace(keyEnv))
                {
                    key = Environment.GetEnvironmentVariable(keyEnv) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        var envFile = Path.Combine(home, ".env");
                        if (File.Exists(envFile))
                            foreach (var line in await File.ReadAllLinesAsync(envFile, cancellationToken))
                            {
                                var parts = line.Split('=', 2);
                                if (parts.Length == 2 && parts[0].Trim().Equals(keyEnv, StringComparison.OrdinalIgnoreCase))
                                {
                                    key = parts[1].Trim().Trim('"', '\'');
                                    break;
                                }
                            }
                    }
                }
                return (baseUrl, string.IsNullOrWhiteSpace(key) ? null : key);
            }

            var envPath = Path.Combine(home, ".env");
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(envPath))
                foreach (var line in await File.ReadAllLinesAsync(envPath, cancellationToken))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2) env[parts[0].Trim()] = parts[1].Trim().Trim('"', '\'');
                }
        var providerUpper = provider.Replace('-', '_').ToUpperInvariant();
        var providerInfo = HermesProviderCatalog.GetValueOrDefault(provider);
        var tokenKeys = providerInfo.ApiKey is null
            ? new[] { providerUpper + "_API_KEY" }
            : new[] { providerInfo.ApiKey };
        if (providerInfo.ApiKey is not null)
            tokenKeys = [.. tokenKeys, providerUpper + "_API_KEY"];
        if (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase))
            tokenKeys = [.. tokenKeys, "GEMINI_API_KEY"];
        if (provider.Equals("nous", StringComparison.OrdinalIgnoreCase))
            tokenKeys = [.. tokenKeys, "NOUS_PORTAL_API_KEY"];
            var apiKey = tokenKeys.Select(name => env.GetValueOrDefault(name) ?? Environment.GetEnvironmentVariable(name))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var apiUrl = providerInfo.BaseUrl ?? string.Empty;
            return (apiUrl, apiKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return (string.Empty, null); }
    }

    private static string ReadYamlScalar(YamlDotNet.RepresentationModel.YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(key), out var value) &&
        value is YamlDotNet.RepresentationModel.YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;

    private static async Task<JsonDocument> ReadAppServerResponseAsync(StreamReader reader, int expectedId,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                document.Dispose();
                throw new InvalidOperationException("Codex app-server model catalog error: " + error.ToString());
            }
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expectedId)
                return document;
            document.Dispose();
        }
        throw new InvalidOperationException("Codex app-server closed before returning its model catalog.");
    }

    private static ProcessStartInfo CreateAppServerStartInfo(string repositoryPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = AgentTools.GetCommand(AgentTools.Codex), WorkingDirectory = repositoryPath,
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("app-server");
        ConfigureWindowsScriptCommand(start);
        return start;
    }

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
            if (agent is not (AgentTools.Codex or AgentTools.Hermes))
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
                    role, prompt, images ?? [], attachments, model, onText, cancellationToken);
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
            if (string.IsNullOrWhiteSpace(resultSessionId) && agent == AgentTools.Hermes)
                resultSessionId = GetHermesSessionId(error);
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
                start.ArgumentList.Add("--quiet");
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
                foreach (var image in images)
                {
                    start.ArgumentList.Add("--image");
                    start.ArgumentList.Add(image);
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
        string? sessionId, string role, string prompt, IReadOnlyList<string> images, IReadOnlyList<string> imageAttachments, string? model,
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
        foreach (var image in imageAttachments)
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
        return instruction + prompt + (images.Count == 0 ? string.Empty :
            "\n\nAttached image(s) for inspection:\n" + string.Join("\n", images));
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

    private static string? GetHermesSessionId(string standardError)
    {
        foreach (var line in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedLine = line.Trim();
            const string sessionIdPrefix = "session_id:";
            if (trimmedLine.StartsWith(sessionIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = trimmedLine[sessionIdPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(sessionId))
                    return sessionId;
            }
        }

        return null;
    }

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
