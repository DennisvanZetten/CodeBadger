using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeBadger.Services;

/// <summary>
/// Host-side HTTPS broker. Not an IPC server and not yet wired into OpenCode.
/// No sandbox-supplied credentials or headers are forwarded. Completion
/// authentication is supplied exclusively by host settings.
/// Redirects, private destinations and oversized responses fail closed.
/// </summary>
public sealed class SandboxNetworkBroker : IDisposable
{
    private readonly SandboxPermissionService _permissions;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;

    public SandboxNetworkBroker(SandboxPermissionService permissions)
    {
        _permissions = permissions;
        _permissions.PermissionsRevoking += CancelRepository;
    }

    public async Task<string> GetAsync(Guid repositoryId, string repositoryPath, Uri destination,
        Func<Uri, CancellationToken, Task<bool>> requestApproval, CancellationToken cancellationToken = default)
        => await SendAsync(repositoryId, repositoryPath, destination, HttpMethod.Get, null, null, requestApproval, cancellationToken);

    public Task<string> GetWithApprovalAsync(Guid repositoryId, string repositoryPath, Uri destination,
        SandboxApprovalService approvals, CancellationToken cancellationToken = default)
        => GetAsync(repositoryId, repositoryPath, destination,
            (origin, token) => approvals.RequestAsync(repositoryId, origin, token), cancellationToken);

    public Task<string> CompleteWithApprovalAsync(Guid repositoryId, string repositoryPath, Uri configuredEndpoint,
        string hostApiKey, string requestJson, SandboxApprovalService approvals, CancellationToken cancellationToken = default)
        => CompleteAsync(repositoryId, repositoryPath, configuredEndpoint, hostApiKey, requestJson,
            (origin, token) => approvals.RequestAsync(repositoryId, origin, token), cancellationToken);

    /// <summary>
    /// Host-only OpenAI-compatible completion entrypoint. The endpoint and API
    /// key must come from trusted host settings, never from a sandbox IPC body.
    /// Streaming is disabled until the sandbox bridge supports bounded streams.
    /// </summary>
    public Task<string> CompleteAsync(Guid repositoryId, string repositoryPath, Uri configuredEndpoint,
        string hostApiKey, string requestJson, Func<Uri, CancellationToken, Task<bool>> requestApproval,
        CancellationToken cancellationToken = default)
    {
        ValidateDestination(configuredEndpoint);
        if (!configuredEndpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) || !string.IsNullOrEmpty(configuredEndpoint.Query))
            throw new ArgumentException("Only the host-configured chat-completions endpoint is supported.", nameof(configuredEndpoint));
        if (string.IsNullOrWhiteSpace(hostApiKey) || hostApiKey.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A valid host-managed API key is required.", nameof(hostApiKey));
        if (Encoding.UTF8.GetByteCount(requestJson) > 4 * 1024 * 1024)
            throw new InvalidDataException("Completion request exceeds the sandbox size limit.");
        using var document = JsonDocument.Parse(requestJson, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(model.GetString()) ||
            !document.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
            (document.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind != JsonValueKind.False))
            throw new ArgumentException("A non-streaming chat completion request with model and messages is required.", nameof(requestJson));
        return SendAsync(repositoryId, repositoryPath, configuredEndpoint, HttpMethod.Post, requestJson, hostApiKey, requestApproval, cancellationToken);
    }

