#nullable enable annotations
namespace CodeBadger.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.AccessControl;
    using System.Security.Principal;
    using System.Text.Json;
    using System.Diagnostics;
    using System.Threading;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// Fixed-file, non-inheriting ACL lease. Directory handles prevent path replacement;
    /// file handles prevent delete/rename. No directory write, DELETE, WRITE_DAC or
    /// WRITE_OWNER is granted. The independent host guardian owns this object.
    /// Journal recovery is handle/identity based and never follows reparse points.
    /// </summary>
    public sealed class WindowsRepositoryAccessLease : IDisposable
    {
        public sealed class Entry
        {
            public string Path { get; set; } = "";
            public uint Volume { get; set; }
            public ulong Id { get; set; }
            public string Original { get; set; } = "";
            public string Granted { get; set; } = "";
            public bool Directory { get; set; }
        }
        public sealed class Journal
        {
            public int Version { get; set; } = 1;
            public string Sid { get; set; } = "";
            public string Root { get; set; } = "";
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }
        private readonly string _journalPath;
        private readonly Journal _journal;
        private readonly List<SafeFileHandle> _handles = new List<SafeFileHandle>();
        private bool _disposed;
        private bool _handlesClosed;
        private WindowsRepositoryAccessLease(string path, Journal journal) { _journalPath = path; _journal = journal; }
        private const uint ReadControl = 0x20000, WriteDac = 0x40000, WriteOwner = 0x80000;
        private const uint DaclInformation = 4, LabelInformation = 16;
        // Read/write data and append, read attributes/EA, read security and synchronize.
        // Deliberately excludes DELETE and ACL/owner changes. Generic file writes
        // require EA/attribute rights; process FSCTL mitigation blocks reparse creation.
        private const int FileRights = 0x12019f;
        private const int DirectoryRights = 0x1200a9;

        public static WindowsRepositoryAccessLease Grant(string root, string[] files, string sid, string journalPath)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(journalPath)) throw new ArgumentException("Absolute paths required.");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (files.Length == 0 || files.Length > 10000) throw new InvalidDataException("Direct mode needs 1–10,000 reviewed files.");
            var identity = new SecurityIdentifier(sid);
            if (!sid.StartsWith("S-1-15-2-", StringComparison.Ordinal)) throw new ArgumentException("AppContainer SID required.");
            if (Within(journalPath, root)) throw new UnauthorizedAccessException("Recovery journal must be outside the repository.");
            var lease = new WindowsRepositoryAccessLease(journalPath, new Journal { Sid = sid, Root = root });
            try
            {
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) || file.Contains(':') ||
                        file.Replace('/', '\\').Split('\\').Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith('.') || p.EndsWith(' ')))
                        throw new UnauthorizedAccessException("Non-canonical selection.");
                    string path = Path.GetFullPath(Path.Combine(root, file));
                    if (!Within(path, root) || !paths.Add(path)) throw new UnauthorizedAccessException("Invalid or duplicate selection.");
                    var parent = new DirectoryInfo(Path.GetDirectoryName(path)!);
                    while (parent != null) { directories.Add(parent.FullName); parent = parent.Parent; }
                }
                // Lock all ancestors before opening any selected file. Ancestors outside
                // the root are validated/locked but never receive an ACE or a new label.
                foreach (string directory in directories.OrderBy(p => p.Length))
                    lease.Add(directory, true, Within(directory, root), identity);
                foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) lease.Add(path, false, true, identity);
                lease.Save(); // durable originals BEFORE the first change, including partial grants
                for (int i = 0; i < lease._journal.Entries.Count; i++)
                {
                    var entry = lease._journal.Entries[i];
                    if (entry.Original == entry.Granted) continue;
                    Apply(lease._handles[i], entry.Granted, entry.Directory);
                    if (!Equivalent(Read(lease._handles[i]), entry.Granted)) throw new IOException("Windows did not enforce the requested repository security.");
                }
                return lease;
            }
            catch (Exception original)
            {
                try { lease.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("Direct repository preparation and ACL recovery failed. Recovery journal: " + journalPath, original, cleanup); }
                throw;
            }
        }

        private void Add(string path, bool directory, bool grant, SecurityIdentifier sid)
        {
            var handle = Open(path, directory, grant);
            try
            {
                var info = Information(handle, directory);
                string original = Read(handle);
                var descriptor = new RawSecurityDescriptor(Convert.FromBase64String(original), 0);
                if (descriptor.DiscretionaryAcl == null || !new CommonSecurityDescriptor(directory, false, descriptor).IsDiscretionaryAclCanonical)
                    throw new UnauthorizedAccessException("Null or non-canonical repository ACL is unsupported.");
                if (descriptor.Owner == sid) throw new UnauthorizedAccessException("Unexpected repository owner.");
                string changed = original;
                if (grant)
                {
                    foreach (GenericAce ace in descriptor.DiscretionaryAcl)
                        if (ace is KnownAce known && known.SecurityIdentifier == sid) throw new UnauthorizedAccessException("Existing sandbox ACE is unsupported.");
                    descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied,
                        0xd0000 | (directory ? 0x110 : 0), sid, false, null)); // DELETE, WRITE_DAC, WRITE_OWNER
                    // Explicit allow before inherited ACEs; all existing ACEs/flags stay unchanged.
                    int index = 0;
                    while (index < descriptor.DiscretionaryAcl.Count && (descriptor.DiscretionaryAcl[index].AceFlags & AceFlags.Inherited) == 0) index++;
                    descriptor.DiscretionaryAcl.InsertAce(index, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                        directory ? DirectoryRights : FileRights, sid, false, null));
                    if (!directory)
                    {
                        // LABEL_SECURITY_INFORMATION fetches only MIC labels, not audit ACEs.
                        // Unknown/high labels are refused rather than downgrading protected data.
                        if (descriptor.SystemAcl != null)
                        {
                            foreach (GenericAce ace in descriptor.SystemAcl)
                            {
                                byte[] data = new byte[ace.BinaryLength]; ace.GetBinaryForm(data, 0);
                                if (data.Length != 20 || data[0] != 0x11 || BitConverter.ToInt32(data, 4) != 1 ||
                                    new SecurityIdentifier(data, 8).Value != "S-1-16-8192")
                                    throw new UnauthorizedAccessException("Only ordinary medium-integrity files support direct mode.");
                            }
                        }
                        descriptor.SystemAcl = new RawAcl(2, 1);
                        // Mandatory label ACE must use its native ACE type, not an audit ACE.
                        byte[] labelBytes = new byte[20];
                        labelBytes[0] = 0x11; labelBytes[2] = 20; labelBytes[4] = 1;
                        new SecurityIdentifier("S-1-16-4096").GetBinaryForm(labelBytes, 8);
                        descriptor.SystemAcl.InsertAce(0, GenericAce.CreateFromBinaryForm(labelBytes, 0));
                        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.SystemAclPresent);
                    }
                    changed = Encode(descriptor);
                }
                _journal.Entries.Add(new Entry { Path = path, Directory = directory, Volume = info.Volume,
                    Id = ((ulong)info.IndexHigh << 32) | info.IndexLow, Original = original, Granted = changed });
                _handles.Add(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        public static void Recover(string journalPath)
        {
            using var input = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 32 * 1024 * 1024) throw new InvalidDataException("Recovery journal is too large.");
            var journal = JsonSerializer.Deserialize<Journal>(input) ?? throw new InvalidDataException("Invalid recovery journal.");
            if (journal.Version != 1 || !journal.Sid.StartsWith("S-1-15-2-", StringComparison.Ordinal) || journal.Entries.Count > 20000 ||
                !Path.IsPathFullyQualified(journal.Root)) throw new InvalidDataException("Invalid recovery journal.");
            var lease = new WindowsRepositoryAccessLease(journalPath, journal);
            try
            {
                foreach (var entry in journal.Entries)
                {
                    if (!Path.IsPathFullyQualified(entry.Path) ||
                        (!Within(entry.Path, journal.Root) && (!entry.Directory || !Within(journal.Root, entry.Path) || entry.Granted != entry.Original)))
                        throw new UnauthorizedAccessException("Recovery entry leaves its repository.");
                    var handle = Open(entry.Path, entry.Directory, entry.Granted != entry.Original);
                    lease._handles.Add(handle);
                    var info = Information(handle, entry.Directory);
                    if (info.Volume != entry.Volume || (((ulong)info.IndexHigh << 32) | info.IndexLow) != entry.Id)
                        throw new UnauthorizedAccessException("Recovery path identity changed; no replacement file was modified.");
                }
                input.Dispose();
                lease.Dispose();
            }
            finally { foreach (var handle in lease._handles) handle.Dispose(); }
        }

        private void Save()
        {
            using var stream = new FileStream(_journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, _journal);
            stream.Flush(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_handlesClosed) throw new IOException("Repository recovery must be retried from its durable journal: " + _journalPath);
            var errors = new List<Exception>();
            try
            {
                for (int i = _handles.Count - 1; i >= 0; i--)
                {
                    var entry = _journal.Entries[i];
                    if (entry.Granted == entry.Original) continue;
                    try
                    {
                        string current = Read(_handles[i]);
                        if (Equivalent(current, entry.Original)) continue; // grant was never reached/already recovered
                        if (!Equivalent(current, entry.Granted))
                        {
                            // Do not overwrite unrelated user ACL edits. Remove only this lease's
                            // explicit SID rule; retain the journal for supervised MIC recovery.
                            var descriptor = new RawSecurityDescriptor(Convert.FromBase64String(current), 0);
                            if (descriptor.DiscretionaryAcl != null)
                                for (int a = descriptor.DiscretionaryAcl.Count - 1; a >= 0; a--)
                                    if (descriptor.DiscretionaryAcl[a] is KnownAce ace && ace.SecurityIdentifier.Value == _journal.Sid)
                                        descriptor.DiscretionaryAcl.RemoveAce(a);
                            Apply(_handles[i], Encode(descriptor), true);
                            throw new IOException("Repository security changed concurrently. Sandbox ACE removed; recovery needs inspection: " + entry.Path);
                        }
                        Apply(_handles[i], entry.Original, entry.Directory);
                        if (!Equivalent(Read(_handles[i]), entry.Original)) throw new IOException("Repository security restoration could not be verified: " + entry.Path);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
                if (errors.Count > 0) throw new AggregateException("Repository access recovery failed. Keep the recovery journal: " + _journalPath, errors);
                if (File.Exists(_journalPath)) File.Delete(_journalPath);
                _disposed = true;
            }
            finally { foreach (var handle in _handles) handle.Dispose(); _handlesClosed = true; }
        }

        private static SafeFileHandle Open(string path, bool directory, bool modify)
        {
            var handle = CreateFileW(path, ReadControl | 0x80u | (modify ? WriteDac | (directory ? 0 : WriteOwner) : 0),
                directory ? 1u : 3u, IntPtr.Zero, 3, 0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return handle;
        }
        private static FileInformation Information(SafeFileHandle handle, bool directory)
        {
            if (!GetFileInformationByHandle(handle, out FileInformation info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory || (!directory && info.Links != 1))
                throw new UnauthorizedAccessException("Reparse points and hardlinks do not support direct repository mode.");
            return info;
        }
        private static string Read(SafeFileHandle handle)
        {
            uint error = GetSecurityInfo(handle, 1, 1 | 2 | DaclInformation | LabelInformation, out _, out _, out _, out _, out IntPtr descriptor);
            if (error != 0) throw new Win32Exception((int)error);
            try
            {
                uint length = GetSecurityDescriptorLength(descriptor);
                byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return Encode(new RawSecurityDescriptor(bytes, 0));
            }
            finally { LocalFree(descriptor); }
        }
        private static string Encode(RawSecurityDescriptor descriptor)
        {
            byte[] bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            return Convert.ToBase64String(bytes);
        }
        private static bool Equivalent(string left, string right)
        {
            var a = new RawSecurityDescriptor(Convert.FromBase64String(left), 0);
            var b = new RawSecurityDescriptor(Convert.FromBase64String(right), 0);
            // Windows may set auto-inheritance bookkeeping flags when applying a DACL.
            // Protection, presence and every ACE (including its inheritance flags) must match.
            const ControlFlags ignored = ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired |
                ControlFlags.SystemAclAutoInherited | ControlFlags.SystemAclAutoInheritRequired | ControlFlags.OwnerDefaulted |
                ControlFlags.GroupDefaulted | ControlFlags.DiscretionaryAclDefaulted | ControlFlags.SystemAclDefaulted;
            a.SetFlags(a.ControlFlags & ~ignored); b.SetFlags(b.ControlFlags & ~ignored);
            if (a.SystemAcl == null || a.SystemAcl.Count == 0) { a.SystemAcl = null; a.SetFlags(a.ControlFlags & ~ControlFlags.SystemAclPresent); }
            if (b.SystemAcl == null || b.SystemAcl.Count == 0) { b.SystemAcl = null; b.SetFlags(b.ControlFlags & ~ControlFlags.SystemAclPresent); }
            return Encode(a) == Encode(b);
        }
        private static void Apply(SafeFileHandle handle, string encoded, bool directory)
        {
            var raw = new RawSecurityDescriptor(Convert.FromBase64String(encoded), 0);
            if (!directory && raw.SystemAcl == null)
            {
                raw.SystemAcl = new RawAcl(2, 0);
                raw.SetFlags(raw.ControlFlags | ControlFlags.SystemAclPresent);
            }
            byte[] bytes = Convert.FromBase64String(Encode(raw));
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                IntPtr descriptor = pin.AddrOfPinnedObject();
                if (!GetSecurityDescriptorDacl(descriptor, out bool present, out IntPtr dacl, out _) || !present)
                    throw new InvalidDataException("Recovery descriptor has no DACL.");
                GetSecurityDescriptorSacl(descriptor, out _, out IntPtr label, out _);
                // No inheritable ACEs are added. DACL protection/inheritance flags are not changed.
                uint error = SetSecurityInfo(handle, 1, DaclInformation | (directory ? 0 : LabelInformation),
                    IntPtr.Zero, IntPtr.Zero, dacl, directory ? IntPtr.Zero : label);
                if (error != 0) throw new Win32Exception((int)error);
            }
            finally { pin.Free(); }
        }
        private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr acl, out bool defaulted);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr acl, out bool defaulted);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    }

    public sealed class RepositoryGuardianJob : IDisposable
    {
        private IntPtr _handle;
        public RepositoryGuardianJob(string name)
        {
            _handle = OpenJobObjectW(0x8 | 0x4, false, name); // terminate/query only
            if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Stop()
        {
            if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (!QueryInformationJobObject(_handle, 1, out Accounting info, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (info.Active == 0) return;
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Sandbox descendants did not stop; ACL recovery remains pending.");
                Thread.Sleep(10);
            }
        }
        public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct Accounting
        {
            public long User, Kernel, PeriodUser, PeriodKernel;
            public uint Faults, Total, Active, Terminated;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int type, out Accounting info, uint length, IntPtr result);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
