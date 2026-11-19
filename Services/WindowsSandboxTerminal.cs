#nullable enable annotations
namespace CodeBadger.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Runtime.InteropServices;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// ConPTY transport for an isolated process. No host shell, environment or
    /// repository is shared. Provider brokering and UI integration are separate.
    /// Caller must drain Output until EOF before disposing the pseudo console.
    /// </summary>
    public sealed class WindowsSandboxTerminal : IDisposable
    {
        private IntPtr _console;
        private bool _disposed;
        public WindowsSandboxProcess Process { get; }
        public FileStream Input { get; }
        public FileStream Output { get; }

        private WindowsSandboxTerminal(IntPtr console, WindowsSandboxProcess process, FileStream input, FileStream output)
        {
            _console = console;
            Process = process;
            Input = input;
            Output = output;
        }

        public static WindowsSandboxTerminal Start(string executable, string arguments, string repositoryPath,
            IEnumerable<string> selectedFiles, int columns = 120, int rows = 30)
            => StartCore(executable, arguments, repositoryPath, selectedFiles, columns, rows, null);

        public static WindowsSandboxTerminal StartPrepared(string executable, string repositoryPath,
            IEnumerable<string> selectedFiles, Func<string, string, string> prepare, int columns = 120, int rows = 30,
            SandboxWorkspace.Limits? limits = null)
            => StartCore(executable, string.Empty, repositoryPath, selectedFiles, columns, rows, prepare, limits);

        private static WindowsSandboxTerminal StartCore(string executable, string arguments, string repositoryPath,
            IEnumerable<string> selectedFiles, int columns, int rows, Func<string, string, string>? prepare, SandboxWorkspace.Limits? limits = null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            FileStream? input = null, output = null;
            WindowsSandboxProcess? process = null;
            IntPtr console = IntPtr.Zero;
            bool success = false;
            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                    !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                int result = CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out console);
                if (result != 0) Marshal.ThrowExceptionForHR(result);
                // Wrap streams before launch, so allocation failure cannot leave
                // a running console process with undrained output.
                input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);
                inputWrite = null;
                output = new FileStream(outputRead, FileAccess.Read, 1, isAsync: false);
                outputRead = null;
                process = prepare is null
                    ? WindowsSandboxProcess.StartWithWorkspace(executable, arguments, repositoryPath, selectedFiles, console)
                    : WindowsSandboxProcess.StartWithWorkspacePrepared(executable, repositoryPath, selectedFiles, console, prepare, limits);
                // Keep ConPTY's supplied endpoints alive until process creation
                // has completed, as required by Microsoft's session lifecycle.
                inputRead.Dispose(); inputRead = null;
                outputWrite.Dispose(); outputWrite = null;
                var terminal = new WindowsSandboxTerminal(console, process, input, output);
                success = true;
                return terminal;
            }
            finally
            {
                inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
                if (!success)
                {
                    try { process?.Dispose(); }
                    finally
                    {
                        input?.Dispose(); output?.Dispose();
                        if (console != IntPtr.Zero) ClosePseudoConsole(console);
                    }
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int result = ResizePseudoConsole(_console, Size(columns, rows));
            if (result != 0) Marshal.ThrowExceptionForHR(result);
        }

        // Call only after stopping the entire process job, with Output still
        // being drained on another thread. Closing ConPTY may write final output.
        public void CloseConsole()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_console != IntPtr.Zero)
            {
                IntPtr console = _console;
                _console = IntPtr.Zero;
                ClosePseudoConsole(console);
            }
        }

        private static Coord Size(int columns, int rows)
        {
            if (columns < 1 || columns > short.MaxValue || rows < 1 || rows > short.MaxValue)
                throw new ArgumentOutOfRangeException("Console dimensions must be positive 16-bit values.");
            return new Coord { X = (short)columns, Y = (short)rows };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Process.Dispose(); }
            finally
            {
                Input.Dispose(); Output.Dispose();
                if (_console != IntPtr.Zero) { ClosePseudoConsole(_console); _console = IntPtr.Zero; }
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
        [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    }
}
