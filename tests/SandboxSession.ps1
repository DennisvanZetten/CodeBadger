# Tests real isolated session management, output draining and review capture.
$ErrorActionPreference = 'Stop'
$test = @'
using System;
using System.IO;
using System.Threading.Tasks;
public static class SandboxSessionTest
{
    public static async Task Run(string repository)
    {
        await using var sessions = new CodeBadger.Services.WindowsSandboxSessionService();
        Guid id = Guid.NewGuid();
        var output = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessions.OutputReceived += (session, text) => { if (session == id && text.Contains("SESSION_READY")) output.TrySetResult(true); };
        await sessions.StartAsync(id, repository, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c \"echo changed>reviewed.txt & echo SESSION_READY & ping -n 30 127.0.0.1 >nul\"", new[] { "reviewed.txt" });
        if (!sessions.IsRunning(id)) throw new Exception("Session was not registered.");
        await output.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await sessions.StopAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
        if (sessions.IsRunning(id)) throw new Exception("Session was not stopped.");
        var changes = sessions.GetChanges(id, repository);
        if (changes.Count != 1) throw new Exception("Stopped session lost its changes.");
        if (sessions.GetChanges(id, repository + "-other").Count != 0) throw new Exception("Review leaked to another repository path.");
        if (File.ReadAllText(Path.Combine(repository, "reviewed.txt")) != "original") throw new Exception("Original file was overwritten.");
        await sessions.DiscardReviewAsync(id);
        if (sessions.GetChanges(id, repository).Count != 0) throw new Exception("Review was not discarded.");
        Console.WriteLine("Sandbox session stop, output and review tests passed.");
    }
}
'@
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs', 'WindowsSandboxProcess.cs', 'WindowsSandboxTerminal.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
$service = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsSandboxSessionService.cs')
$service = $service.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$usings = "#nullable enable annotations`nusing System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;"
$support = @'
public static class FileSystem
{
    public static Task<Stream> OpenAppPackageFileAsync(string name) => throw new NotSupportedException("Not used by this test.");
}
namespace CodeBadger.Services
{
    public sealed class SandboxNetworkBroker { }
    public sealed class SandboxApprovalService { }
    public sealed class SandboxProviderBridge : IDisposable
    {
        public string PipeName => "test";
        public SandboxProviderBridge(string sid, Guid id, string path, Uri endpoint, string model, string key, SandboxNetworkBroker broker, SandboxApprovalService approvals) { }
        public void Dispose() { }
    }
    public static class SandboxOpenCodeConfiguration
    {
        public static void Stage(string folder, string pipe, string model, string plugin, string transport) => throw new NotSupportedException("Not used by this test.");
    }
}
'@
Add-Type -TypeDefinition ($usings + $service + $support + $test.Replace('using System;', '').Replace('using System.IO;', '').Replace('using System.Threading.Tasks;', '') + [Environment]::NewLine + ($sources -join [Environment]::NewLine))
$repository = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerSessionTest-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $repository
$file = Join-Path $repository 'reviewed.txt'
[IO.File]::WriteAllText($file, 'original')
try {
    $null = [SandboxSessionTest]::Run($repository).GetAwaiter().GetResult()
} finally {
    Remove-Item -LiteralPath $file
    Remove-Item -LiteralPath $repository
}
