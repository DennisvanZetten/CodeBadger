# Real NTFS/AppContainer tests. Only the unique synthetic tree below is modified.
$ErrorActionPreference = 'Stop'
$sources = @('SandboxProcessEnvironment.cs', 'WindowsAppContainerProfile.cs', 'SandboxWorkspace.cs',
    'WindowsRepositoryAccessLease.cs', 'WindowsRepositoryAccessGuardian.cs', 'WindowsSandboxProcess.cs') |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "..\Services\$_") }
Add-Type -TypeDefinition ($sources -join [Environment]::NewLine)
$root = Join-Path $env:LOCALAPPDATA ('CodeBadgerDirectTest-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repo'
$private = Join-Path $root 'private'
$null = New-Item -ItemType Directory -Path $repo, $private
$file = Join-Path $repo 'reviewed.txt'
$unselected = Join-Path $repo 'unselected.txt'
$outside = Join-Path $root 'outside.txt'
$secret = Join-Path $repo '.env'
$journal = Join-Path $private 'lease.json'
$profile = $null
$lease = $null
try {
    [IO.File]::WriteAllText($file, 'original')
    [IO.File]::WriteAllText($unselected, 'unselected test only')
    [IO.File]::WriteAllText($outside, 'outside test only')
    [IO.File]::WriteAllText($secret, 'secret test only')
    $original = (Get-Acl -LiteralPath $file).Sddl
    # Use a deliberately protected DACL to cover inheritance-disabled files.
    $acl = Get-Acl -LiteralPath $file
    $acl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $file -AclObject $acl
    $original = (Get-Acl -LiteralPath $file).Sddl
    $profile = [CodeBadger.Services.WindowsAppContainerProfile]::Create()
    $lease = [CodeBadger.Services.WindowsRepositoryAccessLease]::Grant($repo, [string[]]@('reviewed.txt'), $profile.Sid, $journal)
    $command = '/d /c "type reviewed.txt >nul 2>&1 & (if errorlevel 1 exit 10) & type unselected.txt >nul 2>&1 & (if not errorlevel 1 exit 11) & type "' + $outside + '" >nul 2>&1 & (if not errorlevel 1 exit 12) & type .env >nul 2>&1 & (if not errorlevel 1 exit 14) & echo forbidden>new.txt & (if exist new.txt exit 15) & del reviewed.txt >nul 2>&1 & (if not exist reviewed.txt exit 16) & ren reviewed.txt renamed.txt >nul 2>&1 & (if exist renamed.txt exit 17) & echo immediate>reviewed.txt & exit 42"'
    # Same SID: staged prepared hook owns the lease; start failure also cleans up.
    $lease.Dispose(); $lease = $null; $profile.Dispose(); $profile = $null
    $scriptAsset = Join-Path $PSScriptRoot '..\Resources\Raw\sandbox\repository-guardian.ps1'
    Copy-Item -LiteralPath $scriptAsset -Destination $private
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsRepositoryAccessLease.cs') -Destination $private
    $process = [CodeBadger.Services.WindowsSandboxProcess]::StartDirectPrepared(
        (Join-Path ([Environment]::SystemDirectory) 'cmd.exe'), $command, $repo,
        [string[]]@('reviewed.txt'), [IntPtr]::Zero, $null, $private)
    try {
        $code = $process.WaitForExit(15000)
        if ($code -ne 42) { throw "AppContainer direct access/isolation failed ($code)." }
        if ([IO.File]::ReadAllText($file).Trim() -ne 'immediate') { throw 'Direct write did not update the original.' }
    } finally { $process.Dispose() }
    $restored = (Get-Acl -LiteralPath $file).Sddl
    if ($restored.Replace('D:AI', 'D:') -ne $original.Replace('D:AI', 'D:')) { throw 'Original DACL rules/inheritance were not restored.' }
    if (Get-ChildItem -LiteralPath $private -Filter 'lease-*.json') { throw 'Verified cleanup left a journal.' }
    # Simulate failure AFTER access has been granted but BEFORE process creation.
    $badCommand = "invalid`0arguments"
    $failed = $false
    try {
        $null = [CodeBadger.Services.WindowsSandboxProcess]::StartDirectPrepared(
            (Join-Path ([Environment]::SystemDirectory) 'cmd.exe'), $badCommand, $repo,
            [string[]]@('reviewed.txt'), [IntPtr]::Zero, $null, $private)
    } catch { $failed = $true }
    if (-not $failed) { throw 'Invalid startup arguments were accepted.' }
    # Preparation-hook failures occur after grant; unlike invalid arguments they
    # exercise the independent guardian's rollback path.
    $hook = [Func[string,string,string]] { param($sid, $folder) throw 'injected preparation failure' }
    $failed = $false
    try {
        $null = [CodeBadger.Services.WindowsSandboxProcess]::StartDirectPrepared(
            (Join-Path ([Environment]::SystemDirectory) 'cmd.exe'), '', $repo,
            [string[]]@('reviewed.txt'), [IntPtr]::Zero, $hook, $private)
    } catch { $failed = $true }
    if (-not $failed -or (Get-ChildItem -LiteralPath $private -Filter 'lease-*.json')) { throw 'Preparation failure did not roll back access.' }
    # Host pipe loss: launch the guardian directly and close stdin without a
    # normal Stop request. This models abrupt host death without killing the test runner.
    $profile = [CodeBadger.Services.WindowsAppContainerProfile]::Create()
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DirectTestJob
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateJobObjectW(IntPtr security, string name);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
    $jobName = 'Local\CodeBadger.DirectTest.' + [guid]::NewGuid().ToString('N')
    $jobHandle = [DirectTestJob]::CreateJobObjectW([IntPtr]::Zero, $jobName)
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $private 'repository-guardian.ps1'))) { $start.ArgumentList.Add($arg) }
    $guardian = [Diagnostics.Process]::Start($start)
    try {
        $request = @{ operation = 'grant'; root = $repo; files = @('reviewed.txt'); sid = $profile.Sid; journal = $journal; jobName = $jobName }
        $guardian.StandardInput.WriteLine(($request | ConvertTo-Json -Compress)); $guardian.StandardInput.Flush()
        if ($guardian.StandardOutput.ReadLine() -ne 'READY') { throw $guardian.StandardError.ReadToEnd() }
        $guardian.StandardInput.Close()
        if ($guardian.StandardOutput.ReadLine() -ne 'REVOKED' -or -not $guardian.WaitForExit(30000)) { throw 'Host pipe loss did not revoke access.' }
        if (Test-Path -LiteralPath $journal) { throw 'Host pipe loss left a journal.' }
    } finally { $guardian.Dispose(); $null = [DirectTestJob]::CloseHandle($jobHandle); $profile.Dispose(); $profile = $null }
    # Refuse recovery if an original file was replaced while the host was down.
    # Keep the original under another name and explicitly restore it before retrying.
    $profile = [CodeBadger.Services.WindowsAppContainerProfile]::Create()
    $jobName = 'Local\CodeBadger.DirectTest.' + [guid]::NewGuid().ToString('N')
    $jobHandle = [DirectTestJob]::CreateJobObjectW([IntPtr]::Zero, $jobName)
    $guardian = [Diagnostics.Process]::Start($start)
    $saved = Join-Path $repo 'saved-original.txt'
    try {
        $request.sid = $profile.Sid; $request.jobName = $jobName
        $guardian.StandardInput.WriteLine(($request | ConvertTo-Json -Compress)); $guardian.StandardInput.Flush()
        if ($guardian.StandardOutput.ReadLine() -ne 'READY') { throw $guardian.StandardError.ReadToEnd() }
        $guardian.Kill(); $guardian.WaitForExit()
        Move-Item -LiteralPath $file -Destination $saved
        [IO.File]::WriteAllText($file, 'replacement must not be touched')
        $replacementAcl = (Get-Acl -LiteralPath $file).Sddl
        $failed = $false
        try { [CodeBadger.Services.WindowsRepositoryAccessLease]::Recover($journal) } catch { $failed = $true }
        if (-not $failed -or (Get-Acl -LiteralPath $file).Sddl -ne $replacementAcl -or -not (Test-Path -LiteralPath $journal)) {
            throw 'Identity-changing recovery did not fail closed.'
        }
        Remove-Item -LiteralPath $file
        Move-Item -LiteralPath $saved -Destination $file
        [CodeBadger.Services.WindowsRepositoryAccessLease]::Recover($journal)
    } finally { $guardian.Dispose(); $null = [DirectTestJob]::CloseHandle($jobHandle); $profile.Dispose(); $profile = $null }
    # Durable recovery: terminate ONLY our synthetic guardian after READY, then
    # recover the known journal. No repository paths are replaced or followed.
    $profile = [CodeBadger.Services.WindowsAppContainerProfile]::Create()
    $jobName = 'Local\CodeBadger.DirectTest.' + [guid]::NewGuid().ToString('N')
    $jobHandle = [DirectTestJob]::CreateJobObjectW([IntPtr]::Zero, $jobName)
    $guardian = [Diagnostics.Process]::Start($start)
    try {
        $request.sid = $profile.Sid; $request.jobName = $jobName
        $guardian.StandardInput.WriteLine(($request | ConvertTo-Json -Compress)); $guardian.StandardInput.Flush()
        if ($guardian.StandardOutput.ReadLine() -ne 'READY') { throw $guardian.StandardError.ReadToEnd() }
        $guardian.Kill(); $guardian.WaitForExit()
        [CodeBadger.Services.WindowsRepositoryAccessLease]::Recover($journal)
        if (Test-Path -LiteralPath $journal) { throw 'Durable recovery left a journal.' }
    } finally { $guardian.Dispose(); $null = [DirectTestJob]::CloseHandle($jobHandle); $profile.Dispose(); $profile = $null }
    # Linked file cannot grant access to an external alias.
    $link = Join-Path $repo 'linked.txt'
    $null = New-Item -ItemType HardLink -Path $link -Target $outside
    $failed = $false
    try { $null = [CodeBadger.Services.SandboxWorkspace]::Assess($repo, [string[]]@('linked.txt')) } catch { $failed = $true }
    if (-not $failed) { throw 'Hardlink assessment was accepted.' }
    $large = Join-Path $repo 'large.bin'
    $stream = [IO.File]::Create($large); $stream.SetLength(2MB); $stream.Dispose()
    $assessment = [CodeBadger.Services.SandboxWorkspace]::Assess($repo, [string[]]@('large.bin'), [CodeBadger.Services.SandboxWorkspace+Limits]::new(1, 10))
    if ($assessment.Mode.ToString() -ne 'Direct') { throw 'Per-file size threshold did not choose direct mode.' }
    $assessment = [CodeBadger.Services.SandboxWorkspace]::Assess($repo, [string[]]@('large.bin'), [CodeBadger.Services.SandboxWorkspace+Limits]::new(2, 2))
    if ($assessment.Mode.ToString() -ne 'Copy') { throw 'Exact threshold should use copy mode.' }
    $assessment = [CodeBadger.Services.SandboxWorkspace]::Assess($repo, [string[]]@('large.bin'), [CodeBadger.Services.SandboxWorkspace+Limits]::new(4, 1))
    if ($assessment.Mode.ToString() -ne 'Direct') { throw 'Total threshold did not choose direct mode.' }
    Write-Output 'Direct access/isolation, immediate write, thresholds, rollback, host pipe loss, journal recovery and hardlink refusal passed.'
} finally {
    if ($lease) { try { $lease.Dispose() } catch { Write-Warning $_.Exception.ToString() } }
    if ($profile) { $profile.Dispose() }
    Remove-Item -LiteralPath $root -Recurse -Force
}
