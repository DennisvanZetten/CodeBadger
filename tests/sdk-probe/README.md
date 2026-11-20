# Listenerless OpenCode SDK proof

Run on Windows with PowerShell 7 and Node 24.11 or newer:

```powershell
npm ci --ignore-scripts --no-audit --no-fund --prefix tests/sdk-probe
pwsh -NoProfile -File tests/SandboxLpacSdkProbe.ps1 -Prompt -Edit
pwsh -NoProfile -File installer/Build-SandboxSdk.ps1
pwsh -NoProfile -File tests/SandboxLpacSdkProbe.ps1 -Prompt -Edit -Runner
```

The test stages Node and dependencies into a fresh AppContainer profile. It uses
the production direct LPAC policy, the existing authenticated provider pipe, a
synthetic fake provider and only synthetic repository files. It verifies:

- The SDK starts with no listener (`server.info().urls` is empty).
- Unselected, sensitive and external existing files return permission errors.
- A session receives a fake completion through the provider pipe.
- OpenCode's `edit` tool changes the real selected file.
- Process disposal stops the job and restores the repository ACL lease.

SDK configuration uses explicit `OpenCode.create` options (`config.project: false`),
not CLI-only environment flags. This prevents ancestor configuration discovery
without granting access above the repository. File watcher/FFF are disabled.

`-Runner` tests the production runner and authenticated chat pipe with the generated
runtime archive, including two consecutive prompts. CodeBadger uses this listenerless
runner for direct mode and keeps the TUI for copy mode. A standalone Bun build cannot
locate dynamically resolved native PTY dependencies, so the app packages a pinned
private Node dependency tree instead. Installed MSIX behavior and live provider requests
still require manual validation.
