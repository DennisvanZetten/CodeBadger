using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RepoLauncher.Services;

public sealed class WindowsTerminalSessionService : IAsyncDisposable
{
    private readonly Dictionary<Guid, TerminalSession> _sessions = [];

    public event Action<Guid, string>? OutputReceived;
    public event Action<Guid, int>? SessionExited;

    public async Task StartAsync(Guid id, string workingDirectory, string commandLine, int columns = 120, int rows = 30)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("De ingebouwde terminal is alleen beschikbaar op Windows.");
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Repositorymap bestaat niet: {workingDirectory}");
        if (_sessions.ContainsKey(id))
            await StopAsync(id);

        if (string.IsNullOrWhiteSpace(commandLine))
            throw new InvalidOperationException("Configureer eerst een applicatie of opdracht in Instellingen.");

        var session = CreateSession(workingDirectory, commandLine, Math.Max(columns, 1), Math.Max(rows, 1));
        _sessions[id] = session;
        _ = Task.Run(() => ReadOutputAsync(id, session));
        _ = WatchProcessAsync(id, session);
    }

    public async Task WriteInputAsync(Guid id, string input)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            await session.Input.WriteAsync(bytes);
            await session.Input.FlushAsync();
        }
    }

    public void Resize(Guid id, int columns, int rows)
    {
        if (_sessions.TryGetValue(id, out var session) && session.ConsoleHandle != IntPtr.Zero)
        {
            var result = ResizePseudoConsole(session.ConsoleHandle, new Coord((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
            if (result != 0)
                throw new Win32Exception(result);
        }
    }

    public async Task StopAsync(Guid id)
    {
        if (!_sessions.Remove(id, out var session))
            return;
        try
        {
            if (!session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        session.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray())
            await StopAsync(id);
    }

    private async Task ReadOutputAsync(Guid id, TerminalSession session)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await session.Output.ReadAsync(buffer);
                if (count == 0)
                    break;
                OutputReceived?.Invoke(id, Encoding.UTF8.GetString(buffer, 0, count));
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task WatchProcessAsync(Guid id, TerminalSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync();
            if (_sessions.TryGetValue(id, out var active) && ReferenceEquals(active, session))
                SessionExited?.Invoke(id, session.Process.ExitCode);
        }
        catch (InvalidOperationException) { }
    }

    private static TerminalSession CreateSession(string workingDirectory, string commandLine, int columns, int rows)
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
                throw new InvalidOperationException($"CreatePseudoConsole mislukt (HRESULT 0x{result:X8}): {Marshal.GetExceptionForHR(result)?.Message}");
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, out var attributeSize);
            if (attributeSize == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Grootte van de ConPTY-procesattributen kon niet worden bepaald.");
            attributeList = Marshal.AllocHGlobal((IntPtr)attributeSize);
            if (!InitializeProcThreadAttributeListInPlace(attributeList, 1, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Initialiseren van de ConPTY-procesattributen is mislukt.");
            attributeListInitialized = true;
            // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE takes the HPCON value itself, as in Microsoft's ConPTY sample.
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole, pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Koppelen van de pseudo-console aan het terminalproces is mislukt.");

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
                var launch = ResolveCommand(commandLine);
                var mutableCommandLine = new StringBuilder(launch.CommandLine);
                if (!CreateProcessW(launch.Executable, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory,
                        startupPointer, out processInfo))
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"De geconfigureerde opdracht '{commandLine}' kon niet worden gestart (Windows-fout {errorCode}: {new Win32Exception(errorCode).Message}). " +
                        $"Gevonden executable: {launch.Executable}. Controleer of de applicatie vanuit Repo Launcher bereikbaar is.");
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

    private static (string Executable, string CommandLine) ResolveCommand(string configuredCommand)
    {
        var command = configuredCommand.Trim();
        var executable = ReadExecutableToken(command, out var argumentText);
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("De geconfigureerde applicatie is leeg.");

        var resolvedExecutable = FindExecutable(executable);
        var extension = Path.GetExtension(resolvedExecutable);
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powershell = TryFindExecutable("pwsh.exe") ?? TryFindExecutable("powershell.exe")
                ?? throw new FileNotFoundException("De PowerShell-wrapper van de geconfigureerde opdracht is gevonden, maar PowerShell zelf kon niet via PATH worden gevonden.");
            var scriptCommand = $"\"{powershell}\" -NoLogo -NoProfile -File \"{resolvedExecutable}\"" +
                                (string.IsNullOrWhiteSpace(argumentText) ? string.Empty : " " + argumentText);
            return (powershell, scriptCommand);
        }
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrEmpty(extension) && !resolvedExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var batchCommand = $"\"{resolvedExecutable}\"{(string.IsNullOrWhiteSpace(argumentText) ? string.Empty : " " + argumentText)}";
            return (commandInterpreter, $"\"{commandInterpreter}\" /d /s /c \"\"{batchCommand}\"\"");
        }

        return (resolvedExecutable, $"\"{resolvedExecutable}\"{(string.IsNullOrWhiteSpace(argumentText) ? string.Empty : " " + argumentText)}");
    }

    private static string ReadExecutableToken(string command, out string arguments)
    {
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new InvalidOperationException("De applicatieopdracht bevat een niet-afgesloten aanhalingsteken.");
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

    private static string FindExecutable(string executable)
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
            $"Kan '{executable}' niet vinden in de PATH-omgevingsvariabele van Repo Launcher. Vul in Instellingen eventueel het volledige pad naar de applicatie in.", executable);
    }

    private static string? TryFindExecutable(string executable)
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
