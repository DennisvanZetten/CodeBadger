# Reproducible build-time runtime packaging. Never downloads code at sandbox startup.
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'Resources\Raw\sandbox-sdk' }
$dependencies = Join-Path $root 'tests\sdk-probe'
& npm ci --ignore-scripts --no-audit --no-fund --prefix $dependencies
if ($LASTEXITCODE -ne 0) { throw 'Pinned SDK dependency restore failed.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$download = Join-Path $OutputDirectory ('node-' + [guid]::NewGuid().ToString('N') + '.download')
$archivePath = Join-Path $OutputDirectory 'runtime.zip'
$tempArchive = $archivePath + '.tmp'
try {
    Invoke-WebRequest -Uri 'https://nodejs.org/dist/v24.11.0/win-x64/node.exe' -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne 'B7D912484D42E7A0D0CB5B26A86410EC973A79ECE7D61AD535E2D1A97A9026E1') {
        throw 'Node runtime integrity verification failed.'
    }
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($tempArchive, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $download, 'node.exe', [IO.Compression.CompressionLevel]::Fastest)
        $modules = Join-Path $dependencies 'node_modules'
        foreach ($file in Get-ChildItem -LiteralPath $modules -Recurse -File) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked runtime dependency is unsupported.' }
            $relative = 'node_modules/' + [IO.Path]::GetRelativePath($modules, $file.FullName).Replace('\', '/')
            # Optional Claude CLI and build tooling are not part of this provider-only SDK runtime.
            if ($relative -match '^node_modules/(@oven/|@anthropic-ai/claude-agent-sdk-win32-|typescript/)' -or $file.Extension -eq '.pdb') { continue }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Fastest)
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Move-Item -LiteralPath $tempArchive -Destination $archivePath -Force
    $manifest = @{ version = 1; sdk = '2.3.2'; node = '24.11.0'; architecture = 'x64'; sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'runtime.json'), ($manifest | ConvertTo-Json -Compress))
    Write-Output 'Verified SDK runtime packaged.'
} finally {
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
    if (Test-Path -LiteralPath $tempArchive) { Remove-Item -LiteralPath $tempArchive }
}
