#nullable enable annotations
namespace CodeBadger.Services
{
    using System;
    using System.ComponentModel;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Diagnostics;
    using System.Threading;

    /// <summary>
    /// Experimental no-network AppContainer process. This is not an OpenCode
    /// sandbox: callers must separately secure files, IPC and credentials.
    /// A job object contains its descendants; closing the job kills them.
    /// </summary>
    public sealed class WindowsSandboxProcess : IDisposable
    {
        private IntPtr _job;
        private IntPtr _process;
        private WindowsAppContainerProfile _profile;
        private bool _disposed;

        public SandboxWorkspace.PreparedWorkspace? Workspace { get; private set; }

        private WindowsSandboxProcess(IntPtr job, IntPtr process, WindowsAppContainerProfile profile)
        {
            _job = job;
            _process = process;
            _profile = profile;
        }

        public static WindowsSandboxProcess Start(string executable, string arguments, string workingDirectory, string sandboxHome)
        {
            ValidateStart(executable, arguments, workingDirectory);
            byte[] environment = SandboxProcessEnvironment.CreateWindowsBlock(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), sandboxHome);
            return StartCore(executable, arguments, workingDirectory, environment, WindowsAppContainerProfile.Create());
        }

        /// <summary>
        /// Copies a standalone executable into its own private AppContainer profile.
        /// No access is granted to the original executable directory or user home.
        /// Does not configure an AI provider or expose any repository files.
        /// </summary>
        public static WindowsSandboxProcess StartStaged(string executable, string arguments)
            => StartStagedCore(executable, arguments, null, null);

        /// <summary>
        /// Host-only preparation hook for creating IPC before the client starts.
        /// The callback receives only profile SID/path metadata, never credentials.
        /// Its returned arguments must not contain secrets. The caller owns IPC
        /// resources and must close them if process creation fails.
        /// </summary>
        public static WindowsSandboxProcess StartStagedPrepared(string executable, Func<string, string, string> prepare)
        {
            ArgumentNullException.ThrowIfNull(prepare);
            return StartStagedCore(executable, string.Empty, null, null, IntPtr.Zero, prepare);
        }

        /// <summary>
        /// Starts with an explicitly reviewed file selection in a private copy.
        /// Does not expose the original repository or allow direct network access.
        /// The returned workspace is deleted on Dispose; export reviewed changes
        /// before disposal, only after stopping all sandbox processes.
        /// </summary>
        public static WindowsSandboxProcess StartWithWorkspace(string executable, string arguments,
            string repositoryPath, IEnumerable<string> selectedFiles)
            => StartWithWorkspace(executable, arguments, repositoryPath, selectedFiles, IntPtr.Zero);

        public static WindowsSandboxProcess StartWithWorkspace(string executable, string arguments,
            string repositoryPath, IEnumerable<string> selectedFiles, IntPtr pseudoConsole)
        {
            ArgumentNullException.ThrowIfNull(repositoryPath);
            ArgumentNullException.ThrowIfNull(selectedFiles);
            return StartStagedCore(executable, arguments, repositoryPath, selectedFiles, pseudoConsole);
        }

        public static WindowsSandboxProcess StartWithWorkspacePrepared(string executable, string repositoryPath,
            IEnumerable<string> selectedFiles, IntPtr pseudoConsole, Func<string, string, string> prepare, SandboxWorkspace.Limits? limits = null)
        {
            ArgumentNullException.ThrowIfNull(repositoryPath);
            ArgumentNullException.ThrowIfNull(selectedFiles);
            ArgumentNullException.ThrowIfNull(prepare);
            return StartStagedCore(executable, string.Empty, repositoryPath, selectedFiles, pseudoConsole, prepare, limits);
        }

