# Independent host process: never runs inside the AppContainer or its Job Object.
# Parent death closes stdin. Originals are durably journaled before any ACL grant.
$ErrorActionPreference = 'Stop'
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowsRepositoryAccessLease.cs')
    $request = [Console]::ReadLine() | ConvertFrom-Json
    if ($request.operation -eq 'recover') {
        [CodeBadger.Services.WindowsRepositoryAccessLease]::Recover($request.journal)
        [Console]::WriteLine('RECOVERED')
        exit 0
    }
    $gate = [IO.FileStream]::new((Join-Path $PSScriptRoot 'guardian.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $job = [CodeBadger.Services.RepositoryGuardianJob]::new($request.jobName)
    $lease = [CodeBadger.Services.WindowsRepositoryAccessLease]::Grant(
        $request.root, [string[]]$request.files, $request.sid, $request.journal)
    [Console]::WriteLine('READY')
    # EOF also covers host termination, so cleanup does not depend on finally in CodeBadger.
    $null = [Console]::ReadLine()
    $job.Stop()
    $lease.Dispose()
    $job.Dispose()
    $gate.Dispose()
    [Console]::WriteLine('REVOKED')
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
