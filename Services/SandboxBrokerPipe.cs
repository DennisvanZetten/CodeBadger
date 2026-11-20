using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeBadger.Services;

/// <summary>
/// One-connection host IPC transport. Only the selected AppContainer can invoke
/// the handler. Uses bounded length-prefixed UTF-8 messages, never impersonates
/// during host operations. The handler must bind repository, endpoint and key
/// from host session state, not accept them from the untrusted client message.
/// </summary>
public sealed class SandboxBrokerPipe : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly string _expectedSid;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    private int _started;
    private readonly int _maxMessages;
    public string Name { get; }
    public string HostName { get; }

    public SandboxBrokerPipe(string appContainerSid, int maxMessages = 1000)
    {
        if (maxMessages is < 1 or > 432000) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        _maxMessages = maxMessages;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var sid = new SecurityIdentifier(appContainerSid);
        if (!sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal))
            throw new ArgumentException("An AppContainer package SID is required.");
        _expectedSid = sid.Value;
        Name = "LOCAL\\CodeBadger.Broker." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new InvalidOperationException("Current user identity is unavailable.");
        // Low mandatory integrity permits the sandbox to write. Protected DACL
        // permits only host user and this AppContainer; token validation below
        // still rejects ordinary processes running as the same host user.
        string sddl = "D:P(A;;GA;;;" + user + ")(A;;GRGW;;;" + _expectedSid + ")S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // LOCAL names are redirected inside AppContainer. The host must
            // address that exact namespace, not its own logon-session LOCAL pipe.
            HostName = "Sessions\\" + System.Diagnostics.Process.GetCurrentProcess().SessionId +
                "\\AppContainerNamedObjects\\" + _expectedSid + "\\" + Name.Substring("LOCAL\\".Length);
            SafePipeHandle handle = CreateNamedPipeW("\\\\.\\pipe\\" + HostName,
                0x00000003 | 0x40000000 | 0x00080000, // duplex, overlapped, first instance
                0x00000008, // byte mode, reject remote clients
                1, 8192, 8192, 0, ref security);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
            }
            try { _pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    public async Task ServeAsync(Func<string, CancellationToken, Task<string>> handler, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Pipe has already started.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        try
        {
            await _pipe.WaitForConnectionAsync(lifetime.Token);
            byte[] header = new byte[4];
            var encoding = new UTF8Encoding(false, true);
            // Authenticate after the first byte, before allocating or receiving
            // an entire untrusted request. Reading first is required for pipe
            // impersonation to obtain the client's security context.
            using (var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _pipe.ReadExactlyAsync(header.AsMemory(0, 1), authenticationTimeout.Token);
                ValidateClient();
            }
            for (int messages = 0; messages < _maxMessages; messages++)
            {
                // Waiting for the next user prompt is not an in-flight request.
                // Keep the authenticated session alive until explicit shutdown;
                // start the frame deadline only once the client sends a byte.
                if (messages > 0)
                    await _pipe.ReadExactlyAsync(header.AsMemory(0, 1), lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                await _pipe.ReadExactlyAsync(header.AsMemory(1, 3), deadline.Token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 1 || length > 4 * 1024 * 1024) throw new InvalidDataException("Broker message exceeds size limit.");
                byte[] body = new byte[length];
                await _pipe.ReadExactlyAsync(body, deadline.Token);
                // Impersonation is synchronous and ends BEFORE handler execution.
                // A client must send data first for named-pipe impersonation.
                ValidateClient();
                string response;
                // The frame timeout protects partial messages, not human approval
                // or model generation. Those operations have their own deadlines.
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                try { response = await handler(encoding.GetString(body), lifetime.Token); }
                catch (SandboxBrokerException error) { response = System.Text.Json.JsonSerializer.Serialize(new { error = error.Message }); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                { response = "{\"error\":\"Provider request timed out or was canceled. The sandbox connection remains open.\"}"; }
                catch (OperationCanceledException) { throw; }
                catch { response = "{\"error\":\"Broker request rejected.\"}"; }
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                byte[] output = encoding.GetBytes(response);
                if (output.Length > 1024 * 1024) throw new InvalidDataException("Broker response exceeds size limit.");
                BinaryPrimitives.WriteInt32LittleEndian(header, output.Length);
                await _pipe.WriteAsync(header, deadline.Token);
                await _pipe.WriteAsync(output, deadline.Token);
                await _pipe.FlushAsync(deadline.Token);
            }
        }
        finally { _pipe.Dispose(); }
    }

    private void ValidateClient()
    {
        bool allowed = false;
        _pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            GetTokenInformation(identity.AccessToken, 31, IntPtr.Zero, 0, out int size); // TokenAppContainerSid
            if (size < IntPtr.Size || size > 4096) throw new UnauthorizedAccessException("Client AppContainer identity is unavailable.");
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(identity.AccessToken, 31, memory, size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr sid = Marshal.ReadIntPtr(memory);
                allowed = sid != IntPtr.Zero && new SecurityIdentifier(sid).Value == _expectedSid;
            }
            finally { Marshal.FreeHGlobal(memory); }
        });
        if (!allowed) throw new UnauthorizedAccessException("Client does not belong to the expected AppContainer.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _pipe.Dispose();
        // Leave CTS undisposed until any outstanding ServeAsync has unwound.
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr information, int length, out int required);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr LocalFree(IntPtr memory);
}
