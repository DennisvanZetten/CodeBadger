# Starts only `opencode --version` in a temporary, no-network AppContainer.
# Uses only a generated test repository; no host credentials or AI provider.
param([Parameter(Mandatory = $true)][string]$OpenCodeExecutable)
$ErrorActionPreference = 'Stop'
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs', 'WindowsSandboxProcess.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
Add-Type -TypeDefinition ($sources -join [Environment]::NewLine)
$repository = Join-Path ([IO.Path]::GetTempPath()) ('CodeBadgerOpenCodeTest-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $repository
$testFile = Join-Path $repository 'reviewed.txt'
[IO.File]::WriteAllText($testFile, 'reviewed test content')
$process = $null
try {
    $process = [CodeBadger.Services.WindowsSandboxProcess]::StartWithWorkspace($OpenCodeExecutable, '--version', $repository, [string[]]@('reviewed.txt'))
    $exitCode = $process.WaitForExit(30000)
    if ($exitCode -ne 0) { throw "OpenCode did not start successfully inside AppContainer (exit $exitCode)." }
    Write-Output 'OpenCode --version started successfully inside AppContainer with a private workspace.'
} finally {
    if ($null -ne $process) { $process.Dispose() }
    Remove-Item -LiteralPath $testFile
    Remove-Item -LiteralPath $repository
}
