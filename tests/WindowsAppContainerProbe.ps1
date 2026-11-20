# Research probe only. This does not start OpenCode or implement the sandbox.
# It creates a temporary AppContainer with no capabilities and checks whether
# a built-in Windows process can read a private test file or reach the network.
# Run on Windows from a normal user account: pwsh -File tests/WindowsAppContainerProbe.ps1
$ErrorActionPreference = 'Stop'

$probeSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class CodeBadgerAppContainerProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static void Run(string privateFile, string openCodeWrapper)
    {
        using (var profile = CodeBadger.Services.WindowsAppContainerProfile.Create())
        {
            IntPtr sid = profile.SecurityCapabilities;
            string cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
            using (var sandbox = CodeBadger.Services.WindowsSandboxProcess.Start(cmd,
                "/d /c if defined CODEBADGER_SANDBOX_PROBE (exit 1) else (exit 42)", Environment.SystemDirectory, "C:\\CodeBadgerSandboxProbe"))
            {
                if (sandbox.WaitForExit(15000) != 42) throw new Exception("Sandbox process launcher control failed.");
            }
            using (var sandbox = CodeBadger.Services.WindowsSandboxProcess.Start(cmd,
                "/d /c type \"" + privateFile + "\" >nul 2>&1", Environment.SystemDirectory, "C:\\CodeBadgerSandboxProbe"))
            {
                if (sandbox.WaitForExit(15000) == 0) throw new Exception("Sandbox launcher could read the private test file.");
            }
            Console.WriteLine("Product launcher blocked private file access: True");
            using (var sandbox = CodeBadger.Services.WindowsSandboxProcess.Start(cmd,
                "/d /c ping -n 30 127.0.0.1 >nul", Environment.SystemDirectory, "C:\\CodeBadgerSandboxProbe"))
            {
                sandbox.Stop();
                sandbox.WaitForExit(5000);
            }
            Console.WriteLine("Product launcher stops its job: True");
            if (RunProcess(sid, cmd, "\"" + cmd + "\" /d /c exit 42") != 42)
                throw new Exception("The command interpreter failed to run inside AppContainer; file probe is inconclusive.");
            uint fileResult = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c type \"" + privateFile + "\" >nul 2>&1");
            Console.WriteLine("Private file read blocked: " + (fileResult != 0));
            if (fileResult == 0) throw new Exception("AppContainer could read the private test file.");
            if (RunProcess(sid, cmd, "\"" + cmd + "\" /d /c cmd.exe /d /c exit 42") != 42)
                throw new Exception("Child process failed to run inside AppContainer; child file probe is inconclusive.");
            uint childResult = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c cmd.exe /d /c type \"" + privateFile + "\" >nul 2>&1");
            Console.WriteLine("Child process private file read blocked: " + (childResult != 0));
            if (childResult == 0) throw new Exception("Child process could read the private test file.");

            string previousValue = Environment.GetEnvironmentVariable("CODEBADGER_SANDBOX_PROBE");
            Environment.SetEnvironmentVariable("CODEBADGER_SANDBOX_PROBE", "test-value-only");
            try
            {
                uint environmentResult = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c if defined CODEBADGER_SANDBOX_PROBE (exit 42) else (exit 0)");
                Console.WriteLine("Host environment inherited: " + (environmentResult == 42));
                if (environmentResult != 42) throw new Exception("Environment inheritance probe was inconclusive.");

                byte[] environment = CodeBadger.Services.SandboxProcessEnvironment.CreateWindowsBlock(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "C:\\CodeBadgerSandboxProbe");
                string environmentText = Encoding.Unicode.GetString(environment);
                if (!environmentText.EndsWith("\0\0") || environmentText.IndexOf("CODEBADGER_SANDBOX_PROBE", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception("The environment block is malformed or contains the test secret.");
                uint cleanResult = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c if defined CODEBADGER_SANDBOX_PROBE (exit 42) else (exit 0)", environment);
                uint cleanChildResult = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c cmd.exe /d /c if defined CODEBADGER_SANDBOX_PROBE (exit 42) else (exit 0)", environment);
                uint cleanControl = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c if defined SYSTEMROOT (exit 42) else (exit 0)", environment);
                uint cleanHome = RunProcess(sid, cmd, "\"" + cmd + "\" /d /c if \"%USERPROFILE%\"==\"C:\\CodeBadgerSandboxProbe\" (exit 42) else (exit 0)", environment);
                if (cleanResult != 0 || cleanChildResult != 0 || cleanControl != 42 || cleanHome != 42)
                    throw new Exception("Clean environment probe failed or was inconclusive.");
                Console.WriteLine("Host environment excluded from sandbox and child: True");
            }
            finally { Environment.SetEnvironmentVariable("CODEBADGER_SANDBOX_PROBE", previousValue); }

            string powershell = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string control = Convert.ToBase64String(Encoding.Unicode.GetBytes("exit 42"));
            if (RunProcess(sid, powershell, "\"" + powershell + "\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + control) != 42)
                throw new Exception("PowerShell failed to run inside AppContainer; network probe is inconclusive.");
            using (var host = new System.Net.Sockets.TcpClient())
            {
                var pending = host.BeginConnect("1.1.1.1", 443, null, null);
                if (!pending.AsyncWaitHandle.WaitOne(3000)) throw new Exception("Host network control timed out; network probe is inconclusive.");
                host.EndConnect(pending);
            }
            string command = "$c=New-Object Net.Sockets.TcpClient; try {$a=$c.BeginConnect('1.1.1.1',443,$null,$null); if ($a.AsyncWaitHandle.WaitOne(3000)) {$c.EndConnect($a); exit 1}; exit 0} catch {exit 0} finally {$c.Dispose()}";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            uint networkResult = RunProcess(sid, powershell, "\"" + powershell + "\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded);
            Console.WriteLine("Outbound connection blocked: " + (networkResult == 0));
            if (networkResult != 0) throw new Exception("Outbound network probe failed or the connection was allowed.");

            if (!String.IsNullOrEmpty(openCodeWrapper))
            {
                string controlCommand = "\"" + cmd + "\" /d /c type \"" + openCodeWrapper + "\" >nul 2>&1";
                Console.WriteLine("Installed OpenCode wrapper readable: " + (RunProcess(sid, cmd, controlCommand) == 0));
            }
        }
    }

    private static uint RunProcess(IntPtr sid, string executable, string command, byte[] environment = null)
    {
        IntPtr list = IntPtr.Zero, environmentBlock = IntPtr.Zero;
        bool initialized = false;
        ProcessInformation process = new ProcessInformation();
        try
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            list = Marshal.AllocHGlobal((IntPtr)size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            if (!UpdateProcThreadAttribute(list, 0, 0x00020009, sid, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = list };
            if (environment != null)
            {
                environmentBlock = Marshal.AllocHGlobal(environment.Length);
                Marshal.Copy(environment, 0, environmentBlock, environment.Length);
            }
            if (!CreateProcessW(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000 | 0x00000400, environmentBlock, Environment.SystemDirectory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            uint wait = WaitForSingleObject(process.Process, 15000);
            if (wait != 0)
            {
                TerminateProcess(process.Process, 1);
                throw new Exception("Probe process did not finish successfully (wait result " + wait + ").");
            }
            if (!GetExitCodeProcess(process.Process, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return code;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (initialized) DeleteProcThreadAttributeList(list);
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
        }
    }
}
'@

$environmentSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxProcessEnvironment.cs')
$profileSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsAppContainerProfile.cs')
$launcherSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsSandboxProcess.cs')
$workspaceSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxWorkspace.cs')
$guardianSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\WindowsRepositoryAccessGuardian.cs')
Add-Type -TypeDefinition ($probeSource + [Environment]::NewLine + $environmentSource + [Environment]::NewLine + $profileSource + [Environment]::NewLine + $workspaceSource + [Environment]::NewLine + $guardianSource + [Environment]::NewLine + $launcherSource)

$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('CodeBadgerProbe-' + [guid]::NewGuid().ToString('N'))
$privateFile = Join-Path $directory 'private.txt'
try {
    $null = New-Item -ItemType Directory -Path $directory
    [System.IO.File]::WriteAllText($privateFile, 'test data only')
    $wrapperCommand = Get-Command opencode.cmd -ErrorAction SilentlyContinue
    $wrapper = if ($wrapperCommand) { $wrapperCommand.Source } else { '' }
    [CodeBadgerAppContainerProbe]::Run($privateFile, $wrapper)
} finally {
    if (Test-Path -LiteralPath $privateFile -PathType Leaf) { Remove-Item -LiteralPath $privateFile }
    if (Test-Path -LiteralPath $directory -PathType Container) { Remove-Item -LiteralPath $directory }
}
