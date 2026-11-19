# Uses disposable fake installations, never executes a detected binary.
$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\OpenCodeExecutableLocator.cs')
Add-Type -TypeDefinition ("#nullable enable annotations`nusing System; using System.IO;" + $source)
$root = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerLocator-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    $exe = Join-Path $root 'opencode.exe'
    [IO.File]::WriteAllText($exe, 'test only')
    if ([CodeBadger.Services.OpenCodeExecutableLocator]::FindInPath($root) -ne $exe) { throw 'Standalone executable was not found.' }
    Remove-Item -LiteralPath $exe
    $bin = Join-Path $root 'node_modules\@opencode\cli\bin'
    $null = New-Item -ItemType Directory -Path $bin
    $npmExe = Join-Path $bin 'opencode.exe'
    [IO.File]::WriteAllText($npmExe, 'test only')
    if ([CodeBadger.Services.OpenCodeExecutableLocator]::FindInPath('"' + $root + '"') -ne $npmExe) { throw 'npm executable was not found.' }
    if ($null -ne [CodeBadger.Services.OpenCodeExecutableLocator]::FindInPath('relative-path')) { throw 'Relative PATH entry was accepted.' }
    Write-Output 'OpenCode executable detection tests passed.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
