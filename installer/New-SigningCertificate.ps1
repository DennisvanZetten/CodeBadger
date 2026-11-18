$ErrorActionPreference = "Stop"

$signingDirectory = Join-Path $env:USERPROFILE "CodeBadgerSigning"
New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null
$pfxPath = Join-Path $signingDirectory "CodeBadger-signing.pfx"
$cerPath = Join-Path $signingDirectory "CodeBadger.cer"
$repositoryCertificatePath = Join-Path $PSScriptRoot "..\Platforms\Windows\CodeBadger.cer"

if ((Test-Path -LiteralPath $pfxPath) -or (Test-Path -LiteralPath $cerPath) -or (Test-Path -LiteralPath $repositoryCertificatePath)) {
    throw "A CodeBadger signing certificate already exists. Refusing to replace it because doing so would break in-place updates."
}

$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=Repo Launcher Local" `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -KeyExportPolicy Exportable `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @(
        "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
        "2.5.29.19={text}CA=FALSE"
    ) `
    -NotAfter (Get-Date).AddYears(5)

$password = Read-Host "Choose a strong password for the private signing key" -AsSecureString

Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
Copy-Item -LiteralPath $cerPath -Destination $repositoryCertificatePath -Force

Write-Host "Signing key created:" -ForegroundColor Green
Write-Host "  Private key (keep secret): $pfxPath"
Write-Host "  Public certificate (safe to distribute): $cerPath"
Write-Host "  Public certificate to commit: $repositoryCertificatePath"
Write-Host "  Certificate subject: $($certificate.Subject)"
Write-Host "  Certificate thumbprint: $($certificate.Thumbprint)"
Write-Host "Never commit or share the PFX file or its password."
