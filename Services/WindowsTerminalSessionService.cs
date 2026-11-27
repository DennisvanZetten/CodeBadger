using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CodeBadger.Services;

public sealed class WindowsTerminalSessionService : IAsyncDisposable
{
    private readonly Dictionary<Guid, TerminalSession> _sessions = [];
    private readonly object _sessionsLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _startGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<string>> _recentOutput = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _openCodeSessionIds = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _openCodeTerminalLines = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _interactiveAgentLastInput = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _interactiveAgentLastActivity = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _interactiveAgentSessions = new();
    private readonly LocalizationService _localization;
    private readonly ILogger<WindowsTerminalSessionService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _terminalWriteGates = new();

    public WindowsTerminalSessionService(LocalizationService localization, ILogger<WindowsTerminalSessionService> logger)
    {
        _localization = localization;
        _logger = logger;
    }

    public event Action<Guid, string>? OutputReceived;
    public event Action<Guid, int>? SessionExited;
    public bool TryGetOpenCodeSessionId(Guid id, out string sessionId) => _openCodeSessionIds.TryGetValue(id, out sessionId!);
    public bool TryGetOpenCodeTerminalLine(Guid id, out string line) => _openCodeTerminalLines.TryGetValue(id, out line!);

    public bool IsInteractiveAgentWorking(Guid id) =>
        _interactiveAgentSessions.ContainsKey(id) &&
        _interactiveAgentLastInput.TryGetValue(id, out var lastInput) &&
        DateTime.UtcNow - lastInput < TimeSpan.FromHours(8) &&
        _interactiveAgentLastActivity.TryGetValue(id, out var lastActivity) &&
        (lastActivity <= lastInput
            ? DateTime.UtcNow - lastInput < TimeSpan.FromSeconds(90)
            : DateTime.UtcNow - lastActivity < TimeSpan.FromSeconds(12));

    public bool IsRunning(Guid id)
    {
        lock (_sessionsLock)
            return _sessions.ContainsKey(id);
    }

    public IReadOnlyList<string> GetRecentOutput(Guid id) =>
        _recentOutput.TryGetValue(id, out var output)
            ? CopyRecentOutput(output)
            : [];

    public void ClearRecentOutput(Guid id) => _recentOutput.TryRemove(id, out _);

    private static string[] CopyRecentOutput(List<string> output)
    {
        lock (output)
            return output.ToArray();
    }