    private async Task<string> SendAsync(Guid repositoryId, string repositoryPath, Uri destination,
        HttpMethod method, string? requestJson, string? hostApiKey,
        Func<Uri, CancellationToken, Task<bool>> requestApproval, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDestination(destination);
        await _requests.WaitAsync(cancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Guid requestId = Guid.NewGuid();
        // Register before approval so revocation can also cancel a pending prompt.
        var registration = new ActiveRequest(repositoryId, lifetime);
        _registrations[requestId] = registration;
        try
        {
            Uri origin = new(destination.GetLeftPart(UriPartial.Authority));
            if (!await _permissions.IsAllowedAsync(repositoryId, repositoryPath, origin))
            {
                lifetime.CancelAfter(TimeSpan.FromMinutes(5));
                if (!await requestApproval(origin, lifetime.Token))
                    throw new SandboxBrokerException("Network access was rejected in CodeBadger.");
                lifetime.Token.ThrowIfCancellationRequested();
                await _permissions.GrantAsync(repositoryId, repositoryPath, origin);
                lifetime.Token.ThrowIfCancellationRequested();
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if (!await _permissions.IsAllowedAsync(repositoryId, repositoryPath, origin))
                throw new SandboxBrokerException("Network permission has been revoked in CodeBadger.");

            // Reasoning models can take more than a minute before any response
            // arrives, especially with the bounded non-streaming provider route.
            lifetime.CancelAfter(TimeSpan.FromMinutes(5));

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                Credentials = null,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = ConnectPublicAsync,
                MaxResponseHeadersLength = 32
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(method, destination);
            if (requestJson is not null)
            {
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostApiKey);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                throw new HttpRequestException("Redirects are not allowed by the sandbox broker.");
            // Do not expose provider errors, headers or cookies to the sandbox:
            // they can contain authentication data or reflected request metadata.
            if (!response.IsSuccessStatusCode)
                throw new SandboxBrokerException("Provider returned HTTP " + (int)response.StatusCode + ". Check provider access, API key and selected model in OpenCode settings.");
            const int maximumBytes = 1024 * 1024;
            if (response.Content.Headers.ContentLength is > maximumBytes)
                throw new InvalidDataException("Network response exceeds the sandbox size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, lifetime.Token)) != 0)
            {
                if (body.Length + count > maximumBytes)
                    throw new InvalidDataException("Network response exceeds the sandbox size limit.");
                await body.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            string text = Encoding.UTF8.GetString(body.ToArray());
            if (hostApiKey is not null && (text.Contains(hostApiKey, StringComparison.Ordinal) ||
                text.Contains(JsonSerializer.Serialize(hostApiKey).Trim('"'), StringComparison.Ordinal)))
                throw new InvalidDataException("Provider response contained protected authentication material.");
            return text;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SandboxBrokerException("Provider request timed out or permission was revoked. Try again or check the permission dialog in CodeBadger.", error);
        }
        catch (HttpRequestException error)
        {
            throw new SandboxBrokerException("CodeBadger could not connect to the provider (network or TLS error).", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new SandboxBrokerException("Provider destination resolved to a blocked local or non-public address.", error);
        }
        finally
        {
            _registrations.TryRemove(requestId, out _);
            _requests.Release();
        }
    }

    private readonly ConcurrentDictionary<Guid, ActiveRequest> _registrations = new();
    private sealed record ActiveRequest(Guid RepositoryId, CancellationTokenSource Cancellation);

    private void CancelRepository(Guid repositoryId)
    {
        foreach (var request in _registrations.Values.Where(request => request.RepositoryId == repositoryId))
        {
            try { request.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static void ValidateDestination(Uri destination)
    {
        if (!destination.IsAbsoluteUri || destination.Scheme != Uri.UriSchemeHttps || destination.Port != 443 ||
            !string.IsNullOrEmpty(destination.UserInfo) || !string.IsNullOrEmpty(destination.Fragment) || destination.AbsoluteUri.Length > 8192)
            throw new ArgumentException("Only HTTPS port 443 destinations without credentials or fragments are supported.", nameof(destination));
        if (destination.IsLoopback || destination.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            destination.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(destination.IdnHost.Trim('[', ']'), out var address) && !IsPublicAddress(address)))
            throw new UnauthorizedAccessException("Local network destinations are blocked.");
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new UnauthorizedAccessException("Destination resolved to a non-public address.");
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Connect to the checked IP, not the hostname: a second DNS
                // resolution must not bypass the public-address check.
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                lastError = exception;
            }
        }
        throw new HttpRequestException("Could not connect to the approved destination.", lastError);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) &&
                !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0)) &&
                !(bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19 || (bytes[1] == 51 && bytes[2] == 100))) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        // Allow only global-unicast IPv6; exclude transition mechanisms,
        // documentation and protocol-assignment ranges conservatively.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 &&
            (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && ((bytes[2] == 0 && bytes[3] <= 0x1f) || (bytes[2] == 0x0d && bytes[3] == 0xb8)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _permissions.PermissionsRevoking -= CancelRepository;
        foreach (var request in _registrations.Values)
        {
            try { request.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