        private static WindowsSandboxProcess StartStagedCore(string executable, string arguments,
            string? repositoryPath, IEnumerable<string>? selectedFiles, IntPtr pseudoConsole = default,
            Func<string, string, string>? prepare = null, SandboxWorkspace.Limits? limits = null)
        {
            ValidateStart(executable, arguments, Environment.SystemDirectory);
            var profile = WindowsAppContainerProfile.Create();
            string stagedExecutable;
            byte[] environment;
            SandboxWorkspace.PreparedWorkspace? workspace = null;
            try
            {
                string bin = Path.Combine(profile.FolderPath, "bin");
                Directory.CreateDirectory(bin);
                Directory.CreateDirectory(Path.Combine(profile.FolderPath, "Temp"));
                Directory.CreateDirectory(Path.Combine(profile.FolderPath, "AppData", "Local"));
                Directory.CreateDirectory(Path.Combine(profile.FolderPath, "AppData", "Roaming"));
                stagedExecutable = Path.Combine(bin, Path.GetFileName(executable));
                File.Copy(executable, stagedExecutable, overwrite: false);
                if (repositoryPath != null)
                    workspace = SandboxWorkspace.Create(repositoryPath, profile.FolderPath, selectedFiles!, limits);
                environment = SandboxProcessEnvironment.CreateWindowsBlock(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), profile.FolderPath);
                if (prepare is not null)
                {
                    arguments = prepare(profile.Sid, profile.FolderPath);
                    if (arguments is null || arguments.IndexOf('\0') >= 0)
                        throw new ArgumentException("Prepared process arguments must not contain NUL.");
                }
            }
            catch
            {
                try { profile.Dispose(); } catch { /* Preserve preparation failure. */ }
                throw;
            }
            var process = StartCore(stagedExecutable, arguments, workspace == null ? profile.FolderPath : workspace.Directory, environment, profile, pseudoConsole);
            process.Workspace = workspace;
            return process;
        }

        private static void ValidateStart(string executable, string arguments, string workingDirectory)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                throw new ArgumentException("An existing absolute executable path is required.", nameof(executable));
            if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new ArgumentException("An existing absolute working directory is required.", nameof(workingDirectory));
            if (arguments == null || arguments.IndexOf('\0') >= 0)
                throw new ArgumentException("Arguments must not contain NUL.", nameof(arguments));
        }

        private static WindowsSandboxProcess StartCore(string executable, string arguments, string workingDirectory,
            byte[] environment, WindowsAppContainerProfile profile, IntPtr pseudoConsole = default)
        {
            IntPtr job = IntPtr.Zero, process = IntPtr.Zero, thread = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero, env = IntPtr.Zero, limitsPointer = IntPtr.Zero;
            bool initialized = false, assigned = false, success = false;
            try
            {
                job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x00002000; // KILL_ON_JOB_CLOSE
                limitsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<JobExtendedLimitInformation>());
                Marshal.StructureToPtr(limits, limitsPointer, false);
                if (!SetInformationJobObject(job, 9, limitsPointer, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                nuint size = 0;
                int attributeCount = pseudoConsole == IntPtr.Zero ? 1 : 2;
                InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
                if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributes = Marshal.AllocHGlobal((IntPtr)size);
                if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                if (pseudoConsole != IntPtr.Zero && !UpdateProcThreadAttribute(attributes, 0, 0x00020016,
                        pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!UpdateProcThreadAttribute(attributes, 0, 0x00020009, profile.SecurityCapabilities,
                        (nuint)(IntPtr.Size * 2 + sizeof(uint) * 2), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());


                env = Marshal.AllocHGlobal(environment.Length);
                Marshal.Copy(environment, 0, env, environment.Length);
                // Do not inherit the launcher's standard handles. For ConPTY,
                // Windows supplies console handles through the attached HPCON.
                var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), Flags = pseudoConsole == IntPtr.Zero ? 0 : 0x100 }, AttributeList = attributes };
                var command = new StringBuilder("\"" + executable + "\"" + (arguments.Length == 0 ? "" : " " + arguments));
                if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        0x00080000 | 0x00000400 | 0x00000004 | (pseudoConsole == IntPtr.Zero ? 0x08000000u : 0u), env, workingDirectory, ref startup, out ProcessInformation info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                process = info.Process;
                thread = info.Thread;
                if (!AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                assigned = true;
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new WindowsSandboxProcess(job, process, profile);
                success = true;
                return result;
            }
            finally
            {
                if (thread != IntPtr.Zero) CloseHandle(thread);
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
                if (limitsPointer != IntPtr.Zero) Marshal.FreeHGlobal(limitsPointer);
                if (!success)
                {
                    if (process != IntPtr.Zero && !assigned) TerminateProcess(process, 1);
                    if (job != IntPtr.Zero) CloseHandle(job);
                    if (process != IntPtr.Zero) { WaitForSingleObject(process, 5000); CloseHandle(process); }
                    try { profile.Dispose(); } catch { /* Preserve process startup failure. */ }
                }
            }
        }

        public uint WaitForExit(int timeoutMilliseconds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (timeoutMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            uint wait = WaitForSingleObject(_process, (uint)timeoutMilliseconds);
            if (wait == 0x102) throw new TimeoutException("Sandbox process did not exit.");
            if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetExitCodeProcess(_process, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return exitCode;
        }

        public void Stop()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TerminateJobObject(_job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(_process, 5000) != 0)
                throw new InvalidOperationException("The sandbox process did not stop.");
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                if (!QueryInformationJobObject(_job, 1, out JobBasicAccountingInformation info,
                        (uint)Marshal.SizeOf<JobBasicAccountingInformation>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (info.ActiveProcesses == 0) break;
                if (timeout.Elapsed >= TimeSpan.FromSeconds(5))
                    throw new TimeoutException("Sandbox descendants did not stop.");
                Thread.Sleep(10);
            }
        }

        public IReadOnlyList<SandboxWorkspace.FileChange> StopAndCaptureChanges()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Workspace is null) throw new InvalidOperationException("This sandbox has no repository workspace.");
            Stop();
            return SandboxWorkspace.CaptureChanges(Workspace);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
            if (_process != IntPtr.Zero) { WaitForSingleObject(_process, 5000); CloseHandle(_process); _process = IntPtr.Zero; }
            _profile.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public string Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2Size;
            public IntPtr Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)] private struct JobBasicAccountingInformation
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
            out JobBasicAccountingInformation information, uint length, IntPtr returnLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr env, string directory, ref StartupInfoEx startup, out ProcessInformation info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
