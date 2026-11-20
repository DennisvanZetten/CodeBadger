namespace CodeBadger.Services
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Collections.Generic;

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
        private IntPtr _runtimeCapability;
        private readonly List<IntPtr> _runtimeCapabilitySids = new List<IntPtr>();

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

        /// <summary>LPAC needs explicit read-only Windows registry access for Winsock
        /// initialization. This is not a network capability or a filesystem grant.</summary>
        public void EnableLpacRuntime()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtimeCapability != IntPtr.Zero) throw new InvalidOperationException("Runtime already configured.");
            string[] names = new[] { "registryRead" };
            int stride = Marshal.SizeOf<SidAndAttributes>();
            _runtimeCapability = Marshal.AllocHGlobal(stride * names.Length);
            for (int n = 0; n < names.Length; n++)
            {
                if (!DeriveCapabilitySidsFromName(names[n], out IntPtr groups, out uint groupCount, out IntPtr capabilities, out uint count))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                if (count != 1) throw new InvalidOperationException("Windows returned an unexpected registry runtime capability.");
                IntPtr sid = Marshal.ReadIntPtr(capabilities);
                uint length = GetLengthSid(sid);
                IntPtr copiedSid = Marshal.AllocHGlobal((int)length);
                _runtimeCapabilitySids.Add(copiedSid);
                if (!CopySid(length, copiedSid, sid)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Marshal.StructureToPtr(new SidAndAttributes { Sid = copiedSid, Attributes = 4 }, IntPtr.Add(_runtimeCapability, stride * n), false);
                }
                finally
                {
                for (int i = 0; i < groupCount; i++) LocalFree(Marshal.ReadIntPtr(groups, i * IntPtr.Size));
                for (int i = 0; i < count; i++) LocalFree(Marshal.ReadIntPtr(capabilities, i * IntPtr.Size));
                LocalFree(groups); LocalFree(capabilities);
                }
            }
            Marshal.StructureToPtr(new NativeSecurityCapabilities { AppContainerSid = _sid, Capabilities = _runtimeCapability, CapabilityCount = (uint)names.Length }, _securityCapabilities, false);
        }

        public static WindowsAppContainerProfile Create()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("AppContainer requires Windows.");

            // Keep both moniker and display name compact. Packaged launches can
            // have longer profile-storage paths than the standalone test host.
            string name = "cb." + Guid.NewGuid().ToString("N");
            int result = CreateAppContainerProfile(name, name, "Temporary CodeBadger sandbox", IntPtr.Zero, 0, out IntPtr sid);
            if (result < 0)
            {
                if (sid != IntPtr.Zero) FreeSid(sid);
                throw new InvalidOperationException(
                    $"Windows could not create the sandbox profile (CreateAppContainerProfile, HRESULT 0x{result:X8}; name length {name.Length}). " +
                    "OpenCode was not started. " +
                    "Please report this error together with whether CodeBadger was launched from Visual Studio or its installed MSIX package.",
                    Marshal.GetExceptionForHR(result));
            }
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
            if (_runtimeCapability != IntPtr.Zero) { Marshal.FreeHGlobal(_runtimeCapability); _runtimeCapability = IntPtr.Zero; }
            foreach (IntPtr sid in _runtimeCapabilitySids) Marshal.FreeHGlobal(sid);
            _runtimeCapabilitySids.Clear();
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
        [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
        [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern bool DeriveCapabilitySidsFromName(string name, out IntPtr groups, out uint groupCount, out IntPtr capabilities, out uint count);
        [DllImport("advapi32.dll")] private static extern uint GetLengthSid(IntPtr sid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CopySid(uint length, IntPtr destination, IntPtr source);

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
