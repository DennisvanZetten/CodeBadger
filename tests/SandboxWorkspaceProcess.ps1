# End-to-end file isolation with a built-in Windows process and a test repository.
# No AI-provider requests or production repository files are used.
$ErrorActionPreference = 'Stop'
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs', 'WindowsRepositoryAccessGuardian.cs', 'WindowsSandboxProcess.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
Add-Type -TypeDefinition ($sources -join [Environment]::NewLine)
$repository = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerWorkspaceProcess-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $repository
$file = Join-Path $repository 'reviewed.txt'
$secret = Join-Path $repository '.env'
[IO.File]::WriteAllText($file, 'reviewed test content')
[IO.File]::WriteAllText($secret, 'secret test content only')
$process = $null
try {
    $command = '/d /c "type reviewed.txt >nul 2>&1 & (if errorlevel 1 exit 10) & (if exist .env exit 11) & type "' + $secret + '" >nul 2>&1 & (if not errorlevel 1 exit 12) & echo changed test content>reviewed.txt & exit 42"'
    $process = [CodeBadger.Services.WindowsSandboxProcess]::StartWithWorkspace(
        (Join-Path ([Environment]::SystemDirectory) 'cmd.exe'), $command, $repository, [string[]]@('reviewed.txt'))
    $exitCode = $process.WaitForExit(15000)
    if ($exitCode -ne 42) { throw "Sandbox workspace process failed (exit $exitCode)." }
    $copied = Join-Path $process.Workspace.Directory 'reviewed.txt'
    if ([IO.File]::ReadAllText($copied).Trim() -ne 'changed test content') { throw 'Sandbox copy was not writable.' }
    if ([IO.File]::ReadAllText($file) -ne 'reviewed test content') { throw 'Original repository was modified.' }
    $changes = $process.StopAndCaptureChanges()
    if ($changes.Count -ne 1 -or $changes[0].RelativePath -ne 'reviewed.txt' -or $changes[0].Deleted) { throw 'Changed file was not captured.' }
    if ([Text.Encoding]::UTF8.GetString($changes[0].GetContent()).Trim() -ne 'changed test content') { throw 'Captured content differs.' }
    $copy = $changes[0].GetContent()
    $copy[0] = 0
    if ($changes[0].GetContent()[0] -eq 0) { throw 'Review snapshot is mutable.' }
    $process.Dispose()
    $process = $null
    if ([Text.Encoding]::UTF8.GetString($changes[0].GetContent()).Trim() -ne 'changed test content') { throw 'Captured changes were lost during cleanup.' }
    $backupParent = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerApplyTest-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $backupParent
    try {
        [IO.File]::WriteAllText($file, 'concurrent user edit')
        $conflict = $false
        try { $null = [CodeBadger.Services.SandboxWorkspace]::ApplyModification($repository, $changes[0], $backupParent) } catch { $conflict = $true }
        if (-not $conflict -or [IO.File]::ReadAllText($file) -ne 'concurrent user edit') { throw 'Concurrent edit was overwritten.' }
        [IO.File]::WriteAllText($file, 'reviewed test content')
        $backup = [CodeBadger.Services.SandboxWorkspace]::ApplyModification($repository, $changes[0], $backupParent)
        if ([IO.File]::ReadAllText($file).Trim() -ne 'changed test content') { throw 'Approved modification was not applied.' }
        if ([IO.File]::ReadAllText($backup) -ne 'reviewed test content') { throw 'Backup did not preserve the original.' }
    } finally {
        Get-ChildItem -LiteralPath $backupParent -File | Remove-Item
        Remove-Item -LiteralPath $backupParent
    }
    Write-Output 'Sandbox process can edit its reviewed copy but cannot read the original secret.'
} finally {
    if ($null -ne $process) { $process.Dispose() }
    Remove-Item -LiteralPath $file, $secret
    Remove-Item -LiteralPath $repository
}