    public async Task StartAsync(Guid id, string workingDirectory, string commandLine, int columns = 120, int rows = 30,
        bool openCodeSandboxEnabled = false, string? openCodeSessionId = null)
    {
        var gate = _startGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (IsRunning(id))
                return;
            _openCodeSessionIds.TryRemove(id, out _);
            _openCodeTerminalLines.TryRemove(id, out _);
            _logger.LogInformation("Starting agent terminal for repository {RepositoryId} in {WorkingDirectory} with configured command {Command}.",
                id, workingDirectory, SanitizeCommandForLog(commandLine));
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacCatalyst())
                throw new PlatformNotSupportedException(_localization.T("The built-in terminal is only available on Windows and macOS."));
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacCatalyst())
                throw new PlatformNotSupportedException(_localization.T("The built-in terminal is only available on Windows and macOS."));
            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException(_localization.T("Repository folder does not exist: {0}", workingDirectory));
            if (openCodeSandboxEnabled)
                throw new InvalidOperationException(_localization.T("OpenCode sandbox is enabled, but isolated execution is not available yet. OpenCode was not started."));

            var sessionArguments = string.Empty;
            if (!string.IsNullOrWhiteSpace(openCodeSessionId))
            {
                if (!openCodeSessionId.StartsWith("ses_", StringComparison.Ordinal) ||
                    !openCodeSessionId.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                    throw new ArgumentException("Invalid OpenCode session ID.", nameof(openCodeSessionId));
                sessionArguments = $"--session {openCodeSessionId}";
                _openCodeSessionIds[id] = openCodeSessionId;
            }
            if (IsOpenCodeCommand(commandLine))
                _openCodeTerminalLines[id] = openCodeSessionId ?? string.Empty;
            if (IsInteractiveAgentCommand(commandLine))
                _interactiveAgentSessions[id] = 0;
            else
                ClearInteractiveAgentActivity(id);

            var hasEncodedPowerShellCommand = commandLine.StartsWith("powershell-encoded ", StringComparison.Ordinal);
            var encodedPowerShellCommand = hasEncodedPowerShellCommand ? commandLine["powershell-encoded ".Length..] : null;
            var isPowerShell = string.IsNullOrWhiteSpace(commandLine) || hasEncodedPowerShellCommand;
            TerminalSession session;
#if MACCATALYST
            if (OperatingSystem.IsMacCatalyst())
                session = await CreateMacTerminalSessionAsync(workingDirectory, commandLine, Math.Max(columns, 1), Math.Max(rows, 1), sessionArguments);
            else
#endif
                session = isPowerShell
                    ? CreatePowerShellSession(workingDirectory, Math.Max(columns, 1), Math.Max(rows, 1), encodedPowerShellCommand)
                    : CreateSession(workingDirectory, commandLine, Math.Max(columns, 1), Math.Max(rows, 1), sessionArguments);
            lock (_sessionsLock)
                _sessions[id] = session;
            _ = Task.Run(() => ReadOutputAsync(id, session));
            _ = WatchProcessAsync(id, session);
            _logger.LogInformation("Agent terminal started for repository {RepositoryId}; process ID {ProcessId}.", id, session.ProcessId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not start agent terminal for repository {RepositoryId}.", id);
            throw;
        }
        finally { gate.Release(); }
    }

    public Task StartPowerShellAsync(Guid id, string workingDirectory, int columns = 120, int rows = 30) =>
        StartAsync(id, workingDirectory, string.Empty, columns, rows);

    public async Task StartPowerShellWithCommandAsync(Guid id, string workingDirectory, string command, int columns = 120, int rows = 30)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0'))
            throw new ArgumentException("A valid initial PowerShell command is required.", nameof(command));

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(_localization.T("The built-in terminal is only available on Windows and macOS."));
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(_localization.T("Repository folder does not exist: {0}", workingDirectory));

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var gate = _startGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (IsRunning(id))
                return;
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(_localization.T("The built-in terminal is only available on Windows and macOS."));
            _logger.LogInformation("Starting PowerShell update in repository {RepositoryId}.", id);
            var session = CreatePowerShellSession(workingDirectory, Math.Max(columns, 1), Math.Max(rows, 1), encodedCommand);
            lock (_sessionsLock)
                _sessions[id] = session;
            _ = Task.Run(() => ReadOutputAsync(id, session));
            _ = WatchProcessAsync(id, session);
            _logger.LogInformation("Started PowerShell update command for repository {RepositoryId}; process ID {ProcessId}.", id, session.ProcessId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not start PowerShell update command for repository {RepositoryId}.", id);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task WriteInputAsync(Guid id, string input)
    {
        TerminalSession? session;
        lock (_sessionsLock)
            _sessions.TryGetValue(id, out session);
        if (session is not null)
        {
            var gate = _terminalWriteGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
            var bytes = Encoding.UTF8.GetBytes(input);
            await session.Input.WriteAsync(bytes);
            await session.Input.FlushAsync();
            if (_interactiveAgentSessions.ContainsKey(id) && input.IndexOfAny(['\r', '\n']) >= 0)
            {
                var now = DateTime.UtcNow;
                _interactiveAgentLastInput[id] = now;
                _interactiveAgentLastActivity.TryRemove(id, out _);
            }
            _logger.LogInformation("Forwarded terminal input to agent for repository {RepositoryId}.", id);
            }
            finally { gate.Release(); }
        }
    }

    public void Resize(Guid id, int columns, int rows)
    {
        TerminalSession? session;
        lock (_sessionsLock)
            _sessions.TryGetValue(id, out session);
#if MACCATALYST
        if (session?.Pty is { } pty)
        {
            pty.Resize(columns, rows);
        }
        else
#endif
        if (session is not null && session.ConsoleHandle != IntPtr.Zero)
        {
            var result = ResizePseudoConsole(session.ConsoleHandle, new Coord((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
            if (result != 0)
                throw new Win32Exception(result);
        }
    }

    public async Task StopAsync(Guid id, bool requireTermination = false)
    {
        TerminalSession? session;
        lock (_sessionsLock)
            _sessions.TryGetValue(id, out session);
        if (session is null) return;
        _logger.LogInformation("Stopping agent terminal for repository {RepositoryId}.", id);
        try
        {
            if (!session.HasExited)
                await session.KillAsync();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (!requireTermination) { }

        try { await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) when (!requireTermination) { }
        lock (_sessionsLock)
            _sessions.Remove(id);
        _openCodeSessionIds.TryRemove(id, out _);
        _openCodeTerminalLines.TryRemove(id, out _);
        ClearInteractiveAgentActivity(id);
        _recentOutput.TryRemove(id, out _);
        session.Dispose();
        _logger.LogInformation("Agent terminal stopped for repository {RepositoryId}.", id);
    }

    public async ValueTask DisposeAsync()
    {
        Guid[] sessionIds;
        lock (_sessionsLock)
            sessionIds = _sessions.Keys.ToArray();
        foreach (var id in sessionIds)
            await StopAsync(id);
    }

    private async Task ReadOutputAsync(Guid id, TerminalSession session)
    {
        var buffer = new byte[8192];
        var characterBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                var count = await session.Output.ReadAsync(buffer);
                if (count == 0)
                    break;

                // A ConPTY read can end halfway through a multi-byte UTF-8 character
                // (common with box-drawing and other Unicode UI glyphs). Keep decoder
                // state between reads instead of replacing those partial bytes.
                var characterCount = decoder.GetChars(buffer, 0, count, characterBuffer, 0, flush: false);
                if (characterCount > 0)
                    PublishOutput(id, new string(characterBuffer, 0, characterCount));
            }

            var remainingCharacters = decoder.GetChars([], 0, 0, characterBuffer, 0, flush: true);
            if (remainingCharacters > 0)
                PublishOutput(id, new string(characterBuffer, 0, remainingCharacters));
        }
        catch (ObjectDisposedException) { }
        catch (IOException exception)
        {
            _logger.LogInformation(exception, "Agent terminal output stream closed for session {SessionId}.", id);
        }
#if MACCATALYST
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "macOS PTY output stream closed for session {SessionId}.", id);
        }
#endif
    }

    private void PublishOutput(Guid id, string text)
    {
        var output = _recentOutput.GetOrAdd(id, _ => []);
        lock (output)
        {
            output.Add(text);
            while (output.Count > 2000)
                output.RemoveAt(0);
        }
        OutputReceived?.Invoke(id, text);
        if (_interactiveAgentSessions.ContainsKey(id) &&
            _interactiveAgentLastInput.TryGetValue(id, out var lastInput) &&
            DateTime.UtcNow - lastInput < TimeSpan.FromHours(8))
            _interactiveAgentLastActivity[id] = DateTime.UtcNow;
    }

    private async Task WatchProcessAsync(Guid id, TerminalSession session)
    {
        try
        {
            await session.WaitForExitAsync();
            if (IsActiveSession(id, session))
            {
                ClearInteractiveAgentActivity(id);
                _logger.LogInformation("Agent terminal process for repository {RepositoryId} exited with code {ExitCode}.", id, session.ExitCode);
                SessionExited?.Invoke(id, session.ExitCode);
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not observe agent terminal exit for repository {RepositoryId}.", id);
        }
    }

    private bool IsActiveSession(Guid id, TerminalSession session)
    {
        lock (_sessionsLock)
            return _sessions.TryGetValue(id, out var active) && ReferenceEquals(active, session);
    }

    private TerminalSession CreateSession(string workingDirectory, string commandLine, int columns, int rows,
        string additionalArguments = "")
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? outputWrite = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        Process? process = null;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out pseudoConsole);
            if (result != 0)
                throw new InvalidOperationException(_localization.T("CreatePseudoConsole failed (HRESULT 0x{0}): {1}", result.ToString("X8"), Marshal.GetExceptionForHR(result)?.Message ?? string.Empty));
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, out var attributeSize);
            if (attributeSize == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), _localization.T("Unable to determine the ConPTY process attribute size."));
            attributeList = Marshal.AllocHGlobal((IntPtr)attributeSize);
            if (!InitializeProcThreadAttributeListInPlace(attributeList, 1, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), _localization.T("Failed to initialize ConPTY process attributes."));
            attributeListInitialized = true;
            // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE takes the HPCON value itself, as in Microsoft's ConPTY sample.
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole, pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), _localization.T("Failed to attach the pseudo console to the terminal process."));

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributeList
            };
            var startupPointer = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
            try
            {
                Marshal.StructureToPtr(startupInfo, startupPointer, false);
                var processInfo = new ProcessInformation();
                var launch = ResolveCommand(commandLine, additionalArguments);
                var mutableCommandLine = new StringBuilder(launch.CommandLine);
                if (!CreateProcessW(launch.Executable, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory,
                        startupPointer, out processInfo))
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        _localization.T("The configured command '{0}' could not be started (Windows error {1}: {2}). Found executable: {3}. Check that the application is available to CodeBadger.",
                            commandLine, errorCode, new Win32Exception(errorCode).Message, launch.Executable));
                }

                CloseHandle(processInfo.Thread);
                CloseHandle(processInfo.Process);
                process = Process.GetProcessById((int)processInfo.ProcessId);
            }
            finally
            {
                Marshal.FreeHGlobal(startupPointer);
            }

            var session = new TerminalSession(process,
                new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false),
                new FileStream(outputRead, FileAccess.Read, 8192, isAsync: false), pseudoConsole);
            inputWrite = null;
            outputRead = null;
            pseudoConsole = IntPtr.Zero;
            return session;
        }
        catch
        {
            process?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            if (pseudoConsole != IntPtr.Zero)
                ClosePseudoConsole(pseudoConsole);
            throw;
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }
            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private TerminalSession CreatePowerShellSession(string workingDirectory, int columns, int rows, string? encodedCommand = null)
    {
        var powershell = TryFindExecutable("pwsh.exe") ?? TryFindExecutable("powershell.exe")
            ?? throw new FileNotFoundException(_localization.T("PowerShell could not be found in PATH."));
        var arguments = string.IsNullOrWhiteSpace(encodedCommand)
            ? "-NoLogo -NoExit"
            : $"-NoLogo -NoExit -EncodedCommand {encodedCommand}";
        return CreateSession(workingDirectory, $"\"{powershell}\" {arguments}", columns, rows);
    }

