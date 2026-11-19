using System.Text.Json;

namespace CodeBadger.Services;

public static class SandboxOpenCodeConfiguration
{
    public static void Stage(string profileDirectory, string pipeName, string model, string pluginSource, string transportSource)
    {
        string plugin = Path.Combine(profileDirectory, "provider-plugin");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "server.mjs"), pluginSource);
        File.WriteAllText(Path.Combine(plugin, "provider-transport.mjs"), transportSource);
        File.WriteAllText(Path.Combine(plugin, "package.json"),
            "{\"name\":\"codebadger-sandbox-provider\",\"type\":\"module\",\"exports\":{\".\":\"./server.mjs\"}}");
        var config = new Dictionary<string, object>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = "codebadger/" + model,
            ["snapshots"] = false,
            ["plugins"] = new[] { new { package = plugin, options = new { pipeName } } },
            ["providers"] = new Dictionary<string, object>
            {
                ["codebadger"] = new
                {
                    name = "CodeBadger sandbox", package = "@opencode/ai/providers/openai-compatible",
                    settings = new { baseURL = "https://codebadger.invalid/v1", apiKey = "not-a-provider-key" },
                    models = new Dictionary<string, object>
                    {
                        [model] = new { name = model, limit = new { context = 32000, output = 4096 } }
                    }
                }
            },
            ["permissions"] = new[] { new { action = "shell", resource = "*", effect = "ask" } },
            ["policies"] = new[]
            {
                new { action = "provider.use", resource = "*", effect = "deny" },
                new { action = "provider.use", resource = "codebadger", effect = "allow" },
                new { action = "mcp.use", resource = "*", effect = "deny" }
            }
        };
        // No endpoint credential is ever staged into the sandbox.
        File.WriteAllText(Path.Combine(profileDirectory, "opencode.json"), JsonSerializer.Serialize(config));
    }
}
