# Synthetic diagnostic. No firewall rules, exemptions or host ACLs are modified.
param([Parameter(Mandatory = $true)][string]$NodeExecutable)
$ErrorActionPreference = 'Stop'
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs',
    'WindowsRepositoryAccessGuardian.cs', 'WindowsSandboxProcess.cs', 'WindowsSandboxTerminal.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
Add-Type -TypeDefinition ($sources -join [Environment]::NewLine)
$root = Join-Path $env:LOCALAPPDATA ('CodeBadgerSocketProbe-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repo'
$private = Join-Path $root 'private'
$null = New-Item -ItemType Directory -Path $repo, $private
[IO.File]::WriteAllText((Join-Path $repo 'reviewed.txt'), 'synthetic')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\repository-guardian.ps1') -Destination $private
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsRepositoryAccessLease.cs') -Destination $private
try {
    $prepare = [Func[string,string,string]] {
        param($sid, $folder)
        $global:probeHome = $folder
        $js = @'
const fs = require('node:fs');
const net = require('node:net');
const output = require('node:path').join(process.env.HOME, 'socket-result.json');
const server = net.createServer();
server.on('error', e => { fs.writeFileSync(output, JSON.stringify({code:e.code,errno:e.errno,syscall:e.syscall,message:e.message})); process.exit(0); });
server.listen(0, '127.0.0.1', () => { fs.writeFileSync(output, JSON.stringify({listening:true,address:server.address()})); server.close(); });
'@
        [IO.File]::WriteAllText((Join-Path $folder 'probe.cjs'), $js)
        return '--preserve-symlinks-main "' + (Join-Path $folder 'probe.cjs') + '"'
    }
    $terminal = [CodeBadger.Services.WindowsSandboxTerminal]::StartDirectPrepared($NodeExecutable, '', $repo,
        [string[]]@('reviewed.txt'), $prepare, $private)
    $process = $terminal.Process
    try {
        $code = $process.WaitForExit(15000)
        Write-Output "Probe exit: $code"
        $process.Stop(); $terminal.CloseConsole()
        $reader = [IO.StreamReader]::new($terminal.Output)
        Write-Output $reader.ReadToEnd()
        Get-Content -LiteralPath (Join-Path $global:probeHome 'socket-result.json')
    } finally { $terminal.Dispose() }
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
