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
            _logger.LogInformation("Starting agent terminal for repository {RepositoryId} in {WorkingDirectory} with configured command {Command}.",
                id, workingDirectory, SanitizeCommandForLog(commandLine));
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(_localization.T("The built-in terminal is only available on Windows."));
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
            var session = isPowerShell
                ? CreatePowerShellSession(workingDirectory, Math.Max(columns, 1), Math.Max(rows, 1), encodedPowerShellCommand)
                : CreateSession(workingDirectory, commandLine, Math.Max(columns, 1), Math.Max(rows, 1), sessionArguments);
            lock (_sessionsLock)
                _sessions[id] = session;
            _ = Task.Run(() => ReadOutputAsync(id, session));
            _ = WatchProcessAsync(id, session);
            _logger.LogInformation("Agent terminal started for repository {RepositoryId}; process ID {ProcessId}.", id, session.Process.Id);
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
            throw new PlatformNotSupportedException(_localization.T("The built-in terminal is only available on Windows."));
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(_localization.T("Repository folder does not exist: {0}", workingDirectory));

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var gate = _startGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (IsRunning(id))
                return;
            var session = CreatePowerShellSession(workingDirectory, Math.Max(columns, 1), Math.Max(rows, 1), encodedCommand);
            lock (_sessionsLock)
                _sessions[id] = session;
            _ = Task.Run(() => ReadOutputAsync(id, session));
            _ = WatchProcessAsync(id, session);
            _logger.LogInformation("Started PowerShell update command for repository {RepositoryId}; process ID {ProcessId}.", id, session.Process.Id);
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
        if (session is not null && session.ConsoleHandle != IntPtr.Zero)
        {
            var result = ResizePseudoConsole(session.ConsoleHandle, new Coord((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
            if (result != 0)
                throw new Win32Exception(result);
        }
    }

    public async Task StopAsync(Guid id, bool requireTermination = false)
    {
        if (!OperatingSystem.IsWindows())
            return;

        TerminalSession? session;
        lock (_sessionsLock)
            _sessions.TryGetValue(id, out session);
        if (session is null) return;
        _logger.LogInformation("Stopping agent terminal for repository {RepositoryId}.", id);
        try
        {
            if (!session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (!requireTermination) { }

        try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
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
            await session.Process.WaitForExitAsync();
            if (IsActiveSession(id, session))
            {
                ClearInteractiveAgentActivity(id);
                _logger.LogInformation("Agent terminal process for repository {RepositoryId} exited with code {ExitCode}.", id, session.Process.ExitCode);
                SessionExited?.Invoke(id, session.Process.ExitCode);
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
               executable.Equals("hermes", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("codex", StringComparison.OrdinalIgnoreCase);
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

    private sealed class TerminalSession(Process process, FileStream input, FileStream output, IntPtr consoleHandle) : IDisposable
    {
        public Process Process { get; } = process;
        public FileStream Input { get; } = input;
        public FileStream Output { get; } = output;
        public IntPtr ConsoleHandle { get; } = consoleHandle;

        public void Dispose()
        {
            Input.Dispose();
            Output.Dispose();
            if (ConsoleHandle != IntPtr.Zero)
                ClosePseudoConsole(ConsoleHandle);
            Process.Dispose();
        }
    }

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
