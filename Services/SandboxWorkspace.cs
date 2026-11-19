#nullable enable annotations
namespace CodeBadger.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// Prepares an explicitly selected set of files before any sandbox process
    /// starts. Does not discover secrets by content or grant OS access rights.
    /// The caller must review the selected content and keep the destination
    /// private from untrusted processes throughout preparation.
    /// </summary>
    public static class SandboxWorkspace
    {
        public sealed record PreparedFile(string RelativePath, string Sha256, long Length);
        public sealed record PreparedWorkspace(string Directory, IReadOnlyList<PreparedFile> Files);
        public sealed record BrowserEntry(string Name, string RelativePath, bool IsDirectory, bool IsSensitive, IReadOnlyList<BrowserEntry> Children);

        /// <summary>Lists a bounded tree without following links or exposing sensitive paths as selectable entries.</summary>
        public static IReadOnlyList<BrowserEntry> GetBrowserTree(string repositoryPath)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (!Path.IsPathFullyQualified(repositoryPath) || !Directory.Exists(repositoryPath))
                throw new DirectoryNotFoundException("Repository folder is unavailable.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            int visited = 0;
            return ListDirectory(root, string.Empty, 0, ref visited);
        }

        private static IReadOnlyList<BrowserEntry> ListDirectory(string root, string relative, int depth, ref int visited)
        {
            if (depth > 20) return [];
            string directory = relative.Length == 0 ? root : Path.Combine(root, relative);
            var result = new List<BrowserEntry>();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                if (++visited > 10000) throw new InvalidDataException("Repository tree exceeds the 10,000 entry browser limit.");
                string name = Path.GetFileName(path);
                string childRelative = relative.Length == 0 ? name : Path.Combine(relative, name);
                if (IsSensitivePath(childRelative)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                var children = isDirectory ? ListDirectory(root, childRelative, depth + 1, ref visited) : [];
                result.Add(new BrowserEntry(name, childRelative, isDirectory, false, children));
            }
            return result;
        }

        public sealed class FileChange
        {
            private readonly byte[] _content;
            public string RelativePath { get; }
            public string OriginalSha256 { get; }
            public string? UpdatedSha256 { get; }
            public bool Deleted => UpdatedSha256 is null;
            public byte[] GetContent() => (byte[])_content.Clone();

            internal FileChange(string relativePath, string originalSha256, byte[]? content)
            {
                RelativePath = relativePath;
                OriginalSha256 = originalSha256;
                _content = content ?? Array.Empty<byte>();
                UpdatedSha256 = content is null ? null : Convert.ToHexString(SHA256.HashData(content));
            }
        }

        /// <summary>
        /// Applies one explicitly approved modification to an existing regular
        /// file. Refuses conflicts, links and deletions. The original is backed
        /// up before writing through the same locked handle used for validation.
        /// Backup parent must be host-private and outside the repository tree.
        /// Multi-file application is deliberately not an atomic transaction.
        /// </summary>
        public static string ApplyModification(string repositoryPath, FileChange change, string backupParent)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (change.Deleted) throw new NotSupportedException("Deletion requires a separate reviewed operation.");
            if (!Path.IsPathFullyQualified(repositoryPath) || !Path.IsPathFullyQualified(backupParent))
                throw new ArgumentException("Absolute repository and backup paths are required.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            string backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupParent));
            if (Within(root, backupRoot) || Within(backupRoot, root))
                throw new ArgumentException("Repository and backup trees must not overlap.");
            byte[] content = change.GetContent();
            if (content.Length > 16 * 1024 * 1024) throw new InvalidDataException("Modification exceeds size limit.");
            string relative = ValidateSelection(change.RelativePath);
            var locks = new List<SafeFileHandle>();
            try
            {
                LockAncestors(root, locks);
                LockAncestors(backupRoot, locks);
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!Within(target, root)) throw new UnauthorizedAccessException("Target leaves the repository.");
                LockAncestors(Path.GetDirectoryName(target)!, locks);
                using var handle = CreateFileW(target, 0x80000000u | 0x40000000u, 0, IntPtr.Zero, 3, 0x00200000u, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!GetFileInformationByHandle(handle, out FileInformation info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
                    throw new UnauthorizedAccessException("Linked targets cannot be modified.");
                long length = ((long)info.SizeHigh << 32) | info.SizeLow;
                if (length > 16 * 1024 * 1024) throw new InvalidDataException("Original file exceeds size limit.");
                using var targetStream = new FileStream(handle, FileAccess.ReadWrite);
                byte[] original = new byte[(int)length];
                targetStream.ReadExactly(original);
                if (targetStream.ReadByte() != -1 || !Convert.ToHexString(SHA256.HashData(original)).Equals(change.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The original file changed after the sandbox started. Modification was not applied.");
                string backup = Path.Combine(backupRoot, "sandbox-original-" + Guid.NewGuid().ToString("N") + ".bak");
                using (var backupStream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    backupStream.Write(original);
                    backupStream.Flush(flushToDisk: true);
                }
                try
                {
                    targetStream.Position = 0;
                    targetStream.Write(content);
                    targetStream.SetLength(content.Length);
                    targetStream.Flush(flushToDisk: true);
                }
                catch (Exception writeError)
                {
                    try
                    {
                        targetStream.Position = 0;
                        targetStream.Write(original);
                        targetStream.SetLength(original.Length);
                        targetStream.Flush(flushToDisk: true);
                    }
                    catch (Exception restoreError)
                    {
                        throw new IOException("Modification and restoration failed. Original backup: " + backup,
                            new AggregateException(writeError, restoreError));
                    }
                    throw new IOException("Modification failed; original restored. Backup: " + backup, writeError);
                }
                return backup;
            }
            finally { foreach (var handle in locks) handle.Dispose(); }
        }

        /// <summary>
        /// Captures changes only to originally selected paths. Caller must first
        /// stop all sandbox writers. Does not write to the original repository;
        /// returned bytes remain usable after the temporary profile is deleted.
        /// New files and removed parent directories are not supported yet.
        /// </summary>
        public static IReadOnlyList<FileChange> CaptureChanges(PreparedWorkspace workspace)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var locks = new List<SafeFileHandle>();
            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Directory));
                LockAncestors(root, locks);
                var changes = new List<FileChange>();
                long total = 0;
                if (workspace.Files.Count > 10000) throw new InvalidDataException("Workspace manifest exceeds file limit.");
                foreach (var original in workspace.Files)
                {
                    string relative = ValidateSelection(original.RelativePath);
                    string file = Path.GetFullPath(Path.Combine(root, relative));
                    if (!Within(file, root)) throw new UnauthorizedAccessException("Workspace path leaves its root.");
                    LockAncestors(Path.GetDirectoryName(file)!, locks);
                    SafeFileHandle handle;
                    try { handle = OpenLocked(file, isDirectory: false); }
                    catch (Win32Exception error) when (error.NativeErrorCode == 2)
                    {
                        changes.Add(new FileChange(relative, original.Sha256, null));
                        continue;
                    }
                    using (handle)
                    {
                        if (!GetFileInformationByHandle(handle, out FileInformation info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
                            throw new UnauthorizedAccessException("Linked output files cannot be reviewed.");
                        long length = ((long)info.SizeHigh << 32) | info.SizeLow;
                        if (length > 16 * 1024 * 1024 || total + length > 256 * 1024 * 1024)
                            throw new InvalidDataException("Workspace output exceeds size limits.");
                        using var input = new FileStream(handle, FileAccess.Read);
                        using var content = new MemoryStream();
                        byte[] buffer = new byte[8192];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (content.Length + count > 16 * 1024 * 1024 || total + content.Length + count > 256 * 1024 * 1024)
                                throw new InvalidDataException("Workspace output changed or exceeds limits.");
                            content.Write(buffer, 0, count);
                        }
                        if (content.Length != length) throw new IOException("Workspace output changed while capturing.");
                        total += length;
                        var change = new FileChange(relative, original.Sha256, content.ToArray());
                        if (!string.Equals(change.UpdatedSha256, original.Sha256, StringComparison.OrdinalIgnoreCase))
                            changes.Add(change);
                    }
                }
                return changes.AsReadOnly();
            }
            finally { foreach (var handle in locks) handle.Dispose(); }
        }

        public static PreparedWorkspace Create(string repositoryPath, string privateParent, IEnumerable<string> selectedFiles)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Workspace preparation currently requires Windows.");
            if (!Path.IsPathFullyQualified(repositoryPath) || !Path.IsPathFullyQualified(privateParent))
                throw new ArgumentException("Absolute repository and private destination paths are required.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateParent));
            if (!Directory.Exists(root) || !Directory.Exists(parent)) throw new DirectoryNotFoundException();
            if (Within(parent, root) || Within(root, parent))
                throw new ArgumentException("Repository and destination trees must not overlap.");
            string[] selections = ExpandSelections(root, selectedFiles.Select(ValidateSelection));
            if (selections.Length > 10000 || selections.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selections.Length)
                throw new ArgumentException("Too many files or duplicate file selections.");

            var locks = new List<SafeFileHandle>();
            try
            {
                LockAncestors(root, locks);
                LockAncestors(parent, locks);
                string destination = Path.Combine(parent, "workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(destination);
                var files = new List<PreparedFile>();
                long totalBytes = 0;
                // Failure leaves only this newly-created, private directory.
                // It is never returned as a prepared workspace. Its owner may
                // remove it after inspecting cleanup errors; no source is changed.
                foreach (string relative in selections)
                {
                    string source = Path.GetFullPath(Path.Combine(root, relative));
                    if (!Within(source, root)) throw new UnauthorizedAccessException("Selection leaves the repository.");
                    LockAncestors(Path.GetDirectoryName(source)!, locks);
                    using var handle = OpenLocked(source, isDirectory: false);
                    if (!GetFileInformationByHandle(handle, out FileInformation info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || info.Links != 1)
                        throw new UnauthorizedAccessException("Linked files are not allowed in the workspace.");
                    long length = ((long)info.SizeHigh << 32) | info.SizeLow;
                    const long maximumFile = 16 * 1024 * 1024;
                    const long maximumTotal = 256 * 1024 * 1024;
                    if (length > maximumFile || totalBytes + length > maximumTotal)
                        throw new InvalidDataException("Selected files exceed workspace size limits.");
                    string target = Path.Combine(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = new FileStream(handle, FileAccess.Read);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[8192];
                    int count;
                    long copied = 0;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copied += count;
                        if (copied > maximumFile || totalBytes + copied > maximumTotal)
                            throw new InvalidDataException("Selected files changed or exceed workspace size limits.");
                        hash.AppendData(buffer, 0, count);
                        output.Write(buffer, 0, count);
                    }
                    if (copied != length) throw new IOException("Source changed during workspace preparation.");
                    files.Add(new PreparedFile(relative, Convert.ToHexString(hash.GetHashAndReset()), copied));
                    totalBytes += copied;
                }
                return new PreparedWorkspace(destination, files.AsReadOnly());
            }
            finally { foreach (var handle in locks) handle.Dispose(); }
        }

        private static string ValidateSelection(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
                throw new ArgumentException("Only relative file paths are allowed.");
            string[] parts = relative.Replace('/', '\\').Split('\\');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Non-canonical paths are not allowed.");
                string lower = part.ToLowerInvariant();
                string stem = lower.Split('.')[0];
                if (new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(stem))
                    throw new ArgumentException("Windows device paths are not allowed.");
                if (IsSensitiveSegment(lower))
                    throw new UnauthorizedAccessException("Sensitive or agent configuration paths cannot be copied.");
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        private static string[] ExpandSelections(string root, IEnumerable<string> selections)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int visited = 0;
            foreach (string selection in selections)
            {
                string path = Path.GetFullPath(Path.Combine(root, selection));
                if (!Within(path, root)) throw new UnauthorizedAccessException("Selection leaves the repository.");
                if (File.Exists(path)) { files.Add(selection); continue; }
                if (!Directory.Exists(path)) throw new FileNotFoundException("Selected repository entry no longer exists.", selection);
                ExpandDirectory(path, selection, files, 0, ref visited);
            }
            return files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void ExpandDirectory(string directory, string relative, HashSet<string> files, int depth, ref int visited)
        {
            if (depth > 20) throw new InvalidDataException("Selected directory nesting exceeds the sandbox limit.");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++visited > 10000) throw new InvalidDataException("Selected directories exceed the 10,000 entry sandbox limit.");
                string name = Path.GetFileName(path);
                string child = Path.Combine(relative, name);
                if (IsSensitivePath(child)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) ExpandDirectory(path, child, files, depth + 1, ref visited);
                else files.Add(child);
            }
        }

        private static bool IsSensitivePath(string relative) => relative.Replace('/', '\\').Split('\\').Any(part => IsSensitiveSegment(part.ToLowerInvariant()));

        private static bool IsSensitiveSegment(string lower)
        {
            return lower is ".git" or ".hg" or ".svn" or ".ssh" or ".aws" or ".azure" or ".opencode" or ".claude" or ".codex" or
                ".mcp.json" or ".npmrc" or ".pypirc" or ".netrc" or "opencode.json" or "opencode.jsonc" ||
                lower.Contains(".env") || lower.Contains("secret") || lower.Contains("credential") || lower.StartsWith("id_rsa") || lower.StartsWith("id_ed25519") ||
                new[] { ".pem", ".key", ".pfx", ".p12", ".kdbx" }.Contains(Path.GetExtension(lower));
        }

        private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void LockAncestors(string directory, List<SafeFileHandle> locks)
        {
            var parents = new Stack<string>();
            var current = new DirectoryInfo(directory);
            while (current is not null) { parents.Push(current.FullName); current = current.Parent; }
            foreach (string path in parents)
            {
                var handle = OpenLocked(path, isDirectory: true);
                if (!GetFileInformationByHandle(handle, out FileInformation info))
                {
                    int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
                }
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                { handle.Dispose(); throw new UnauthorizedAccessException("Reparse-point directories are not supported."); }
                locks.Add(handle);
            }
        }

        private static SafeFileHandle OpenLocked(string path, bool isDirectory)
        {
            // OPEN_REPARSE_POINT prevents following a final-component symlink.
            // No write/delete sharing prevents replacement of held source paths.
            var handle = CreateFileW(path, isDirectory ? 0u : 0x80000000u, 1, IntPtr.Zero, 3,
                0x00200000u | (isDirectory ? 0x02000000u : 0u), IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return handle;
        }

        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    }
}
