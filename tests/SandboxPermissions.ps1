# Storage tests using the real permission service and a fake terminal service.
# These do not verify OS process termination or sandbox network enforcement.
$ErrorActionPreference = 'Stop'
$support = @'
using System;
using System.IO;
using System.Threading.Tasks;

public static class FileSystem
{
    public static string AppDataDirectory { get; set; }
}
namespace CodeBadger.Services
{
    public sealed class WindowsSandboxSessionService
    {
        public Task StopAsync(Guid id) => Task.CompletedTask;
    }
    public sealed class WindowsTerminalSessionService
    {
        public bool FailStop { get; set; }
        public int StopCount { get; private set; }
        public Task StopAsync(Guid id, bool requireTermination = false)
        {
            if (!requireTermination) throw new Exception("Revocation did not require termination.");
            if (FailStop) throw new Exception("Test stop failure.");
            StopCount++;
            return Task.CompletedTask;
        }
    }
}
'@
# Wrap file-scoped sources for Add-Type's combined compilation unit.
$service = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxPermissionService.cs')
$service = $service.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$model = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Models\SandboxPermission.cs')
$model = $model.Replace('namespace CodeBadger.Models;', 'namespace CodeBadger.Models { using System;') + '}'
$usings = "#nullable enable annotations`nusing System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;"
Add-Type -TypeDefinition ($usings + $service + $model + $support.Replace('using System;', '').Replace('using System.IO;', '').Replace('using System.Threading.Tasks;', ''))

$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('CodeBadgerPermissionsTest-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
[FileSystem]::AppDataDirectory = $directory
$file = Join-Path $directory 'sandbox-permissions.json'
try {
    $terminals = [CodeBadger.Services.WindowsTerminalSessionService]::new()
    $service = [CodeBadger.Services.SandboxPermissionService]::new($terminals, [CodeBadger.Services.WindowsSandboxSessionService]::new())
    $id = [guid]::NewGuid()
    $repository = Join-Path $directory 'repository'
    $destination = [uri]'https://example.com/path?token=test-only'
    $null = $service.GrantAsync($id, $repository, $destination).GetAwaiter().GetResult()
    $null = $service.GrantAsync($id, $repository, [uri]'https://example.com/other').GetAwaiter().GetResult()
    $records = $service.GetAsync($id, $repository).GetAwaiter().GetResult()
    if ($records.Count -ne 1) { throw 'Duplicate origin grant was not deduplicated.' }
    if ((Get-Content -Raw -LiteralPath $file).Contains('test-only')) { throw 'Request query was persisted.' }
    if (-not $service.IsAllowedAsync($id, $repository, $destination).GetAwaiter().GetResult()) { throw 'Saved grant was not found.' }
    if ($service.IsAllowedAsync($id, ($repository + '-other'), $destination).GetAwaiter().GetResult()) { throw 'Grant leaked to a different path.' }
    if ($service.IsAllowedAsync([guid]::NewGuid(), $repository, $destination).GetAwaiter().GetResult()) { throw 'Grant leaked to a different repository.' }

    $terminals.FailStop = $true
    $failed = $false
    try { $service.RevokeAsync($id, $repository, $records[0].Id).GetAwaiter().GetResult() } catch { $failed = $true }
    if (-not $failed) { throw 'Revocation incorrectly succeeded after stop failure.' }
    if (-not $service.IsAllowedAsync($id, $repository, $destination).GetAwaiter().GetResult()) { throw 'Failed revocation was recorded as successful.' }
    $terminals.FailStop = $false
    $null = $service.RevokeAsync($id, $repository, $records[0].Id).GetAwaiter().GetResult()
    if ($terminals.StopCount -ne 1) { throw 'Revocation did not stop the session.' }
    if ($service.IsAllowedAsync($id, $repository, $destination).GetAwaiter().GetResult()) { throw 'Revoked permission is still allowed.' }
    $records = $service.GetAsync($id, $repository).GetAwaiter().GetResult()
    if ($null -eq $records[0].RevokedAt) { throw 'Revocation history was lost.' }

    [System.IO.File]::WriteAllText($file, 'invalid json')
    $failed = $false
    try { $service.IsAllowedAsync($id, $repository, $destination).GetAwaiter().GetResult() } catch { $failed = $true }
    if (-not $failed) { throw 'Corrupt store did not fail closed.' }
    Write-Output 'Sandbox permission storage tests passed.'
} finally {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    Remove-Item -LiteralPath $directory
}
