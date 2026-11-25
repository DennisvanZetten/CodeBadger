# Exercise the actual cost-meter calculation without starting OpenCode or MAUI.
$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Components\Shared\RepositoryWorkspace.razor')
$match = [regex]::Match($source, '(?s)    private static decimal GetSessionCostProgress\(.*?;')
if (-not $match.Success) { throw 'Cost progress calculation was not found.' }
$method = $match.Value.Replace('private static', 'public static')
Add-Type -TypeDefinition ("public static class SessionCostProgressTest {`n" + $method + "`n}")
$cases = @(
    @(0.42d, 2d, 21d),
    @(0d, 2d, 0d),
    @(0.47d, 0d, 0d),
    @(0.47d, -1d, 0d),
    @(1.4d, 2d, 70d),
    @(1.7d, 2d, 85d),
    @(2d, 2d, 100d),
    @(3d, 2d, 100d),
    @([decimal]::MaxValue, 1d, 100d)
)
foreach ($case in $cases) {
    $actual = [SessionCostProgressTest]::GetSessionCostProgress($case[0], $case[1])
    if ($actual -ne $case[2]) { throw "Expected $($case[2]), got $actual for cost $($case[0]) / limit $($case[1])." }
}
Write-Output 'CodeBadger session cost progress tests passed.'
