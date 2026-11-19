#nullable enable annotations
namespace CodeBadger.Services
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.IO.Pipes;
    using System.Security.Principal;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Client-side IPC transport intended to run inside AppContainer. It cannot
    /// grant permissions or provide host credentials. The host binds each pipe
    /// to its repository and provider settings and validates the client token.
    /// </summary>
    public sealed class SandboxBrokerClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public SandboxBrokerClient(string pipeName)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (!pipeName.StartsWith("LOCAL\\CodeBadger.Broker.", StringComparison.Ordinal) ||
                pipeName.Length != "LOCAL\\CodeBadger.Broker.".Length + 32 ||
                !Guid.TryParseExact(pipeName.Substring("LOCAL\\CodeBadger.Broker.".Length), "N", out _))
                throw new ArgumentException("A CodeBadger broker pipe name is required.");
            // Identification allows token validation but forbids host operations
            // under an impersonation-capable client identity.
            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _pipe.ConnectAsync(10000, cancellationToken);
        }

        public async Task<string> RequestAsync(string message, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var encoding = new UTF8Encoding(false, true);
            byte[] body = encoding.GetBytes(message);
            if (body.Length < 1 || body.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Broker request exceeds size limit.");
            await _gate.WaitAsync(cancellationToken);
            try
            {
                byte[] header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
                await _pipe.WriteAsync(header, cancellationToken);
                await _pipe.WriteAsync(body, cancellationToken);
                await _pipe.FlushAsync(cancellationToken);
                await _pipe.ReadExactlyAsync(header, cancellationToken);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 1 || length > 1024 * 1024) throw new InvalidDataException("Broker response exceeds size limit.");
                byte[] response = new byte[length];
                await _pipe.ReadExactlyAsync(response, cancellationToken);
                return encoding.GetString(response);
            }
            catch
            {
                // Cancellation midway through a frame makes reuse unsafe.
                _pipe.Dispose();
                _disposed = true;
                throw;
            }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pipe.Dispose();
        }
    }
}
