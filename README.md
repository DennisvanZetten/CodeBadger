# CodeBadger

CodeBadger is a Windows app for managing repositories, coding agents, MCP servers, skills, and Git workflows. It is built with .NET MAUI Blazor Hybrid and MudBlazor. Windows releases are packaged as MSIX and signed with a stable self-signed certificate.

## Publishing a GitHub release

GitHub Actions builds and signs a Windows MSIX whenever a version tag such as `v1.2.0` is pushed. The workflow attaches the MSIX, public certificate, Windows App Runtime dependency, and a `CodeBadger-Setup.zip` containing the full installer.

Create one stable self-signed certificate for all releases. Run this once in PowerShell on Windows:

```powershell
./installer/New-SigningCertificate.ps1
```

Run this script once before making a release commit. It creates the public certificate at `Platforms/Windows/CodeBadger.cer` and keeps the private PFX at `%USERPROFILE%\CodeBadgerSigning\CodeBadger-signing.pfx`. Review and commit only the public `.cer` file; never commit or share the PFX. Keep the PFX and its password private and securely backed up. Upload its base64-encoded contents and PFX password as repository Actions secrets named `CODE_SIGNING_PFX_BASE64` and `CODE_SIGNING_PFX_PASSWORD` (Repository Settings → Secrets and variables → Actions → New repository secret). To copy a base64 value without line breaks, run:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\CodeBadgerSigning\CodeBadger-signing.pfx")) | Set-Clipboard
```

Commit `Platforms/Windows/CodeBadger.cer`. The build verifies that it matches the PFX and the manifest's publisher identity.

Push the code, certificate, and workflow commit to GitHub first. Then tag that commit with a new version greater than any existing release. For example:

```powershell
git tag v1.0.1
git push origin main
git push origin v1.0.1
```

Monitor the run under the repository's Actions tab. When it succeeds, open Releases and share the `CodeBadger-Setup.zip` asset with customers. The zip contains the MSIX, public certificate, Windows App Runtime dependency, and installer script. In PowerShell, customers extract it and run `powershell -ExecutionPolicy Bypass -File .\Install-CodeBadger.ps1`. They will be asked to trust the self-signed publisher certificate the first time. For future releases, CodeBadger checks at startup and asks whether to download and install the update. Choosing "Not now" hides that version's prompt; a later version will still be offered.

**Migration note:** previous local builds used temporary certificates. Install the first GitHub release manually from its setup ZIP. That release pins the permanent signing certificate; future versions can then be offered in-app.

## Local Windows build

For a local build using the existing long-term PFX and password:

```powershell
./installer/Build-Installer.ps1 `
  -Version "1.0.1" `
  -CertificatePath "$env:USERPROFILE\CodeBadgerSigning\CodeBadger-signing.pfx" `
  -CertificatePassword ([Net.NetworkCredential]::new("", (Read-Host "PFX password" -AsSecureString)).Password)
```

The package is signed with a self-signed certificate. Windows customers must explicitly trust its public certificate; the certificate is not a publicly trusted commercial code-signing identity. The first signed release must be downloaded and installed manually; subsequent releases can be offered from inside that release of the app.
