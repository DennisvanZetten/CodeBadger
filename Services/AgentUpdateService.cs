using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeBadger.Models;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed record AgentUpdateStatus(
    string Agent,
    string? InstalledVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    bool IsCheckVerified,
    bool IsConfigImpactVerified,
    bool IsMajorUpgrade,
    bool ConfigChangeMentioned,
    string ReleaseNotesUrl,
    string ExecutablePath);

public sealed record OpenCodeUpdateProcessCheck(bool HasInteractiveSession, bool CouldVerify);

public sealed class AgentUpdateService(ILogger<AgentUpdateService> logger)
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly (string Agent, string Command, string Repository, string NotesUrl)[] Agents =
    [
            (AgentTools.OpenCode, "opencode", "anomalyco/opencode", "https://opencode.ai/changelog"),
        (AgentTools.ClaudeCode, "claude", "anthropics/claude-code", "https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md"),
        (AgentTools.Codex, "codex", "openai/codex", "https://github.com/openai/codex/releases"),
        (AgentTools.Hermes, "hermes", "NousResearch/hermes-agent", "https://github.com/NousResearch/hermes-agent/releases")
    ];
    private static readonly Regex VersionPattern = new(@"(?<!\d)(\d+(?:\.\d+){1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ConfigChangePattern = new(@"\b(breaking|migration|migrat(e|ion)|config(uration)?|deprecated|deprecat(e|ion)|removed|rename)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly object _sync = new();
    private IReadOnlyList<AgentUpdateStatus> _statuses = [];
    private bool _hasChecked;
    private bool _isChecking;
    private Task? _checkTask;

    public event Action? UpdatesChanged;

    public IReadOnlyList<AgentUpdateStatus> Statuses
    {
        get { lock (_sync) return _statuses; }
    }

    public bool HasUpdates => Statuses.Any(status => status.IsUpdateAvailable);
    public bool HasChecked { get { lock (_sync) return _hasChecked; } }
    public bool IsChecking { get { lock (_sync) return _isChecking; } }

    public async Task<OpenCodeUpdateProcessCheck> CheckOpenCodeProcessesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new OpenCodeUpdateProcessCheck(false, true);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            const string script = "$items = @(Get-CimInstance Win32_Process -Filter \"Name='opencode.exe'\" | Select-Object ProcessId, CommandLine); ConvertTo-Json -InputObject $items -Compress -Depth 3";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script }
            });
            if (process is null)
                return new OpenCodeUpdateProcessCheck(false, false);

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var error = await errorTask;
                logger.LogWarning("Could not inspect OpenCode processes (exit code {ExitCode}): {Error}", process.ExitCode, error);
                return new OpenCodeUpdateProcessCheck(false, false);
            }

            var output = await outputTask;
            if (string.IsNullOrWhiteSpace(output))
                return new OpenCodeUpdateProcessCheck(false, true);
            using var document = JsonDocument.Parse(output);
            var processes = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            foreach (var item in processes)
            {
                var commandLine = item.TryGetProperty("CommandLine", out var commandLineElement)
                    ? commandLineElement.GetString()
                    : null;
                if (commandLine is null)
                    return new OpenCodeUpdateProcessCheck(false, false);

                // CodeBadger uses short-lived `opencode api get/post ...` processes to
                // query the managed service. Those are not interactive sessions and
                // must not prevent the user from upgrading OpenCode.
                // `serve --service` is OpenCode's background service, not a user TUI.
                // Treating it as an open session made the update guard permanently fire.
                if (!Regex.IsMatch(commandLine, @"(?i)(?:^|\s)api\s+(?:get|post|put|patch|delete)\s+") &&
                    !Regex.IsMatch(commandLine, @"(?i)(?:^|\s)serve\s+--service(?:\s|$)"))
                    return new OpenCodeUpdateProcessCheck(true, true);
            }

            return new OpenCodeUpdateProcessCheck(false, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Timed out while checking whether OpenCode has active sessions.");
            return new OpenCodeUpdateProcessCheck(false, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not check whether OpenCode has active sessions.");
            return new OpenCodeUpdateProcessCheck(false, false);
        }
    }

    public async Task CheckForUpdatesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_isChecking)
                return;
            if (refresh)
            {
                _hasChecked = false;
                _statuses = [];
            }
            if (_hasChecked)
                return;
            _isChecking = true;
            _checkTask = CheckCoreAsync(cancellationToken);
        }
        UpdatesChanged?.Invoke();
        await _checkTask;
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            if (!OperatingSystem.IsWindows())
                return;

            var tasks = Agents.Select(agent => CheckAgentAsync(agent, cancellationToken));
            var results = await Task.WhenAll(tasks);
            lock (_sync)
            {
                _statuses = results.Where(status => status is not null).Cast<AgentUpdateStatus>().ToArray();
                _hasChecked = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check coding agent updates.");
        }
        finally
        {
            lock (_sync) _isChecking = false;
            UpdatesChanged?.Invoke();
        }
    }

    public async Task<string> CreateConfigBackupAsync(string agent, LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var files = GetConfigFiles(agent, settings)
            .Concat(GetConfigDirectories(agent, settings).SelectMany(EnumerateBackupFiles))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBadger", "AgentBackups");
        Directory.CreateDirectory(backupDirectory);
        var archivePath = Path.Combine(backupDirectory, $"{SanitizeFileName(agent)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = Path.GetFullPath(file).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            await using var entryStream = entry.Open();
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            await input.CopyToAsync(entryStream, cancellationToken);
        }
        if (files.Length == 0)
            archive.CreateEntry("No existing agent configuration files were found.txt");
        return archivePath;
    }

    public void StartUpdateInTerminal(string agent, string? executablePath = null)
    {
        var command = agent switch
        {
            AgentTools.OpenCode => BuildOpenCodeUpdateCommand(executablePath),
            AgentTools.ClaudeCode => "claude update",
            AgentTools.Codex => GetCodexUpdateCommand(),
            AgentTools.Hermes => "hermes update --backup",
            _ => throw new InvalidOperationException("Unsupported agent update.")
        };
        var startInfo = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false };
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add("pwsh.exe");
        startInfo.ArgumentList.Add("-NoExit");
        var script = agent == AgentTools.OpenCode
            ? command
            : $"{command}; if ($LASTEXITCODE -eq 0) {{ Write-Host 'Update command completed. Review the output above; restart CodeBadger if the updated executable is still in use.' -ForegroundColor Green }} else {{ Write-Host 'Update command failed or did not complete. Review the output above.' -ForegroundColor Yellow }}";
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Could not start Windows Terminal.");
    }

    private static string BuildOpenCodeUpdateCommand(string? executablePath)
    {
        var executable = string.IsNullOrWhiteSpace(executablePath)
            ? FindExecutableCandidatesAsync("opencode", CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault()
            : executablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The OpenCode executable could not be found.");
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic OpenCode updates require its native Windows executable. This installation type is not supported safely.");
        var installMethod = GetOpenCodeInstallMethod(executable);
        if (installMethod == "unknown")
            throw new InvalidOperationException("Could not determine the selected OpenCode installation method. No update was started, to avoid installing a second copy.");
        var installationRoot = GetNodeRootForExecutable(executable);
        if (installMethod == "npm" && installationRoot is null)
            throw new InvalidOperationException("The npm install directory for this OpenCode executable could not be identified. No update was started, to avoid updating a different Node installation.");
        var updateCommand = GetOpenCodeInstallerUpdateCommand(executable, installMethod);

        var escapedPath = executable.Replace("'", "''", StringComparison.Ordinal);
        return "$ErrorActionPreference = 'Continue'; " +
            $"$openCode = '{escapedPath}'; " +
            "$updateExit = 1; " +
            "$managedServiceStopped = $false; " +
            "try { " +
                "& $openCode service stop; " +
                "$managedServiceStopped = $LASTEXITCODE -eq 0; " +
                "if (-not $managedServiceStopped) { throw 'OpenCode background service did not stop cleanly; update cancelled.' }; " +
                "$openCodeProcesses = @(Get-CimInstance Win32_Process -Filter \"Name='opencode.exe'\" -ErrorAction Stop); " +
                "$updatedBinary = [System.IO.Path]::GetFullPath($openCode); " +
                "foreach ($openCodeProcess in $openCodeProcesses) { " +
                    "$commandLine = [string]$openCodeProcess.CommandLine; " +
                    "$processPath = [string]$openCodeProcess.ExecutablePath; " +
                    "if ($processPath -and -not [string]::Equals([System.IO.Path]::GetFullPath($processPath), $updatedBinary, [StringComparison]::OrdinalIgnoreCase)) { continue }; " +
                    "if ($commandLine -match '(?i)(?:^|\\s)api\\s+(?:get|post|put|patch|delete)\\s+' -or $commandLine -match '(?i)(?:^|\\s)upgrade(?:\\s|$)') { continue }; " +
                    "Stop-Process -Id $openCodeProcess.ProcessId -Force -ErrorAction SilentlyContinue; " +
                "}; " +
                "Start-Sleep -Seconds 2; " +
                updateCommand + "; " +
                "$updateExit = $LASTEXITCODE; " +
            "} finally { " +
                "if ($managedServiceStopped) { & $openCode service start; " +
                "if ($LASTEXITCODE -ne 0) { Write-Host 'OpenCode update finished, but its background service could not be restarted. Run: opencode service start' -ForegroundColor Yellow } } " +
            "}; " +
            "if ($updateExit -eq 0) { Write-Host 'OpenCode was updated and its background service restart was requested.' -ForegroundColor Green } " +
            "else { Write-Host 'OpenCode update failed. The background service restart was requested; review the output above.' -ForegroundColor Yellow }";
    }

    private static string GetOpenCodeInstallMethod(string executablePath)
    {
        var normalized = executablePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME") ??
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "nvm");
        if (normalized.Contains("\\nvm4w\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase) && IsUnderDirectory(executablePath, nvmHome))
            return "npm";

        var nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
        if (!string.IsNullOrWhiteSpace(nvmSymlink) && IsUnderDirectory(executablePath, nvmSymlink))
            return "npm";

        var nodeRoot = GetNodeRootForExecutable(executablePath);
        var npmRoot = RunNpmGlobalRoot(executablePath);
        if (!string.IsNullOrWhiteSpace(npmRoot))
        {
            var binaryPaths = new[]
            {
                Path.Combine(npmRoot, "@opencode", "cli", "bin", "opencode.exe"),
                Path.Combine(npmRoot, "opencode-ai", "bin", "opencode.exe"),
                Path.Combine(npmRoot, "opencode-ai", "node_modules", "opencode-windows-x64", "bin", "opencode.exe")
            };
            if (binaryPaths.Any(binaryPath => SamePhysicalFile(executablePath, binaryPath)))
                return "npm";
        }

        var globalManagerPrefix = RunNpmGlobalPrefix(executablePath);
        var nodeRootForManager = nodeRoot;
        var managerPaths = new (string Command, string PackageDirectory, string Method)[]
        {
            ("pnpm.cmd", Path.Combine("global", "5", "node_modules", "@opencode", "cli", "bin", "opencode.exe"), "pnpm"),
            ("bun.exe", Path.Combine("node_modules", "@opencode", "cli", "bin", "opencode.exe"), "bun"),
            ("yarn.cmd", Path.Combine("node_modules", "@opencode", "cli", "bin", "opencode.exe"), "yarn"),
            ("vp.exe", Path.Combine("node_modules", "@opencode", "cli", "bin", "opencode.exe"), "vp")
        };
        foreach (var manager in managerPaths)
        {
            var managerPath = FindCommandPath(manager.Command);
            if (string.IsNullOrWhiteSpace(managerPath))
                continue;
            var managerRoot = Path.GetDirectoryName(managerPath)!;
            var possibleRoots = new[]
            {
                nodeRootForManager ?? string.Empty,
                managerRoot,
                globalManagerPrefix ?? string.Empty,
                globalManagerPrefix is null || nodeRootForManager is null ? string.Empty : Path.Combine(globalManagerPrefix, Path.GetFileName(nodeRootForManager))
            };
            foreach (var root in possibleRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                if (SamePhysicalFile(executablePath, Path.Combine(root, manager.PackageDirectory)))
                    return manager.Method;
            }
        }

        var commandPath = FindCommandPath("opencode.cmd");
        if (!string.IsNullOrWhiteSpace(commandPath))
        {
            var commandDirectory = Path.GetDirectoryName(commandPath)!;
            var commandBinaries = new[]
            {
                Path.Combine(commandDirectory, "node_modules", "@opencode", "cli", "bin", "opencode.exe"),
                Path.Combine(commandDirectory, "node_modules", "opencode-ai", "bin", "opencode.exe"),
                Path.Combine(commandDirectory, "node_modules", "opencode-ai", "node_modules", "opencode-windows-x64", "bin", "opencode.exe")
            };
            if (commandBinaries.Any(binary => SamePhysicalFile(executablePath, binary)))
                return "npm";
        }

        var scoopRoot = Environment.GetEnvironmentVariable("SCOOP");
        if (!string.IsNullOrWhiteSpace(scoopRoot) && IsUnderDirectory(executablePath, Path.Combine(scoopRoot, "apps", "opencode")))
            return "scoop";
        if (IsUnderDirectory(executablePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "opencode")))
            return "scoop";

        if (IsUnderDirectory(executablePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages")))
            return "winget";

        var standaloneDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin")
        };
        if (standaloneDirs.Any(directory => SamePhysicalFile(executablePath, Path.Combine(directory, "opencode.exe"))))
            return "curl";

        if (nodeRoot is not null && IsUnderDirectory(executablePath, nodeRoot))
            return "npm";
        return "unknown";
    }

    private static string? FindCommandPath(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { command }
            });
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
                return null;
            return stdout.GetAwaiter().GetResult().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch { return null; }
    }

    private static string GetOpenCodeInstallerUpdateCommand(string executablePath, string method)
    {
        var prefix = GetNpmPrefixForExecutable(executablePath);
        var escapedPrefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Replace("'", "''", StringComparison.Ordinal);
        return method switch
        {
            "npm" when escapedPrefix is not null => $"npm install --global --prefix '{escapedPrefix}' @opencode/cli@latest",
            "npm" => "npm install --global @opencode/cli@latest",
            "pnpm" => "pnpm add --global @opencode/cli@latest",
            "bun" => "bun install --global @opencode/cli@latest",
            "yarn" => "yarn global add @opencode/cli@latest",
            "winget" => "winget upgrade --id SST.opencode --exact",
            "scoop" => "scoop update opencode",
            "brew" => "brew upgrade opencode",
            "vp" => "vp install --global @opencode/cli@latest",
            "curl" => "& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing https://opencode.ai/install.ps1).Content))",
            _ => throw new InvalidOperationException("Could not determine how this OpenCode instance was installed; refusing to install another copy.")
        };
    }

    private static string? GetNpmPrefixForExecutable(string executablePath)
    {
        var nodeRoot = GetNodeRootForExecutable(executablePath);
        if (nodeRoot is not null)
        {
            var prefix = RunNpmGlobalPrefix(executablePath);
            if (!string.IsNullOrWhiteSpace(prefix))
                return prefix;

            // NVM installs each Node version under its own directory. Setting this
            // exact prefix updates that Node version instead of PATH's current Node.
            if (Directory.Exists(Path.Combine(nodeRoot, "node_modules")))
                return nodeRoot;
        }
        return null;
    }

    private static string GetPackageManagerUpdateCommand(string command, string executablePath, string arguments)
    {
        var nodeRoot = GetNodeRootForExecutable(executablePath);
        var managerPath = nodeRoot is not null
            ? Path.Combine(nodeRoot, command + ".cmd")
            : FindCommandPath(command + ".cmd") ?? command;
        var globalPrefix = RunNpmGlobalPrefix(executablePath);
        if (!string.IsNullOrWhiteSpace(globalPrefix))
            arguments = command.Equals("pnpm", StringComparison.OrdinalIgnoreCase)
                ? $"{arguments} --global-dir '{globalPrefix.Replace("'", "''", StringComparison.Ordinal)}'"
                : $"{arguments} --global-dir '{globalPrefix.Replace("'", "''", StringComparison.Ordinal)}'";
        return $"& '{managerPath.Replace("'", "''", StringComparison.Ordinal)}' {arguments}";
    }

    private static string? GetNodeRootForExecutable(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Combine(directory, "node_modules", "@opencode", "cli")) ||
                Directory.Exists(Path.Combine(directory, "node_modules", "opencode-ai")))
                return directory;
            var parent = Directory.GetParent(directory);
            if (parent is null || string.Equals(parent.FullName, directory, StringComparison.OrdinalIgnoreCase))
                break;
            directory = parent.FullName;
        }
        return null;
    }

    private static string GetNpmCommandForExecutable(string executablePath)
    {
        var nodeRoot = GetNodeRootForExecutable(executablePath);
        if (nodeRoot is not null)
        {
            foreach (var name in new[] { "npm.cmd", "npm.exe" })
            {
                var candidate = Path.Combine(nodeRoot, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return FindCommandPath("npm.cmd") ?? "npm";
    }

    private static string? RunNpmGlobalRoot(string executablePath)
    {
        try
        {
            var nodeRoot = GetNodeRootForExecutable(executablePath);
            var npmCommand = GetNpmCommandForExecutable(executablePath);
            var args = nodeRoot is null
                ? new[] { "root", "--global" }
                : new[] { "--prefix", nodeRoot, "root", "--global" };
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = npmCommand,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { }
            });
            if (process is null)
                return null;
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
                return null;
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    private static string? RunNpmGlobalPrefix(string executablePath)
    {
        try
        {
            var nodeRoot = GetNodeRootForExecutable(executablePath);
            var npmCommand = GetNpmCommandForExecutable(executablePath);
            var args = new[] { "config", "get", "prefix" };
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = npmCommand,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { }
            });
            if (process is null)
                return null;
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
                return null;
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    private static bool SamePhysicalFile(string left, string right)
    {
        if (!File.Exists(left) || !File.Exists(right))
            return false;
        try
        {
            return OpenCodeExecutableLocator.AreSameFile(left, right);
        }
        catch { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
    }

    private static bool IsUnderDirectory(string file, string directory)
    {
        var fullFile = Path.GetFullPath(file);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCodexUpdateCommand()
    {
        var executable = FindExecutableCandidatesAsync("codex", CancellationToken.None).GetAwaiter().GetResult().FirstOrDefault();
        if (executable is null)
            throw new FileNotFoundException("Codex could not be found on PATH.");
        var path = executable.Replace("'", "''", StringComparison.Ordinal);
        return $"& '{path}' upgrade; if ($LASTEXITCODE -ne 0) {{ Write-Host 'Use the install method shown by Codex, such as its official installer, npm, or Homebrew.' -ForegroundColor Yellow }}";
    }

    private async Task<AgentUpdateStatus?> CheckAgentAsync(
        (string Agent, string Command, string Repository, string NotesUrl) definition,
        CancellationToken cancellationToken)
    {
        string? executable = null;
        string installedText;
        try
        {
            var executables = await FindExecutableCandidatesAsync(definition.Command, cancellationToken);
            if (executables.Count == 0)
                return null;

            installedText = string.Empty;
            foreach (var candidate in executables)
            {
                executable = candidate;
                installedText = await RunVersionAsync(candidate, cancellationToken);
                if (ExtractVersion(installedText) is not null)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not identify installed {Agent}.", definition.Agent);
            return null;
        }
        if (executable is null)
            return null;

        try
        {
            var installedVersion = ExtractVersion(installedText);
            if (installedVersion is null)
                return new AgentUpdateStatus(definition.Agent, null, null, false, false, false, false, false, definition.NotesUrl, executable!);

            var release = definition.Agent == AgentTools.ClaudeCode
                ? await GetLatestClaudeCodeReleaseAsync(installedVersion, cancellationToken)
                : await GetLatestReleaseAsync(definition.Repository, installedVersion, cancellationToken);
            if (release is null)
                return new AgentUpdateStatus(definition.Agent, installedVersion, null, false, false, false, false, false, definition.NotesUrl, executable);

            var latestVersion = ExtractVersion(release.Value.Version);
            if (latestVersion is null || !Version.TryParse(installedVersion, out var current) || !Version.TryParse(latestVersion, out var latest))
                return new AgentUpdateStatus(definition.Agent, installedVersion, null, false, false, false, false, false, definition.NotesUrl, executable);

            var isUpdateAvailable = latest > current;
            var isMajorUpgrade = latest.Major > current.Major;
            var notesUrl = string.IsNullOrWhiteSpace(release.Value.Url) ? definition.NotesUrl : release.Value.Url;
            var coversInstalledVersion = release.Value.ReachedInstalledVersion || string.Equals(latestVersion, installedVersion, StringComparison.OrdinalIgnoreCase);
            return new AgentUpdateStatus(definition.Agent, installedVersion, latestVersion, isUpdateAvailable,
                true, coversInstalledVersion, isMajorUpgrade,
                isMajorUpgrade || ConfigChangePattern.IsMatch(release.Value.Notes), notesUrl, executable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not check update for {Agent}.", definition.Agent);
            var installedVersion = ExtractVersion(installedText);
            return installedVersion is null
                ? null
                : new AgentUpdateStatus(definition.Agent, installedVersion, null, false, false, false, false, false, definition.NotesUrl, executable);
        }
    }

    private static async Task<(string Version, string Url, string Notes, bool ReachedInstalledVersion)?> GetLatestClaudeCodeReleaseAsync(
        string installedVersion, CancellationToken cancellationToken)
    {
        using var packageResponse = await Http.GetAsync("https://registry.npmjs.org/@anthropic-ai%2Fclaude-code/latest", cancellationToken);
        packageResponse.EnsureSuccessStatusCode();
        await using var packageStream = await packageResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var package = await JsonDocument.ParseAsync(packageStream, cancellationToken: cancellationToken);
        if (!package.RootElement.TryGetProperty("version", out var versionElement) || versionElement.GetString() is not { Length: > 0 } latestVersion)
            return null;

        using var notesResponse = await Http.GetAsync("https://raw.githubusercontent.com/anthropics/claude-code/main/CHANGELOG.md", cancellationToken);
        notesResponse.EnsureSuccessStatusCode();
        var allNotes = await notesResponse.Content.ReadAsStringAsync(cancellationToken);
        var latestNotes = GetChangelogRange(allNotes, latestVersion, installedVersion);
        var reachedInstalledVersion = latestNotes is not null || string.Equals(latestVersion, installedVersion, StringComparison.OrdinalIgnoreCase);
        return (latestVersion, "https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md", latestNotes ?? string.Empty, reachedInstalledVersion);
    }

    private static string? GetChangelogRange(string changelog, string latestVersion, string installedVersion)
    {
        var latest = FindVersionHeader(changelog, latestVersion);
        if (latest < 0)
            return null;
        var installed = FindVersionHeader(changelog, installedVersion, latest + 1);
        return changelog[latest..(installed >= 0 ? installed : changelog.Length)];
    }

    private static int FindVersionHeader(string changelog, string version, int startIndex = 0)
    {
        var escaped = Regex.Escape(version);
        var match = Regex.Match(changelog[startIndex..], $"(?m)^#+[^\\r\\n]*\\bv?{escaped}\\b[^\\r\\n]*");
        return match.Success ? startIndex + match.Index : -1;
    }

    private static async Task<(string Version, string Url, string Notes, bool ReachedInstalledVersion)?> GetLatestReleaseAsync(
        string repository, string installedVersion, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=100", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        string? latestTag = null;
        string latestUrl = string.Empty;
        var notes = new List<string>();
        var reachedInstalledVersion = false;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
                continue;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var version = ExtractVersion(tag);
            if (version is null)
                continue;
            if (latestTag is null)
            {
                latestTag = tag;
                latestUrl = release.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
            }
            if (Version.TryParse(version, out var parsedInstalled) && Version.TryParse(installedVersion, out var installed) && parsedInstalled <= installed)
            {
                reachedInstalledVersion = true;
                break;
            }
            if (release.TryGetProperty("body", out var bodyElement) && bodyElement.GetString() is { Length: > 0 } body)
                notes.Add(body);
        }
        return latestTag is null ? null : (latestTag, latestUrl, string.Join("\n", notes), reachedInstalledVersion);
    }

    private static async Task<IReadOnlyList<string>> FindExecutableCandidatesAsync(string command, CancellationToken cancellationToken)
    {
        if (command.Equals("opencode", StringComparison.OrdinalIgnoreCase))
        {
            var standalone = OpenCodeExecutableLocator.FindInPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            if (standalone is not null)
                return new[] { standalone };
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "where.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { command }
        });
        if (process is null)
            return Array.Empty<string>();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            return Array.Empty<string>();
        return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(File.Exists)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat" or ".ps1" or ".com")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private static async Task<string> RunVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var isNativeExecutable = Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                 Path.GetExtension(executable).Equals(".com", StringComparison.OrdinalIgnoreCase);
        var startInfo = isNativeExecutable
            ? new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
            : new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", $"& '{executable.Replace("'", "''", StringComparison.Ordinal)}' --version" }
        };
        if (isNativeExecutable)
            startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo);
        if (process is null)
            return string.Empty;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? await stdout : await stderr;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return string.Empty;
        }
    }

    private static IEnumerable<string> GetConfigFiles(string agent, LauncherSettings settings)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] global = agent switch
        {
            AgentTools.OpenCode => [Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "opencode.json"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "opencode.jsonc"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "cli.json"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "tui.json"), Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "tui.jsonc")],
            AgentTools.ClaudeCode => [Path.Combine(home, ".claude.json"), Path.Combine(home, ".claude", "settings.json"), Path.Combine(home, ".claude", "settings.local.json"), Path.Combine(home, ".claude", "CLAUDE.md")],
            AgentTools.Codex => [Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "config.toml")],
            AgentTools.Hermes => [Path.Combine(Environment.GetEnvironmentVariable("HERMES_HOME") ?? Path.Combine(home, ".hermes"), "config.yaml")],
            _ => Array.Empty<string>()
        };

        foreach (var path in global)
            if (File.Exists(path))
                yield return path;

        foreach (var repository in settings.Repositories.Where(repository => Directory.Exists(repository.Path)))
        {
            string[] projectFiles = agent switch
            {
                AgentTools.OpenCode => [Path.Combine(repository.Path, "opencode.json"), Path.Combine(repository.Path, "opencode.jsonc"), Path.Combine(repository.Path, ".opencode", "opencode.json"), Path.Combine(repository.Path, ".opencode", "opencode.jsonc")],
                AgentTools.ClaudeCode => [Path.Combine(repository.Path, ".mcp.json"), Path.Combine(repository.Path, ".claude", "settings.json"), Path.Combine(repository.Path, ".claude", "settings.local.json"), Path.Combine(repository.Path, "CLAUDE.md"), Path.Combine(repository.Path, ".claude", "CLAUDE.md")],
                AgentTools.Codex => [Path.Combine(repository.Path, ".codex", "config.toml")],
                _ => Array.Empty<string>()
            };
            foreach (var file in projectFiles)
                if (File.Exists(file))
                    yield return file;

        }
    }

    private static IEnumerable<string> GetConfigDirectories(string agent, LauncherSettings settings)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        if (agent == AgentTools.OpenCode)
        {
            var globalRoot = Path.Combine(xdg, "opencode");
            foreach (var name in new[] { "agents", "commands", "plugins", "skills" })
                yield return Path.Combine(globalRoot, name);
        }
        else if (agent == AgentTools.ClaudeCode)
        {
            foreach (var name in new[] { "agents", "commands", "rules", "skills" })
                yield return Path.Combine(home, ".claude", name);
        }

        foreach (var repository in settings.Repositories.Where(repository => Directory.Exists(repository.Path)))
        {
            if (agent == AgentTools.OpenCode)
            {
                foreach (var name in new[] { "agents", "commands", "plugins", "skills" })
                    yield return Path.Combine(repository.Path, ".opencode", name);
            }
            else if (agent == AgentTools.ClaudeCode)
            {
                foreach (var name in new[] { "agents", "commands", "rules", "skills" })
                    yield return Path.Combine(repository.Path, ".claude", name);
            }
            else if (agent == AgentTools.Codex)
                yield return Path.Combine(repository.Path, ".agents", "skills");
        }
    }

    private static IEnumerable<string> EnumerateBackupFiles(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(".venv", StringComparison.OrdinalIgnoreCase))
                        pending.Push(entry);
                }
                else
                    yield return entry;
            }
        }
    }

    private static string? ExtractVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = VersionPattern.Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeBadger-AgentUpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
