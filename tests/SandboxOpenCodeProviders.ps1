# Synthetic configuration tests. Never reads real provider credentials.
$ErrorActionPreference = 'Stop'
$support = @'
namespace CodeBadger.Models { public static class AgentTools { public const string OpenCode = "OpenCode"; } }
namespace CodeBadger.Services
{
    public sealed class AgentConfigService
    {
        public string Json { get; set; } = "{}";
        public Task<string> LoadAsync(string tool) => Task.FromResult(Json);
    }
}
public static class ProviderConfigurationTests
{
    public static async Task Run(string repository)
    {
        var source = new CodeBadger.Services.AgentConfigService
        {
            Json = "{\"model\":\"bonzai/test-model\",\"providers\":{\"bonzai\":{\"package\":\"aisdk:@ai-sdk/openai-compatible\",\"settings\":{\"baseURL\":\"https://example.com/ai/v1\",\"apiKey\":\"synthetic-key-only\"},\"models\":{\"test-model\":{\"modelID\":\"remote-test-model\"}}}}}"
        };
        var service = new CodeBadger.Services.SandboxOpenCodeProviderService(source);
        if (await service.GetDefaultModelAsync(repository) != "bonzai/test-model") throw new Exception("Default model was lost.");
        var choices = await service.GetModelsAsync(repository);
        if (choices.Count != 1 || choices[0].Reference != "bonzai/test-model") throw new Exception("Model choices were not loaded.");
        var provider = await service.ResolveAsync(repository, "");
        if (provider.Endpoint.AbsoluteUri != "https://example.com/ai/v1/chat/completions" || provider.Model != "remote-test-model")
            throw new Exception("Host provider mapping differs.");
        if (provider.ToString().Contains("synthetic-key-only")) throw new Exception("Provider diagnostic exposes credentials.");
        foreach (string reference in new[] { "bonzai/unknown", "bonzai/test-model#variant", "unknown/test" })
        {
            bool denied = false;
            try { await service.ResolveAsync(repository, reference); } catch (InvalidOperationException) { denied = true; }
            if (!denied) throw new Exception("Unsupported provider/model accepted.");
        }
        File.WriteAllText(Path.Combine(repository, "opencode.json"), "{\"providers\":{\"bonzai\":{\"settings\":{\"baseURL\":\"https://override.example.com/v1\"}}}}");
        provider = await service.ResolveAsync(repository, "bonzai/test-model");
        if (provider.Endpoint.Host != "override.example.com") throw new Exception("Project override was not applied.");
        source.Json = source.Json.Replace("aisdk:@ai-sdk/openai-compatible", "custom-executable-runtime");
        bool rejected = false;
        try { await service.ResolveAsync(repository, "bonzai/test-model"); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Unsupported runtime silently accepted.");
        Console.WriteLine("Existing OpenCode provider configuration tests passed.");
    }
}
'@
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxOpenCodeProviderService.cs')
$source = $source.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$usings = "#nullable enable annotations`nusing System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Threading.Tasks;"
Add-Type -TypeDefinition ($usings + $source + $support)
$repository = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerProviderConfigTest-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $repository
try { $null = [ProviderConfigurationTests]::Run($repository).GetAwaiter().GetResult() }
finally {
    $config = Join-Path $repository 'opencode.json'
    if (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config }
    Remove-Item -LiteralPath $repository
}
