# Verify the pure request-matching and handoff logic without starting OpenCode or MAUI.
$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\PlanBuildHandoff.cs')
Add-Type -TypeDefinition ("#nullable enable`n" + $source)

if (-not [CodeBadger.Services.PlanBuildHandoff]::IsSameRequest("Add the feature`r`nKeep compatibility", "Add the feature`nKeep compatibility")) {
    throw 'Equivalent requests with different line endings should match.'
}
if ([CodeBadger.Services.PlanBuildHandoff]::IsSameRequest('Implement feature A', 'Implement feature B')) {
    throw 'A plan must not match a different request.'
}
if ([CodeBadger.Services.PlanBuildHandoff]::IsSameRequest('', 'Implement feature A')) {
    throw 'A legacy plan without request provenance must not match.'
}

$request = 'Add the feature while preserving the existing API.'
$plan = '1. Update the implementation. 2. Add regression coverage.'
$prompt = [CodeBadger.Services.PlanBuildHandoff]::CreateWorkerPrompt($request, $plan)
if (-not $prompt.Contains("Original user request:`n$request") -or -not $prompt.Contains("Plan:`n$plan")) {
    throw 'The Worker handoff must include both the original request and plan.'
}
if (-not $prompt.Contains('source of truth') -or -not $prompt.Contains('constraints and acceptance criteria')) {
    throw 'The Worker handoff must explain how to resolve missing details and preserve constraints.'
}

Write-Output 'CodeBadger plan/build handoff tests passed.'
