param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePath
)

$ErrorActionPreference = "Stop"

$package = (Resolve-Path -LiteralPath $PackagePath).Path
$certificateFile = (Resolve-Path -LiteralPath $CertificatePath).Path
$certificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($certificateFile)

if ($certificate.Subject -ne "CN=CodeBadger Local") {
    throw "Het certificaat heeft een onverwachte uitgever: $($certificate.Subject)"
}

$packageSignature = Get-AuthenticodeSignature -FilePath $package
if (-not $packageSignature.SignerCertificate -or
    $packageSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
    $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or
    $certificate.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow) {
    throw "De MSIX-handtekening is ongeldig of hoort niet bij het meegeleverde CodeBadger-certificaat."
}

$trustedPeople = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
    Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }

if (-not $trustedPeople) {
    $trust = Read-Host "Trust CodeBadger publisher $($certificate.Subject) on this device? (Y/N)"
    if ($trust -notin @("Y", "y", "YES", "yes")) {
        throw "The publisher certificate was not trusted. CodeBadger was not installed."
    }
    Import-Certificate -FilePath $certificateFile -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
}

$packageSignature = Get-AuthenticodeSignature -FilePath $package
if ($packageSignature.Status -notin @("Valid", "NotTrusted") -or
    -not $packageSignature.SignerCertificate -or
    $packageSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw "Windows kon de handtekening van de MSIX niet valideren: $($packageSignature.Status)."
}

$packageDirectory = Split-Path -Parent $package
$x64RuntimeDependencies = @(Get-ChildItem -Path (Join-Path $packageDirectory "Dependencies\x64") -Filter "*.msix" -File -ErrorAction SilentlyContinue)
if ($x64RuntimeDependencies.Count -eq 0) {
    $bundledDependency = Join-Path $packageDirectory "CodeBadger-WindowsAppRuntime-x64.msix"
    if (Test-Path -LiteralPath $bundledDependency) {
        $x64RuntimeDependencies = @((Get-Item -LiteralPath $bundledDependency))
    }
}

if ($x64RuntimeDependencies.Count -gt 0) {
    Add-AppxPackage -Path $package -DependencyPath $x64RuntimeDependencies.FullName -ForceApplicationShutdown
}
else {
    Add-AppxPackage -Path $package -ForceApplicationShutdown
}

Write-Host "CodeBadger is geïnstalleerd voor de huidige gebruiker." -ForegroundColor Green
