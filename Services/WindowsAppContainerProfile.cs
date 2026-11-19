namespace CodeBadger.Services
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// A temporary Windows AppContainer identity with no capabilities. This is
    /// only one building block: it does not prepare a workspace, validate its
    /// contents, broker network requests or manage the lifetime of processes.
    /// Keep this object alive until all processes using the identity have exited.
    /// </summary>
    public sealed class WindowsAppContainerProfile : IDisposable
    {
        private IntPtr _sid;
        private IntPtr _securityCapabilities;
        private bool _disposed;

        private WindowsAppContainerProfile(string name, IntPtr sid)
        {
            Name = name;
            _sid = sid;
        }

        public string Name { get; }

        public string FolderPath { get; private set; } = string.Empty;
        public string Sid { get; private set; } = string.Empty;

        /// <summary>Pointer for PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES.</summary>
        public IntPtr SecurityCapabilities
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _securityCapabilities;
            }
        }

        public static WindowsAppContainerProfile Create()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("AppContainer requires Windows.");

            string name = "CodeBadger.Sandbox." + Guid.NewGuid().ToString("N");
            int result = CreateAppContainerProfile(name, name, "Temporary CodeBadger sandbox", IntPtr.Zero, 0, out IntPtr sid);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            if (sid == IntPtr.Zero)
            {
                DeleteAppContainerProfile(name);
                throw new InvalidOperationException("Windows returned an empty AppContainer identity.");
            }

            var profile = new WindowsAppContainerProfile(name, sid);
            try
            {
                profile._securityCapabilities = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSecurityCapabilities>());
                var capabilities = new NativeSecurityCapabilities { AppContainerSid = sid };
                Marshal.StructureToPtr(capabilities, profile._securityCapabilities, false);
                if (!ConvertSidToStringSidW(sid, out IntPtr stringSid))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    string sidText = Marshal.PtrToStringUni(stringSid)
                        ?? throw new InvalidOperationException("Windows returned an empty SID string.");
                    profile.Sid = sidText;
                    int folderResult = GetAppContainerFolderPath(sidText, out IntPtr folder);
                    if (folderResult < 0) Marshal.ThrowExceptionForHR(folderResult);
                    try
                    {
                        profile.FolderPath = Marshal.PtrToStringUni(folder)
                            ?? throw new InvalidOperationException("Windows returned an empty AppContainer folder.");
                    }
                    finally { Marshal.FreeCoTaskMem(folder); }
                }
                finally { LocalFree(stringSid); }
                return profile;
            }
            catch
            {
                try { profile.Dispose(); }
                catch { /* Preserve the original allocation failure. */ }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_securityCapabilities != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_securityCapabilities);
                _securityCapabilities = IntPtr.Zero;
            }
            if (_sid != IntPtr.Zero)
            {
                FreeSid(_sid);
                _sid = IntPtr.Zero;
            }

            // Windows can release process/registry references after process
            // termination returns. Retry only this exact profile API; never
            // recursively delete a user-controlled path as a cleanup fallback.
            int result = DeleteAppContainerProfile(Name);
            for (int attempt = 0; result < 0 && attempt < 3; attempt++)
            {
                Thread.Sleep(100);
                result = DeleteAppContainerProfile(Name);
            }
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            _disposed = true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSecurityCapabilities
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;
            public uint CapabilityCount;
            public uint Reserved;
        }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CreateAppContainerProfile(string name, string displayName, string description,
            IntPtr capabilities, uint count, out IntPtr sid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int DeleteAppContainerProfile(string name);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern IntPtr FreeSid(IntPtr sid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
