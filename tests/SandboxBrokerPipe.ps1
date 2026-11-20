# Tests both host-user rejection and a real AppContainer client's request/reply.
param([switch]$TestIdleTimeout)
$ErrorActionPreference = 'Stop'
$test = @'
using System;
using System.IO.Pipes;
using System.Threading.Tasks;
public static class BrokerPipeTest
{
    public static async Task Run(bool testIdleTimeout)
    {
        using var profile = CodeBadger.Services.WindowsAppContainerProfile.Create();
        using var pipe = new CodeBadger.Services.SandboxBrokerPipe(profile.Sid);
        int invoked = 0;
        Task serving = pipe.ServeAsync((message, token) => { invoked++; return Task.FromResult("response"); });
        using var client = new NamedPipeClientStream(".", pipe.HostName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        byte[] packet = { 1, 0, 0, 0, 65 };
        await client.WriteAsync(packet);
        await client.FlushAsync();
        bool rejected = false;
        try { await serving.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (UnauthorizedAccessException) { rejected = true; }
        if (!rejected || invoked != 0) throw new Exception("Host client reached the sandbox broker handler.");
        Console.WriteLine("Sandbox broker pipe rejects a non-AppContainer client.");

        CodeBadger.Services.SandboxBrokerPipe allowedPipe = null;
        Task allowedServing = null;
        string diagnostic = null;
        using var sandbox = CodeBadger.Services.WindowsSandboxProcess.StartStagedPrepared(
            System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), (sid, folder) =>
            {
                allowedPipe = new CodeBadger.Services.SandboxBrokerPipe(sid);
                diagnostic = System.IO.Path.Combine(folder, "pipe-test-error.txt");
                allowedServing = allowedPipe.ServeAsync((message, token) =>
                {
                    if (message != "sandbox-test") throw new Exception("Unexpected sandbox request.");
                    return Task.FromResult("host-response");
                });
                string script = "$ErrorActionPreference='Stop'; try {" +
                    "$p=New-Object IO.Pipes.NamedPipeClientStream('.', '" + allowedPipe.Name + "', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification);" +
                    "$p.Connect(5000); $w=New-Object IO.BinaryWriter($p); $b=[Text.Encoding]::UTF8.GetBytes('sandbox-test'); $w.Write([int]$b.Length); $w.Write($b); $w.Flush();" +
                    "$r=New-Object IO.BinaryReader($p); $n=$r.ReadInt32(); $s=[Text.Encoding]::UTF8.GetString($r.ReadBytes($n)); if($s -ne 'host-response'){exit 3};" +
                    (testIdleTimeout ? "Start-Sleep -Seconds 125; $w.Write([int]$b.Length); $w.Write($b); $w.Flush(); $n=$r.ReadInt32(); $s=[Text.Encoding]::UTF8.GetString($r.ReadBytes($n)); if($s -ne 'host-response'){exit 5};" : "") +
                    "$p.Dispose(); exit 42" +
                    "} catch {[IO.File]::WriteAllText('" + diagnostic + "',$_.ToString()); exit 4}";
                string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                string powershell = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                return "/d /c \"\"" + powershell + "\" -NoProfile -NonInteractive -EncodedCommand " + encoded + "\"";
            });
        try
        {
            uint result = sandbox.WaitForExit(testIdleTimeout ? 150000 : 15000);
            if (result != 42) throw new Exception("AppContainer pipe client failed (exit " + result + "): " +
                (System.IO.File.Exists(diagnostic) ? System.IO.File.ReadAllText(diagnostic) : "No diagnostics"));
            try { await allowedServing.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (System.IO.EndOfStreamException) { }
            Console.WriteLine("Sandbox broker pipe accepts the intended AppContainer and returns a response.");
        }
        finally { allowedPipe?.Dispose(); }
    }
}
'@
$pipe = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerPipe.cs')
$pipe = $pipe.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$profile = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsAppContainerProfile.cs')
$process = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsSandboxProcess.cs')
$workspace = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxWorkspace.cs')
$guardian = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsRepositoryAccessGuardian.cs')
$environment = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxProcessEnvironment.cs')
$usings = "#nullable enable annotations`nusing System; using System.IO; using System.Threading; using System.Threading.Tasks;"
$test = $test.Replace('using System;', '').Replace('using System.IO.Pipes;', '').Replace('using System.Threading.Tasks;', '')
$errorSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerException.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
Add-Type -TypeDefinition ($usings + $pipe + $errorSource + $profile + $process + $workspace + $guardian + $environment + $test)
$null = [BrokerPipeTest]::Run($TestIdleTimeout.IsPresent).GetAwaiter().GetResult()
