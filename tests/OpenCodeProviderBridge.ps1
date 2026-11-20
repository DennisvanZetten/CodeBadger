# Real OpenCode/AppContainer integration against a fake pipe provider only.
# No provider credentials or external AI requests are used.
param([Parameter(Mandatory = $true)][string]$OpenCodeExecutable, [switch]$Direct, [switch]$ProbeWithoutFsctlMitigation,
    [ValidateSet('registryRead', 'lpacAppExperience', 'lpacServicesManagement')][string]$ProbeRuntimeCapability = 'registryRead')
$ErrorActionPreference = 'Stop'
$test = @'
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
public static class OpenCodeProviderTest
{
    public static async Task Run(string executable, string assets, bool direct)
    {
        CodeBadger.Services.SandboxBrokerPipe pipe = null;
        Task serving = null;
        string home = null;
        int requests = 0;
        CodeBadger.Services.WindowsSandboxTerminal terminal = null;
        CodeBadger.Services.WindowsSandboxProcess process = null;
        string repository = Path.Combine(Path.GetTempPath(), "CodeBadgerProviderTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string recovery = Path.Combine(Path.GetTempPath(), "CodeBadgerProviderRecovery-" + Guid.NewGuid().ToString("N"));
        if (direct)
        {
            Directory.CreateDirectory(recovery);
            File.WriteAllText(Path.Combine(repository, "reviewed.txt"), "synthetic reviewed content");
            File.Copy(Path.Combine(assets, "repository-guardian.ps1"), Path.Combine(recovery, "repository-guardian.ps1"));
            File.Copy(Path.GetFullPath(Path.Combine(assets, "../../../Services/WindowsRepositoryAccessLease.cs")), Path.Combine(recovery, "WindowsRepositoryAccessLease.cs"));
        }
        string consoleOutput = "";
        Task reader = null;
        var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Func<string, string, string> prepare = (sid, folder) =>
            {
                home = folder;
                pipe = new CodeBadger.Services.SandboxBrokerPipe(sid);
                serving = pipe.ServeAsync((message, token) =>
                {
                    using var parsed = JsonDocument.Parse(message);
                    if (parsed.RootElement.GetProperty("operation").GetString() != "complete") throw new Exception("Unexpected operation.");
                    requests++;
                    return Task.FromResult("{\"response\":{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"SANDBOX_PROVIDER_OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}}");
                });
                string plugin = Path.Combine(folder, "provider-plugin");
                Directory.CreateDirectory(plugin);
                File.Copy(Path.Combine(assets, "server.mjs"), Path.Combine(plugin, "server.mjs"));
                File.Copy(Path.Combine(assets, "provider-transport.mjs"), Path.Combine(plugin, "provider-transport.mjs"));
                // Test-only instrumentation records lifecycle markers, never
                // prompts, credentials, request bodies or destination URLs.
                string serverSource = File.ReadAllText(Path.Combine(plugin, "server.mjs"));
                serverSource = "import fs from 'node:fs';\n" + serverSource.Replace("async setup(ctx) {",
                    "async setup(ctx) { fs.writeFileSync(" + JsonSerializer.Serialize(Path.Combine(folder, "adapter-setup.txt")) + ", 'setup');");
                serverSource = serverSource.Replace("const request = event.request;", "fs.writeFileSync(" +
                    JsonSerializer.Serialize(Path.Combine(folder, "adapter-http.txt")) + ", 'http'); const request = event.request;");
                File.WriteAllText(Path.Combine(plugin, "server.mjs"), serverSource);
                string transportSource = File.ReadAllText(Path.Combine(plugin, "provider-transport.mjs"));
                transportSource = "import fs from 'node:fs';\n" + transportSource.Replace("globalThis.fetch = async (input, init) => {",
                    "globalThis.fetch = async (input, init) => { fs.writeFileSync(" + JsonSerializer.Serialize(Path.Combine(folder, "adapter-fetch.txt")) + ", 'fetch');");
                File.WriteAllText(Path.Combine(plugin, "provider-transport.mjs"), transportSource);
                File.WriteAllText(Path.Combine(plugin, "package.json"), "{\"name\":\"codebadger-sandbox-test\",\"type\":\"module\",\"exports\":{\".\":\"./server.mjs\"}}");
                var config = new
                {
                    model = "codebadger/test-model",
                    snapshots = false,
                    plugins = new[] { new { package = plugin, options = new { pipeName = pipe.HostName } } },
                    providers = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["codebadger"] = new {
                            name = "CodeBadger test provider", package = "@opencode/ai/providers/openai-compatible",
                            settings = new { baseURL = "https://codebadger.invalid/v1", apiKey = "non-secret-test-placeholder" },
                            models = new System.Collections.Generic.Dictionary<string, object> { ["test-model"] = new { name = "Test", limit = new { context = 32000, output = 1024 } } }
                        }
                    }
                };
                File.WriteAllText(Path.Combine(folder, "opencode.json"), JsonSerializer.Serialize(config));
                return "run --standalone --print-logs --log-level error --model codebadger/test-model \"Reply OK without using tools\"";
            };
            terminal = direct
                ? CodeBadger.Services.WindowsSandboxTerminal.StartDirectPrepared(executable, "", repository, new[] { "reviewed.txt" }, prepare, recovery)
                : CodeBadger.Services.WindowsSandboxTerminal.StartPrepared(executable, repository, Array.Empty<string>(), prepare);
            process = terminal.Process;
            reader = Task.Run(() =>
            {
                byte[] buffer = new byte[8192]; int count;
                while ((count = terminal.Output.Read(buffer, 0, buffer.Length)) != 0)
                {
                    string text = System.Text.Encoding.UTF8.GetString(buffer, 0, count);
                    consoleOutput += text;
                    if (consoleOutput.Contains("SANDBOX_PROVIDER_OK")) answered.TrySetResult(true);
                    if (consoleOutput.Length > 32768) consoleOutput = consoleOutput.Substring(consoleOutput.Length - 32768);
                }
            });
            bool received = false;
            try { await answered.Task.WaitAsync(TimeSpan.FromSeconds(30)); received = true; }
            catch (TimeoutException) { }
            process.Stop();
            if (requests == 0 || !received)
            {
                string diagnostics = "";
                foreach (string file in Directory.EnumerateFiles(home, "*.log", SearchOption.AllDirectories))
                {
                    string content = File.ReadAllText(file);
                    diagnostics += content.Length > 8192 ? content.Substring(content.Length - 8192) : content;
                }
                diagnostics = diagnostics.Length > 3000 ? diagnostics.Substring(diagnostics.Length - 3000) : diagnostics;
                throw new Exception("OpenCode provider adapter did not return an answer. Pipe requests=" + requests +
                    ", adapter setup=" + File.Exists(Path.Combine(home, "adapter-setup.txt")) +
                    ", http hook=" + File.Exists(Path.Combine(home, "adapter-http.txt")) + ". " +
                    (consoleOutput.Length > 6000 ? consoleOutput.Substring(consoleOutput.Length - 6000) : consoleOutput) + diagnostics);
            }
            Console.WriteLine("OpenCode received a fake provider answer through the AppContainer pipe (" + (direct ? "direct LPAC" : "copy") + "); explicit stop succeeded.");
        }
        finally
        {
            pipe?.Dispose();
            if (terminal != null)
            {
                try { terminal.Process.Stop(); terminal.CloseConsole(); if (reader != null) await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception cleanup) { Console.WriteLine("Test console cleanup failed: " + cleanup.Message); }
                try { terminal.Dispose(); } catch (Exception cleanup) {
                    Console.WriteLine("Test cleanup failed: " + cleanup.Message);
                    foreach (string entry in Directory.EnumerateFileSystemEntries(home, "*", SearchOption.AllDirectories))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReadOnly) != 0) Console.WriteLine("Read-only test artifact: " + entry);
                        try
                        {
                            if ((attributes & FileAttributes.Directory) == 0)
                            {
                                using var probe = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None);
                            }
                        }
                        catch (Exception locked) { Console.WriteLine("Locked test artifact: " + entry + " (" + locked.Message + ")"); }
                    }
                }
            }
            if (serving != null) { try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            if (direct)
            {
                File.Delete(Path.Combine(repository, "reviewed.txt"));
                foreach (string file in Directory.EnumerateFiles(recovery)) File.Delete(file);
                Directory.Delete(recovery);
            }
            Directory.Delete(repository);
        }
    }
}
'@
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs', 'WindowsRepositoryAccessGuardian.cs', 'WindowsSandboxProcess.cs', 'WindowsSandboxTerminal.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
# Test-only flags confirmed in the installed V2 binary. Do not widen host ACLs.
$sources = $sources | ForEach-Object { $_.Replace('["HOME"] = sandboxHome,', '["HOME"] = sandboxHome, ["OPENCODE_CONFIG_PROJECT_DISABLE"] = "1", ["OPENCODE_CONFIG"] = Path.Combine(sandboxHome, "opencode.json"), ["OPENCODE_DISABLE_MODELS_FETCH"] = "1",') }
if ($ProbeWithoutFsctlMitigation) {
    # Diagnostic ONLY on the synthetic test repository. Never used by production startup.
    $sources = $sources | ForEach-Object { $_.Replace('Marshal.WriteInt64(mitigation, 8, 1L << 56);', 'Marshal.WriteInt64(mitigation, 8, 0);') }
}
if ($ProbeRuntimeCapability -ne 'registryRead') {
    # Isolated test-only runtime capability probe, not a production fallback.
    $sources = $sources | ForEach-Object { $_.Replace('new[] { "registryRead" }', 'new[] { "registryRead", "' + $ProbeRuntimeCapability + '" }') }
}
$pipe = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerPipe.cs')
$pipe = $pipe.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$usings = "#nullable enable annotations`nusing System; using System.IO; using System.Threading; using System.Threading.Tasks;"
$test = $test.Replace('using System;', '').Replace('using System.IO;', '').Replace('using System.Text.Json;', '').Replace('using System.Threading.Tasks;', '')
$errorSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerException.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
Add-Type -TypeDefinition ($usings + 'using System.Text.Json;' + $pipe + $errorSource + ($sources -join [Environment]::NewLine) + $test)
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Resources\Raw\sandbox'))
$null = [OpenCodeProviderTest]::Run($OpenCodeExecutable, $assets, $Direct.IsPresent).GetAwaiter().GetResult()
