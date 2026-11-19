# Workspace preparation tests; no real repository files are copied.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxWorkspace.cs'))
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('CodeBadgerWorkspaceTest-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repo'
$private = Join-Path $root 'private'
$null = New-Item -ItemType Directory -Path (Join-Path $repo 'src'), $private
try {
    $source = Join-Path $repo 'src\example.txt'
    [IO.File]::WriteAllText($source, 'reviewed test content')
    [IO.File]::WriteAllText((Join-Path $repo '.env'), 'test-secret-only')
    $large = Join-Path $repo 'src\large.bin'
    $stream = [IO.File]::Create($large)
    try { $stream.SetLength(17MB) } finally { $stream.Dispose() }
    $normal = [CodeBadger.Services.SandboxWorkspace]::Create($repo, $private, [string[]]@('src/large.bin'))
    if ($normal.Files[0].Length -ne 17MB -or $normal.Limits.FileBytes -ne 64MB -or $normal.Limits.TotalBytes -ne 1024MB) { throw 'Default sandbox limits were not applied.' }
    $rejected = $false
    try { $null = [CodeBadger.Services.SandboxWorkspace]::Create($repo, $private, [string[]]@('src/large.bin'), [CodeBadger.Services.SandboxWorkspace+Limits]::new(16, 1024)) }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Custom per-file limit was not enforced.' }
    $rejected = $false
    try { $null = [CodeBadger.Services.SandboxWorkspace]::Create($repo, $private, [string[]]@('src/large.bin'), [CodeBadger.Services.SandboxWorkspace+Limits]::new(64, 16)) }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Custom total limit was not enforced.' }
    $workspace = [CodeBadger.Services.SandboxWorkspace]::Create($repo, $private, [string[]]@('src/example.txt'))
    if ($workspace.Files.Count -ne 1) { throw 'Unexpected copied file count.' }
    if ([IO.File]::ReadAllText((Join-Path $workspace.Directory 'src\example.txt')) -ne 'reviewed test content') { throw 'Copied content differs.' }
    if (Test-Path -LiteralPath (Join-Path $workspace.Directory '.env')) { throw 'Unselected secret was copied.' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $workspace.Files[0].Sha256) { throw 'Manifest hash differs.' }
    if ([CodeBadger.Services.SandboxWorkspace]::CaptureChanges($workspace).Count -ne 0) { throw 'Unchanged copy was reported as modified.' }
    $copiedSource = Join-Path $workspace.Directory 'src\example.txt'
    Remove-Item -LiteralPath $copiedSource
    $deleted = [CodeBadger.Services.SandboxWorkspace]::CaptureChanges($workspace)
    if ($deleted.Count -ne 1 -or -not $deleted[0].Deleted) { throw 'Deletion was not captured.' }
    [IO.File]::WriteAllText($copiedSource, 'modified review content')
    $outputLink = Join-Path $workspace.Directory 'linked-output.txt'
    $null = New-Item -ItemType HardLink -Path $outputLink -Target $copiedSource
    $denied = $false
    try { $null = [CodeBadger.Services.SandboxWorkspace]::CaptureChanges($workspace) } catch { $denied = $true }
    if (-not $denied) { throw 'Linked output was accepted for review.' }
    foreach ($selection in @('.env', '..\private\file.txt', 'src\..\example.txt', 'src\example.txt:stream', '.git\config', '.opencode\plugins\index.ts', 'secrets.json', 'private.pem', 'NUL.txt', 'src\example.txt.')) {
        $denied = $false
        try { $null = [CodeBadger.Services.SandboxWorkspace]::Create($repo, $private, [string[]]@($selection)) } catch { $denied = $true }
        if (-not $denied) { throw "Unsafe path was accepted: $selection" }
    }
    $link = Join-Path $repo 'linked.txt'
    $null = New-Item -ItemType HardLink -Path $link -Target $source
    $denied = $false
    try { $null = [CodeBadger.Services.SandboxWorkspace]::Create($repo, $private, [string[]]@('linked.txt')) } catch { $denied = $true }
    if (-not $denied) { throw 'Hardlinked source was accepted.' }
    if ([IO.File]::ReadAllText($source) -ne 'reviewed test content') { throw 'Original source was modified.' }
    Write-Output 'Sandbox workspace preparation tests passed.'
} finally {
    # Delete only the unique test directory created above, never a user path.
    Remove-Item -LiteralPath $root -Recurse -Force
}