#if MACCATALYST
    private static async Task<TerminalSession> CreateMacTerminalSessionAsync(string workingDirectory, string commandLine, int columns, int rows,
        string additionalArguments)
    {
        if (commandLine.StartsWith("powershell-encoded ", StringComparison.Ordinal))
            throw new PlatformNotSupportedException("PowerShell commands are not supported in the macOS terminal.");

        var command = commandLine.Trim();
        string executable;
        string arguments;
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new InvalidOperationException("The configured application command has an unclosed quotation mark.");
            executable = command[1..closingQuote];
            arguments = command[(closingQuote + 1)..].Trim();
        }
        else
        {
            var firstSpace = command.IndexOfAny([' ', '\t']);
            executable = firstSpace < 0 ? command : command[..firstSpace];
            arguments = firstSpace < 0 ? string.Empty : command[(firstSpace + 1)..].Trim();
        }
        if (!string.IsNullOrWhiteSpace(additionalArguments))
            arguments = string.Join(' ', new[] { arguments, additionalArguments }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var isInteractiveShell = string.IsNullOrWhiteSpace(command);
        var executablePath = isInteractiveShell ? "/bin/zsh" : ResolveMacExecutable(executable);
        var processArguments = isInteractiveShell ? ["-l"] : SplitCommandArguments(arguments);
        return new TerminalSession(MacPtyConnection.Start(workingDirectory, executablePath, processArguments, columns, rows));
    }

    private static string ResolveMacExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar))
        {
            if (!File.Exists(executable))
                throw new FileNotFoundException($"The configured application '{executable}' does not exist.", executable);
            return executable;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find '{executable}' in CodeBadger’s PATH.", executable);
    }

    private static string[] SplitCommandArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;
        foreach (var character in arguments)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
            }
            else if (character == '\\' && quote != '\'')
            {
                escaped = true;
            }
            else if (quote != '\0' && character == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && character is '\'' or '"')
            {
                quote = character;
            }
            else if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (escaped)
            current.Append('\\');
        if (quote != '\0')
            throw new InvalidOperationException("The configured application command has an unclosed quotation mark.");
        if (current.Length > 0)
            result.Add(current.ToString());
        return result.ToArray();
    }
