$ErrorActionPreference = "Stop"

$installScript = Join-Path $PSScriptRoot "Install-Installer.ps1"
& $installScript `
    -PackagePath (Join-Path $PSScriptRoot "CodeBadger.msix") `
    -CertificatePath (Join-Path $PSScriptRoot "CodeBadger.cer")
