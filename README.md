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

## Local Mac Catalyst debugging

The Mac Catalyst target supports local Debug builds on macOS with Xcode and the .NET MAUI workload. Debug builds do not enable the App Sandbox, so CodeBadger can access selected repositories, user agent configuration, and installed command-line tools (`git`, `gh`, `az`, and AI agents). Release builds keep the sandboxed entitlements for Mac App Store compatibility; repository access and external tool launching need a separate design before distributing such builds.

Select the repository folder with the native folder picker when opening a repository or choosing a clone/create destination. The app adds common Homebrew, npm, Node, and agent install folders to its process `PATH`; if a tool is installed elsewhere, launch CodeBadger from a shell whose `PATH` includes it.

Use the terminal icon on a repository tab to open Terminal.app in that repository and launch its configured agent. This opens a normal external zsh session; terminal input/output is not embedded in CodeBadger.

Build from a Mac with:

```bash
dotnet workload install maui
dotnet build CodeBadger.csproj -f net10.0-maccatalyst -c Debug
```

The Windows AppContainer sandbox, Windows Terminal, and application updater remain Windows-only.

## Local Windows build

### OpenCode sandbox modes

Sandbox mode is selected from the **reviewed file selection**, not the size of excluded
repository content. At or below the configured per-file and total limits, files are copied
and changes remain subject to review. Exceeding either size limit selects direct mode.
Entry/depth limits and unsafe paths are safety errors, not triggers for direct mode.

Direct mode currently supports **editing existing selected files only**. Changes are immediate;
there is no review copy. Creation, deletion, rename and atomic-replacement saves are blocked.
Unselected files do not receive access. Symlinks, junctions, hardlinks and unsupported ACLs
are refused. It requires system-installed PowerShell 7 and Windows support for FSCTL process
mitigation; unsupported systems fail closed. Direct mode does not add MCP or provider compatibility.

Large selections use a **listenerless SDK chat** in CodeBadger, not the OpenCode TUI.
Small selections retain the existing OpenCode terminal. The app includes a pinned SDK 2.3.2 /
Node 24.11.0 runtime archive built by `installer/Build-SandboxSdk.ps1`; its archive and Node
hashes are checked before repository access is granted. No SDK download or dependency
installation occurs at sandbox startup. The initial local build needs npm/network access to
prepare this generated archive. Release builds package it in the signed MSIX.

The chat uses a separate AppContainer-authenticated pipe for bounded prompts, answers and
tool status. Provider requests still use the existing host-owned approval/key broker.
Shell execution is denied in direct chat; existing selected files can be edited immediately.
Live Bonzai requests, MCP integration and installed-MSIX behavior have not been validated
by the synthetic tests; the provider compatibility limits still apply.

Run `tests/SandboxLpacSdkProbe.ps1 -Prompt -Edit -Runner` to test the packaged runtime,
two chat prompts, fake provider completion, direct file editing and stop/ACL cleanup.
Run `tests/SandboxDirectRepository.ps1` for isolation, thresholds and crash/recovery checks.

A less-privileged AppContainer (LPAC) opts out of broad ALL APPLICATION PACKAGES access.
A separate host guardian holds file/directory handles and journals the original DACL and
integrity labels before grants. On stop or host termination it stops all sandbox descendants,
removes access and verifies restoration. Windows may update ACL auto-inheritance bookkeeping;
the original access rules, ACE inheritance flags and protection remain unchanged. Concurrent
ACL edits are not overwritten: the sandbox ACE is removed, recovery is reported as incomplete,
and the journal is retained. Pending journals must recover before another direct session.
Only one direct session per CodeBadger recovery directory is supported.

**Recovery limitation:** ACLs are persistent, not a kernel-managed expiring lease. Simultaneous
guardian/host termination or power loss leaves journals for recovery at the next app startup;
immediate ACL restoration during a machine outage cannot be guaranteed. Direct mode is not a
solution when that stronger guarantee is required. As with AppContainer generally, Windows
runtime resources and the sandbox's own profile remain accessible; this is not a literal
filesystem containing only the repository.

For a local build using the existing long-term PFX and password:

```powershell
./installer/Build-Installer.ps1 `
  -Version "1.0.1" `
  -CertificatePath "$env:USERPROFILE\CodeBadgerSigning\CodeBadger-signing.pfx" `
  -CertificatePassword ([Net.NetworkCredential]::new("", (Read-Host "PFX password" -AsSecureString)).Password)
```

The package is signed with a self-signed certificate. Windows customers must explicitly trust its public certificate; the certificate is not a publicly trusted commercial code-signing identity. The first signed release must be downloaded and installed manually; subsequent releases can be offered from inside that release of the app.