#endif

    private (string Executable, string CommandLine) ResolveCommand(string configuredCommand, string additionalArguments = "")
    {
        var command = configuredCommand.Trim();
        var executable = ReadExecutableToken(command, out var argumentText);
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException(_localization.T("The configured application is empty."));

        var resolvedExecutable = FindExecutable(executable);
        argumentText = string.Join(' ', new[] { argumentText, additionalArguments }
            .Where(argument => !string.IsNullOrWhiteSpace(argument)));
        var extension = Path.GetExtension(resolvedExecutable);
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powershell = TryFindExecutable("pwsh.exe") ?? TryFindExecutable("powershell.exe")
                ?? throw new FileNotFoundException(_localization.T("The PowerShell wrapper for the configured command was found, but PowerShell could not be found in PATH."));
            var scriptCommand = $"\"{powershell}\" -NoLogo -NoProfile -File \"{resolvedExecutable}\"" +
                                (string.IsNullOrWhiteSpace(argumentText) ? string.Empty : " " + argumentText);
            return (powershell, scriptCommand);
        }
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrEmpty(extension) && !resolvedExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var batchCommand = $"call \"{resolvedExecutable}\"{(string.IsNullOrWhiteSpace(argumentText) ? string.Empty : " " + argumentText)}";
            return (commandInterpreter, $"\"{commandInterpreter}\" /d /s /c {batchCommand}");
        }

        return (resolvedExecutable, $"\"{resolvedExecutable}\"{(string.IsNullOrWhiteSpace(argumentText) ? string.Empty : " " + argumentText)}");
    }

    private static string SanitizeCommandForLog(string command)
    {
        const int maximumLength = 500;
        var sanitized = System.Text.RegularExpressions.Regex.Replace(command,
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength] + "… [truncated]";
    }

    private static bool IsOpenCodeCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.StartsWith("powershell-encoded ", StringComparison.Ordinal))
            return false;
        var command = commandLine.Trim();
        var token = command.StartsWith('"')
            ? command[1..Math.Max(1, command.IndexOf('"', 1))]
            : command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return Path.GetFileNameWithoutExtension(token).Equals("opencode", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInteractiveAgentCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.StartsWith("powershell-encoded ", StringComparison.Ordinal))
            return false;
        var command = commandLine.Trim();
        var token = command.StartsWith('"')
            ? command[1..Math.Max(1, command.IndexOf('"', 1))]
            : command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var executable = Path.GetFileNameWithoutExtension(token);
        return executable.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("hermes", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearInteractiveAgentActivity(Guid id)
    {
        _interactiveAgentSessions.TryRemove(id, out _);
        _interactiveAgentLastInput.TryRemove(id, out _);
        _interactiveAgentLastActivity.TryRemove(id, out _);
    }

    private string ReadExecutableToken(string command, out string arguments)
    {
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new InvalidOperationException(_localization.T("The configured application command has an unclosed quotation mark."));
            arguments = command[(closingQuote + 1)..].TrimStart();
            return command[1..closingQuote];
        }

        var firstSpace = command.IndexOfAny([' ', '\t']);
        if (firstSpace < 0)
        {
            arguments = string.Empty;
            return command;
        }

        arguments = command[(firstSpace + 1)..].TrimStart();
        return command[..firstSpace];
    }

    private string FindExecutable(string executable)
    {
        var extensions = Path.HasExtension(executable)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(".PS1")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var candidates = new List<string>();

        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            candidates.AddRange(extensions.Select(extension => executable + extension));
        }
        else
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                candidates.AddRange(extensions.Select(extension => Path.Combine(directory.Trim('"'), executable + extension)));
        }

        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            _localization.T("Could not find '{0}' in CodeBadger’s PATH. You can enter the full application path in Settings.", executable), executable);
    }

    private string? TryFindExecutable(string executable)
    {
        try { return FindExecutable(executable); }
        catch (FileNotFoundException) { return null; }
    }

    private sealed class TerminalSession : IDisposable
    {
        private readonly Process? _process;
#if MACCATALYST
        private readonly MacPtyConnection? _pty;
#endif

        public TerminalSession(Process process, FileStream input, FileStream output, IntPtr consoleHandle)
        {
            _process = process;
            Input = input;
            Output = output;
            ConsoleHandle = consoleHandle;
        }

#if MACCATALYST
        public TerminalSession(MacPtyConnection pty)
        {
            _pty = pty;
            Input = pty.WriterStream;
            Output = pty.ReaderStream;
        }
#endif

#if MACCATALYST
        public int ProcessId => _pty?.Pid ?? _process!.Id;
        public int ExitCode => _pty?.ExitCode ?? _process!.ExitCode;
        public bool HasExited => _pty?.HasExited ?? _process!.HasExited;
#else
        public int ProcessId => _process!.Id;
        public int ExitCode => _process!.ExitCode;
        public bool HasExited => _process!.HasExited;
#endif
        public Stream Input { get; }
        public Stream Output { get; }
        public IntPtr ConsoleHandle { get; }
#if MACCATALYST
        public MacPtyConnection? Pty => _pty;
#endif

        public async Task WaitForExitAsync()
        {
#if MACCATALYST
            if (_pty is not null)
            {
                await _pty.WaitForExitAsync();
                return;
            }
#endif
            await _process!.WaitForExitAsync();
        }

        public Task KillAsync()
        {
#if MACCATALYST
            if (_pty is not null)
                return _pty.KillAsync();
#endif
            return KillProcessTreeAsync(_process!);
        }

        private static async Task KillProcessTreeAsync(Process process)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        public void Dispose()
        {
#if MACCATALYST
            if (_pty is not null)
            {
                _pty.Dispose();
                return;
            }
#endif

            Input.Dispose();
            Output.Dispose();
            if (ConsoleHandle != IntPtr.Zero)
                ClosePseudoConsole(ConsoleHandle);
            _process!.Dispose();
        }
    }

