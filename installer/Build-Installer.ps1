param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePassword,

    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use three numeric parts, for example 1.2.3."
}

$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -lt 1) {
    throw "The MSIX major version must be at least 1."
}
if (@($versionParts | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw "MSIX versions need a major version of at least 1 and each part must be no greater than 65535."
}

$projectDirectory = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectDirectory "RepoLauncher.csproj"
$outputDirectory = Join-Path $projectDirectory "artifacts\installer\$Version"
$appPackagesDirectory = Join-Path $projectDirectory "artifacts\installer\.packages\$Version"
$appPackagesDirectoryRelative = "artifacts/installer/.packages/$Version/"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

try {
    $certificateFile = (Resolve-Path -LiteralPath $CertificatePath).Path
    $signingCertificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
        $certificateFile,
        $CertificatePassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)

    if ($signingCertificate.Subject -ne "CN=Repo Launcher Local") {
        throw "The signing certificate subject must be CN=Repo Launcher Local."
    }

    if (-not $signingCertificate.HasPrivateKey) {
        throw "The PFX does not contain a private signing key."
    }

    $repositoryCertificatePath = Join-Path $projectDirectory "Platforms\Windows\CodeBadger.cer"
    if (-not (Test-Path -LiteralPath $repositoryCertificatePath)) {
        throw "The public signing certificate is missing. Run installer/New-SigningCertificate.ps1 once and commit Platforms/Windows/CodeBadger.cer."
    }

    $repositoryCertificate = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($repositoryCertificatePath)
    if ($repositoryCertificate.Thumbprint -ne $signingCertificate.Thumbprint) {
        throw "The PFX signing key does not match Platforms/Windows/CodeBadger.cer."
    }
    $packagePublisher = [xml](Get-Content -LiteralPath (Join-Path $projectDirectory "Platforms\Windows\Package.appxmanifest") -Raw)
    $packagePublisherName = $packagePublisher.Package.Identity.Publisher
    if ($packagePublisherName -ne $signingCertificate.Subject) {
        throw "Package.appxmanifest publisher '$packagePublisherName' does not match signing certificate subject '$($signingCertificate.Subject)'."
    }
    $repositoryCertificate.Dispose()

    $env:PackageCertificatePassword = $CertificatePassword
    $publishArguments = @(
        $projectFile,
        "--configuration", $Configuration,
        "--framework", "net10.0-windows10.0.19041.0",
        "--runtime", $RuntimeIdentifier,
        "-p:TargetFrameworks=net10.0-windows10.0.19041.0",
        "-p:RestoreSources=https://api.nuget.org/v3/index.json",
        "-p:WindowsPackageType=MSIX",
        "-p:AppxPackageSigningEnabled=true",
        "-p:AppxBundle=Never",
        "-p:AppxPackageDir=$appPackagesDirectoryRelative",
        "-p:AppxPackageSigningTimestampDigestAlgorithm=SHA256",
        "-p:AppxSymbolPackageEnabled=false",
        "-p:AppxPackageSigningTimestampServerUrl=http://timestamp.digicert.com",
        "-p:PackageCertificateKeyFile=$certificateFile",
        "-p:CodeBadgerSigningCertificateThumbprint=$($signingCertificate.Thumbprint)",
        "-p:ApplicationDisplayVersion=$Version",
        "-p:ApplicationVersion=0",
        "-p:PackageLocation=$appPackagesDirectoryRelative"
    )

    & dotnet publish @publishArguments

    if ($LASTEXITCODE -ne 0) {
        throw "MSIX build failed (exit code $LASTEXITCODE)."
    }

    $packageDirectory = Join-Path $appPackagesDirectory "RepoLauncher_${Version}.0_x64_Test"

    if (-not (Test-Path -LiteralPath $packageDirectory)) {
        throw "The generated MSIX package directory was not found under $appPackagesDirectory."
    }

    $generatedPackage = Get-ChildItem -Path $packageDirectory -Filter "*.msix" -File |
        Where-Object { $_.Name -notlike "Microsoft.WindowsAppRuntime.*" } |
        Select-Object -First 1

    if (-not $generatedPackage) {
        throw "The generated application MSIX was not found in $packageDirectory."
    }

    $packageSignature = Get-AuthenticodeSignature -FilePath $generatedPackage.FullName
    if ($packageSignature.Status -notin @("Valid", "NotTrusted") -or
        -not $packageSignature.SignerCertificate -or
        $packageSignature.SignerCertificate.Thumbprint -ne $signingCertificate.Thumbprint) {
        throw "The generated MSIX signature is invalid or does not match the configured CodeBadger certificate."
    }

    Copy-Item -LiteralPath $generatedPackage.FullName -Destination (Join-Path $outputDirectory "CodeBadger.msix") -Force
    [IO.File]::WriteAllBytes(
        (Join-Path $outputDirectory "CodeBadger.cer"),
        $signingCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($generatedPackage.FullName)
    try {
        $manifestEntry = $archive.Entries | Where-Object FullName -eq "AppxManifest.xml" | Select-Object -First 1
        if (-not $manifestEntry) {
            throw "The generated MSIX does not contain AppxManifest.xml."
        }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$packageManifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $namespaces = [System.Xml.XmlNamespaceManager]::new($packageManifest.NameTable)
    $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $dependencyNode = $packageManifest.SelectSingleNode("/f:Package/f:Dependencies/f:PackageDependency", $namespaces)
    if (-not $dependencyNode) {
        throw "The generated MSIX does not declare a Windows App Runtime package dependency."
    }
    $dependencyName = $dependencyNode.GetAttribute("Name")
    if ([string]::IsNullOrWhiteSpace($dependencyName)) {
        throw "The generated MSIX does not declare a Windows App Runtime package dependency."
    }

    $dependencyPath = Join-Path $packageDirectory "Dependencies\x64\$dependencyName.msix"
    if (-not (Test-Path -LiteralPath $dependencyPath)) {
        throw "The Windows App Runtime x64 dependency was not found at $dependencyPath."
    }

    $dependencySignature = Get-AuthenticodeSignature -FilePath $dependencyPath
    if ($dependencySignature.Status -ne "Valid" -or $dependencySignature.SignerCertificate.Subject -ne "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US") {
        throw "The Windows App Runtime dependency signature could not be verified."
    }

    $runtimePackage = [System.IO.Compression.ZipFile]::OpenRead($dependencyPath)
    try {
        $runtimeManifestEntry = $runtimePackage.Entries | Where-Object FullName -eq "AppxManifest.xml" | Select-Object -First 1
        if (-not $runtimeManifestEntry) {
            throw "The Windows App Runtime dependency does not contain AppxManifest.xml."
        }
        $runtimeReader = [IO.StreamReader]::new($runtimeManifestEntry.Open())
        try {
            [xml]$runtimeManifest = $runtimeReader.ReadToEnd()
        }
        finally {
            $runtimeReader.Dispose()
        }
    }
    finally {
        $runtimePackage.Dispose()
    }

    $runtimeIdentity = $runtimeManifest.Package.Identity
    if ($runtimeIdentity.Name -ne $dependencyName -or
        $runtimeIdentity.ProcessorArchitecture -ne "x64" -or
        [version]$runtimeIdentity.Version -lt [version]$dependencyNode.GetAttribute("MinVersion")) {
        throw "The packaged Windows App Runtime dependency does not satisfy the package manifest dependency."
    }

    New-Item -ItemType Directory -Path (Join-Path $outputDirectory "Dependencies\x64") -Force | Out-Null
    $dependencyAsset = Join-Path $outputDirectory "CodeBadger-WindowsAppRuntime-x64.msix"
    Copy-Item -LiteralPath $dependencyPath -Destination (Join-Path $outputDirectory "Dependencies\x64\$dependencyName.msix") -Force
    Copy-Item -LiteralPath $dependencyPath -Destination $dependencyAsset -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Install-Installer.ps1") -Destination $outputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Install-CodeBadger.ps1") -Destination $outputDirectory -Force

    Write-Host "Signed CodeBadger $Version installer created in: $outputDirectory" -ForegroundColor Green
}
finally {
    Remove-Item Env:\PackageCertificatePassword -ErrorAction SilentlyContinue
    if ($repositoryCertificate) {
        $repositoryCertificate.Dispose()
    }
    if ($signingCertificate) {
        $signingCertificate.Dispose()
    }
}
