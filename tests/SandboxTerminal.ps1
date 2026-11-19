# ConPTY integration test with a generated repository and built-in cmd.exe.
$ErrorActionPreference = 'Stop'
$test = @'
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
public static class SandboxTerminalTest
{
    public static async Task Run(string repository)
    {
        using var terminal = CodeBadger.Services.WindowsSandboxTerminal.Start(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q", repository, new[] { "reviewed.txt" });
        terminal.Resize(100, 25);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = Task.Run(() =>
        {
            var content = new StringBuilder();
            var buffer = new byte[4096];
            int count;
            while ((count = terminal.Output.Read(buffer, 0, buffer.Length)) > 0)
            {
                content.Append(Encoding.UTF8.GetString(buffer, 0, count));
                if (content.ToString().Contains(">")) ready.TrySetResult(true);
                if (content.ToString().Contains("SANDBOX_OUTPUT_OK")) return;
                if (content.Length > 65536) throw new Exception("Unexpectedly large terminal output.");
            }
            throw new Exception("Terminal ended without expected output.");
        });
        // The caret is removed by cmd during execution, so the marker cannot
        // match the echoed command text. Check copy access and host isolation.
        byte[] command = Encoding.UTF8.GetBytes("@type reviewed.txt >nul 2>&1 && (if not defined CODEBADGER_TERMINAL_TEST_SECRET echo SANDBOX_OUTPUT_^OK)\r");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await terminal.Input.WriteAsync(command);
        await terminal.Input.FlushAsync();
        await reading.WaitAsync(TimeSpan.FromSeconds(15));
        terminal.Process.Stop();
        Console.WriteLine("Sandbox ConPTY input, output and resize tests passed.");
    }
}
'@
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs', 'WindowsSandboxProcess.cs', 'WindowsSandboxTerminal.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
Add-Type -TypeDefinition ($test + [Environment]::NewLine + ($sources -join [Environment]::NewLine))
$repository = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerTerminalTest-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $repository
$file = Join-Path $repository 'reviewed.txt'
[IO.File]::WriteAllText($file, 'reviewed test content')
$previous = $env:CODEBADGER_TERMINAL_TEST_SECRET
try {
    $env:CODEBADGER_TERMINAL_TEST_SECRET = 'test only'
    $null = [SandboxTerminalTest]::Run($repository).GetAwaiter().GetResult()
} finally {
    $env:CODEBADGER_TERMINAL_TEST_SECRET = $previous
    Remove-Item -LiteralPath $file
    Remove-Item -LiteralPath $repository
}