#if MACCATALYST
    private sealed class MacPtyConnection : IDisposable
    {
        private readonly int _masterFd;
        private readonly int _pid;
        private readonly Task _waitTask;
        private readonly object _exitLock = new();
        private bool _processExited;
        private int _disposed;

        private MacPtyConnection(int masterFd, int pid)
        {
            _masterFd = masterFd;
            _pid = pid;
            var readerFd = Dup(masterFd);
            if (readerFd < 0)
            {
                Close(masterFd);
                throw NativeError("Could not duplicate the PTY output descriptor");
            }

            try
            {
                ReaderStream = new FileStream(new SafeFileHandle((IntPtr)readerFd, ownsHandle: true), FileAccess.Read, 8192, isAsync: false);
                WriterStream = new FileStream(new SafeFileHandle((IntPtr)masterFd, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
            }
            catch
            {
                ReaderStream?.Dispose();
                Close(masterFd);
                throw;
            }

            _waitTask = Task.Run(WaitForExit);
        }

        public Stream ReaderStream { get; }
        public Stream WriterStream { get; }
        public int Pid => _pid;
        public int ExitCode { get; private set; }
        public bool HasExited
        {
            get { lock (_exitLock) return _processExited; }
        }
        public Task WaitForExitAsync() => _waitTask;

        public static MacPtyConnection Start(string workingDirectory, string executable, string[] arguments, int columns, int rows)
        {
            var argv = new[] { executable }.Concat(arguments).ToArray();
            string[] environment =
            [
                "TERM=xterm-256color",
                "TMUX=",
                "TMUX_PANE=",
                "STY=",
                "WINDOW=",
                "WINDOWID=",
                "TERMCAP=",
                "COLUMNS=",
                "LINES="
            ];
            var termios = MacPtyTermios.Create();
            var windowSize = new MacPtyWindowSize
            {
                Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
                Columns = (ushort)Math.Clamp(columns, 1, ushort.MaxValue)
            };
            using var nativeArguments = new Utf8StringArray(argv);
            using var nativeEnvironment = new Utf8StringArray(environment);
            var result = Spawn(executable, nativeArguments.Pointer, nativeEnvironment.Pointer, workingDirectory, ref termios, ref windowSize);
            if (result.Pid < 0)
                throw new InvalidOperationException($"pty_spawn failed with error {result.Error}: {new Win32Exception(result.Error).Message}");
            return new MacPtyConnection(result.MasterFd, result.Pid);
        }


        public void Resize(int columns, int rows)
        {
            if (ResizePty(_masterFd, (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
                    (ushort)Math.Clamp(columns, 1, ushort.MaxValue)) == -1)
                throw NativeError("Could not resize the macOS PTY");
        }

        public async Task KillAsync()
        {
            lock (_exitLock)
            {
                if (_processExited)
                    return;
                _ = KillProcess(_pid, MacSighup);
            }
            try { await _waitTask.WaitAsync(TimeSpan.FromMilliseconds(300)); }
            catch (TimeoutException)
            {
                lock (_exitLock)
                {
                    if (!_processExited)
                        _ = KillProcess(_pid, MacSigkill);
                }
            }
        }

        private void WaitForExit()
        {
            var status = 0;
            var waitedPid = WaitPid(_pid, ref status, 0);
            if (waitedPid < 0)
                throw NativeError("Could not wait for the macOS PTY process");
            lock (_exitLock)
            {
                ExitCode = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                _processExited = true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            ReaderStream.Dispose();
            WriterStream.Dispose();
        }

        private static Win32Exception NativeError(string operation)
        {
            var error = GetPtyError();
            return new Win32Exception(error, $"{operation} (errno {error}).");
        }

        private const int MacSighup = 1;
        private const int MacSigkill = 9;

        // pty_spawn expects null-terminated char** vectors. StringToCoTaskMemUTF8
        // avoids the runtime's unsupported LPUTF8Str array marshalling.
        private sealed class Utf8StringArray : IDisposable
        {
            private readonly List<IntPtr> _strings = [];
            public IntPtr Pointer { get; private set; }

            public Utf8StringArray(IReadOnlyList<string> values)
            {
                try
                {
                    Pointer = Marshal.AllocHGlobal(checked((values.Count + 1) * IntPtr.Size));
                    for (var i = 0; i < values.Count; i++)
                    {
                        if (values[i].Contains('\0'))
                            throw new ArgumentException("PTY arguments cannot contain NUL characters.");
                        var value = Marshal.StringToCoTaskMemUTF8(values[i]);
                        _strings.Add(value);
                        Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, value);
                    }
                    Marshal.WriteIntPtr(Pointer, values.Count * IntPtr.Size, IntPtr.Zero);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                foreach (var value in _strings)
                    Marshal.FreeCoTaskMem(value);
                _strings.Clear();
                if (Pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Pointer);
                    Pointer = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MacPtySpawnResult
        {
            public int MasterFd;
            public int Pid;
            public int Error;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MacPtyWindowSize
        {
            public ushort Rows;
            public ushort Columns;
            public ushort XPixel;
            public ushort YPixel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MacPtyTermios
        {
            public uint InputFlags;
            public uint OutputFlags;
            public uint ControlFlags;
            public uint LocalFlags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ControlCharacters;
            public uint InputSpeed;
            public uint OutputSpeed;

            public static MacPtyTermios Create()
            {
                var characters = new byte[32];
                characters[0] = 4;   // VEOF
                characters[3] = 127; // VERASE
                characters[4] = 23;  // VWERASE
                characters[5] = 21;  // VKILL
                characters[6] = 18;  // VREPRINT
                characters[8] = 3;   // VINTR
                characters[9] = 28;  // VQUIT
                characters[10] = 26; // VSUSP
                characters[12] = 17; // VSTART
                characters[13] = 19; // VSTOP
                characters[14] = 22; // VLNEXT
                characters[15] = 15; // VDISCARD
                characters[16] = 1;  // VMIN
                return new MacPtyTermios
                {
                    InputFlags = 0x00000002 | 0x00000100 | 0x00000200 | 0x00000800 | 0x00002000 | 0x00004000,
                    OutputFlags = 0x00000001 | 0x00000002,
                    ControlFlags = 0x00000800 | 0x00000300 | 0x00004000,
                    LocalFlags = 0x00000100 | 0x00000080 | 0x00000400 | 0x00000008 | 0x00000002 | 0x00000004 | 0x00000001 | 0x00000040,
                    ControlCharacters = characters,
                    InputSpeed = 38400,
                    OutputSpeed = 38400
                };
            }
        }

        [DllImport("libporta_pty", EntryPoint = "pty_spawn", SetLastError = true)]
        private static extern MacPtySpawnResult Spawn(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
            IntPtr arguments,
            IntPtr environment,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string workingDirectory,
            ref MacPtyTermios termios,
            ref MacPtyWindowSize windowSize);

        [DllImport("libporta_pty", EntryPoint = "pty_resize", SetLastError = true)]
        private static extern int ResizePty(int masterFd, ushort rows, ushort columns);

        [DllImport("libporta_pty", EntryPoint = "pty_kill", SetLastError = true)]
        private static extern int KillProcess(int pid, int signal);

        [DllImport("libporta_pty", EntryPoint = "pty_waitpid", SetLastError = true)]
        private static extern int WaitPid(int pid, ref int status, int options);

        [DllImport("libporta_pty", EntryPoint = "pty_close", SetLastError = true)]
        private static extern int Close(int masterFd);

        [DllImport("libporta_pty", EntryPoint = "pty_get_errno")]
        private static extern int GetPtyError();

        [DllImport("libSystem.dylib", EntryPoint = "dup", SetLastError = true)]
        private static extern int Dup(int fileDescriptor);
    }
#endif

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, out nuint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "InitializeProcThreadAttributeList")] private static extern bool InitializeProcThreadAttributeListInPlace(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, IntPtr startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
