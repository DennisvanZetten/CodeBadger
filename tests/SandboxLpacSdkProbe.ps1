# Listenerless SDK probe in the production direct LPAC process policy.
param([string]$NodeExecutable = '', [switch]$Prompt, [switch]$Edit, [switch]$Runner)
$ErrorActionPreference = 'Stop'
if (-not $NodeExecutable) { $NodeExecutable = (Get-Command node.exe -ErrorAction Stop).Source }
if ($Edit -and -not $Prompt) { throw 'Use -Prompt with -Edit.' }
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs',
    'WindowsRepositoryAccessGuardian.cs', 'WindowsSandboxProcess.cs', 'WindowsSandboxTerminal.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
Add-Type -TypeDefinition ($sources -join [Environment]::NewLine)
$pipeSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerPipe.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$errorSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerException.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$fakeSource = @'
public static class SdkFakeProvider
{
    public static CodeBadger.Services.SandboxBrokerPipe Pipe;
    public static Task Serving;
    public static bool Edit;
    private static int calls;
    public static void Start(string sid)
    {
        Pipe = new CodeBadger.Services.SandboxBrokerPipe(sid);
        Serving = Pipe.ServeAsync((message, cancellation) => {
            if (Edit && message.Contains("\"tools\"") && System.Threading.Interlocked.Increment(ref calls) == 1)
                return Task.FromResult("{\"response\":{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"edit-test\",\"type\":\"function\",\"function\":{\"name\":\"edit\",\"arguments\":\"{\\\"filePath\\\":\\\"reviewed.txt\\\",\\\"oldString\\\":\\\"synthetic\\\",\\\"newString\\\":\\\"SDK_EDIT_OK\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}}");
            return Task.FromResult("{\"response\":{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"SDK_PROVIDER_OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}}");
        });
    }
}
'@
$fakeSource = $fakeSource.Replace('filePath', 'path')
$chatSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxSdkChatBridge.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$runtimeSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxSdkRuntime.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$extraUsings = ''
foreach ($sourceName in @('chatSource', 'runtimeSource')) {
    $value = Get-Variable -Name $sourceName -ValueOnly
    $extraUsings += ([regex]::Matches($value, '(?m)^using [^;]+;') | ForEach-Object Value) -join ''
    Set-Variable -Name $sourceName -Value ([regex]::Replace($value, '(?m)^using [^;]+;\r?\n', ''))
}
$extraUsings = ($extraUsings.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | Select-Object -Unique | ForEach-Object { $_ + ';' }) -join ''
Add-Type -TypeDefinition ("#nullable enable annotations`nusing System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;" + $extraUsings + $pipeSource + $errorSource + $chatSource + $runtimeSource + $fakeSource + @'
public static class FileSystem
{
    public static Task<Stream> OpenAppPackageFileAsync(string name) => throw new NotSupportedException();
}
public static class SdkChatRunnerTest
{
    public static CodeBadger.Services.SandboxSdkChatBridge Chat;
    public static TaskCompletionSource Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int answers;
    public static void Start(string sid)
    {
        Chat = new CodeBadger.Services.SandboxSdkChatBridge(sid);
        Chat.Updated += (type, text) => {
            if (type == "ready") Chat.Send("Update reviewed.txt, then reply OK.");
            if (type == "text" && text.Contains("SDK_PROVIDER_OK")) {
                if (System.Threading.Interlocked.Increment(ref answers) == 1) Chat.Send("Reply OK again without editing files.");
                else Answer.TrySetResult();
            }
            if (type == "error") Answer.TrySetException(new Exception(text));
        };
    }
}
'@)
$root = Join-Path $env:LOCALAPPDATA ('CodeBadgerSdkProbe-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repo'
$private = Join-Path $root 'private'
$null = New-Item -ItemType Directory -Path $repo, $private
[IO.File]::WriteAllText((Join-Path $repo 'reviewed.txt'), 'synthetic')
[IO.File]::WriteAllText((Join-Path $repo 'unselected.txt'), 'synthetic unselected')
[IO.File]::WriteAllText((Join-Path $repo '.env'), 'synthetic secret, not a credential')
[IO.File]::WriteAllText((Join-Path $root 'outside.txt'), 'synthetic outside')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\repository-guardian.ps1') -Destination $private
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsRepositoryAccessLease.cs') -Destination $private
$runtime = Join-Path $PSScriptRoot 'sdk-probe'
try {
    $prepare = [Func[string,string,string]] {
        param($sid, $folder)
        $global:sdkProbeHome = $folder
        if (-not $Runner) {
            & robocopy (Join-Path $runtime 'node_modules') (Join-Path $folder 'node_modules') /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /MT:16 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw 'Private SDK dependency staging failed.' }
            Copy-Item -LiteralPath (Join-Path $runtime 'probe.mjs') -Destination $folder
        }
        if ($Prompt) {
            [SdkFakeProvider]::Edit = $Edit.IsPresent
            [SdkFakeProvider]::Start($sid)
            $plugin = Join-Path $folder 'provider-plugin'
            $null = New-Item -ItemType Directory -Path $plugin
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\server.mjs') -Destination $plugin
            $server = [IO.File]::ReadAllText((Join-Path $plugin 'server.mjs'))
            $server = $server.Replace('async setup(ctx) {', 'async setup(ctx) { await ctx.session.hook("title", event => { event.result = "Synthetic SDK test"; });')
            [IO.File]::WriteAllText((Join-Path $plugin 'server.mjs'), $server)
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\provider-transport.mjs') -Destination $plugin
            [IO.File]::WriteAllText((Join-Path $plugin 'package.json'), '{"name":"sdk-fake-provider","type":"module","exports":{".":"./server.mjs"}}')
            $config = @{ model = 'codebadger/test-model'; snapshots = $false; permissions = @(@{ action='edit';resource='*';effect='allow' }); plugins = @(@{ package = $plugin; options = @{ pipeName = [SdkFakeProvider]::Pipe.HostName } });
                providers = @{ codebadger = @{ name = 'Fake'; package = '@opencode/ai/providers/openai-compatible'; settings = @{ baseURL = 'https://codebadger.invalid/v1'; apiKey = 'non-secret-test-placeholder' }; models = @{ 'test-model' = @{ name = 'Test'; limit = @{ context = 32000; output = 1024 } } } } } }
            [IO.File]::WriteAllText((Join-Path $folder 'opencode.json'), ($config | ConvertTo-Json -Depth 10 -Compress))
        }
        if ($Runner) {
            [SdkChatRunnerTest]::Start($sid)
            $archive = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\Resources\Raw\sandbox-sdk\runtime.zip'))
            $runnerText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\sdk-runner.mjs'))
            $transportText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\provider-transport.mjs'))
            $global:stagedSdkNode = [CodeBadger.Services.SandboxSdkRuntime]::Stage($folder, $archive, $runnerText, $transportText, [SdkChatRunnerTest]::Chat.PipeName)
            return '--preserve-symlinks --preserve-symlinks-main "' + (Join-Path $folder 'sdk-runtime\sdk-runner.mjs') + '"'
        }
        return '--preserve-symlinks --preserve-symlinks-main "' + (Join-Path $folder 'probe.mjs') + '" --sandbox --security' + $(if ($Prompt) { ' --prompt' } else { '' })
    }
    if ($Runner) {
        $stage = [Func[string,string,string]] { param($sid, $folder) $global:runnerArgs = $prepare.Invoke($sid, $folder); return $global:stagedSdkNode }
        $argsHook = [Func[string,string,string]] { param($sid, $folder) return $global:runnerArgs }
        $terminal = [CodeBadger.Services.WindowsSandboxTerminal]::StartDirectRuntime($repo, [string[]]@('reviewed.txt'), $stage, $argsHook, $private)
    } else {
        $terminal = [CodeBadger.Services.WindowsSandboxTerminal]::StartDirectPrepared($NodeExecutable, '', $repo,
            [string[]]@('reviewed.txt'), $prepare, $private)
    }
    try {
        # Output is drained concurrently, including shutdown output.
        $reader = [IO.StreamReader]::new($terminal.Output)
        $output = $reader.ReadToEndAsync()
        if ($Runner) {
            try { $null = [SdkChatRunnerTest]::Answer.Task.WaitAsync([TimeSpan]::FromSeconds(45)).GetAwaiter().GetResult(); $code = 0 }
            catch {
                $terminal.Process.Stop(); $terminal.CloseConsole()
                Write-Output $output.GetAwaiter().GetResult()
                throw
            }
        }
        else { $code = $terminal.Process.WaitForExit(45000) }
        $terminal.Process.Stop(); $terminal.CloseConsole()
        $text = $output.GetAwaiter().GetResult()
        Write-Output $text
        if ($code -ne 0) {
            Get-ChildItem -LiteralPath $global:sdkProbeHome -Filter '*.log' -Recurse -File |
                ForEach-Object { Get-Content -LiteralPath $_.FullName -Tail 30 }
        }
        if (-not $Runner -and ($code -ne 0 -or $text -notmatch 'SDK_READY')) { throw "LPAC SDK smoke test failed ($code)." }
        if (-not $Runner -and $Prompt -and $text -notmatch 'SDK_PROVIDER_OK') { throw 'SDK did not receive the fake provider answer.' }
        if ($Edit -and [IO.File]::ReadAllText((Join-Path $repo 'reviewed.txt')) -ne 'SDK_EDIT_OK') { throw 'SDK tool did not edit the real selected file.' }
        if ($Runner) { Write-Output 'Packaged SDK chat: two prompts, fake-provider pipe, direct edit and stop passed.' }
    } finally { $terminal.Dispose() }
} finally { if ([SdkFakeProvider]::Pipe) { [SdkFakeProvider]::Pipe.Dispose() }; if ([SdkChatRunnerTest]::Chat) { [SdkChatRunnerTest]::Chat.Dispose() }; Remove-Item -LiteralPath $root -Recurse -Force }
