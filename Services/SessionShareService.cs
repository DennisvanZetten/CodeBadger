using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

/// <summary>Hosts one revocable, read-only session page and exposes it through a temporary Cloudflare tunnel.</summary>
public sealed class SessionShareService(ILogger<SessionShareService> logger) : IAsyncDisposable
{
    private const string CloudflaredDownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ShareHost? _host;
    private Process? _tunnel;
    private string? _link;
    private bool _disposed;

    public sealed record ShareLink(string Url, Guid RepositoryId, string SessionId);
    public ShareLink? CurrentShare => _link is { } url && _host is { } host
        ? new ShareLink(url, host.RepositoryId, host.SessionId) : null;
    public int ViewerCount => _host?.ViewerCount ?? 0;
    public event Action? ShareChanged;

    public int GetViewerCountForSession(string sessionId) => _host?.SessionId == sessionId ? _host.ViewerCount : 0;

    public async Task<ShareLink> StartAsync(Guid repositoryId, string sessionId, string directory, string title,
        Func<CancellationToken, Task<IReadOnlyList<OpenCodeActivityService.SessionTextMessage>>> loadMessages,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Session sharing is currently supported on Windows only.");
        if (!IsValidSessionId(sessionId) || !Directory.Exists(directory))
            throw new ArgumentException("The selected OpenCode session or repository is not available.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tunnel is { HasExited: false } && _link is not null)
            {
                throw new InvalidOperationException("A session share is already active. Stop it before creating another link.");
            }

            var executable = FindCloudflared() ?? throw new FileNotFoundException("cloudflared is not installed.");
            await VerifyCloudflaredAsync(executable, cancellationToken);
            _host = new ShareHost(loadMessages, repositoryId, sessionId, title, () => ShareChanged?.Invoke());
            var port = _host.Start();
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("tunnel");
            start.ArgumentList.Add("--url");
            start.ArgumentList.Add($"http://127.0.0.1:{port}");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += HandleTunnelExited;
            if (!process.Start())
            {
                _host.Dispose();
                _host = null;
                process.Dispose();
                throw new InvalidOperationException("Could not start cloudflared.");
            }

            _tunnel = process;
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(50));
            var tunnelUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, eventArgs) => SetTunnelUrl(eventArgs.Data, tunnelUrl);
            process.ErrorDataReceived += (_, eventArgs) => SetTunnelUrl(eventArgs.Data, tunnelUrl);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.Exited += (_, _) => tunnelUrl.TrySetException(
                new InvalidOperationException("cloudflared closed before it provided a share URL."));
            try
            {
                var url = await tunnelUrl.Task.WaitAsync(startupTimeout.Token);
                _link = url.TrimEnd('/') + "/s/" + _host.Token;
                ShareChanged?.Invoke();
                logger.LogInformation("Started a read-only share for OpenCode session {SessionId}.", sessionId);
                return new ShareLink(_link, repositoryId, sessionId);
            }
            catch
            {
                await StopCoreAsync();
                throw;
            }
            finally { startupTimeout.Cancel(); }
        }
        finally { _gate.Release(); }
    }

    public Task<bool> IsInstalledAndRunnableAsync(CancellationToken cancellationToken)
    {
        var executable = FindCloudflared();
        return executable is null ? Task.FromResult(false) : IsRunnableAsync(executable, cancellationToken);
    }

    public static string? FindCloudflared()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var candidates = new List<string>();
        var appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeBadger", "tools", "cloudflared.exe");
        candidates.Add(appPath);
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(path.Trim('"'), "cloudflared.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Cloudflare", "Cloudflare Tunnel", "cloudflared.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string> InstallCloudflaredAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cloudflare Quick Tunnel sharing is currently supported on Windows only.");

        var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeBadger", "tools", "cloudflared.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await client.GetAsync(CloudflaredDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 100_000_000)
                throw new InvalidOperationException("The cloudflared download is unexpectedly large.");

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 100_000_000)
                        throw new InvalidOperationException("The cloudflared download is unexpectedly large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            await using (var file = File.OpenRead(temporary))
            {
                var header = new byte[2];
                if (await file.ReadAsync(header, cancellationToken) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidOperationException("The downloaded cloudflared file is not a Windows executable.");
            }

            await VerifyAuthenticodeSignatureAsync(temporary, cancellationToken);

            File.Move(temporary, destination, overwrite: true);
            await VerifyCloudflaredAsync(destination, cancellationToken);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var process = _tunnel;
        _tunnel = null;
        _link = null;
        _host?.Dispose();
        _host = null;
        ShareChanged?.Invoke();
        if (process is null)
            return;
        process.Exited -= HandleTunnelExited;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
        finally { process.Dispose(); }
    }

    private void HandleTunnelExited(object? sender, EventArgs args)
    {
        logger.LogWarning("The cloudflared tunnel process exited; the session share is no longer available.");
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_tunnel is { HasExited: true } process)
                {
                    process.Exited -= HandleTunnelExited;
                    process.Dispose();
                    _tunnel = null;
                    _host?.Dispose();
                    _host = null;
                    _link = null;
                    ShareChanged?.Invoke();
                }
            }
            finally { _gate.Release(); }
        });
    }

    private static void SetTunnelUrl(string? line, TaskCompletionSource<string> result)
    {
        if (line is null) return;
        var match = System.Text.RegularExpressions.Regex.Match(line,
            @"https://[a-z0-9-]+\.trycloudflare\.com", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            result.TrySetResult(match.Value);
    }

    private static async Task VerifyCloudflaredAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            ArgumentList = { "--version" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not run cloudflared.");
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The cloudflared executable could not be verified.");
    }

    private static async Task<bool> IsRunnableAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            await VerifyCloudflaredAsync(executable, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or
                                           TimeoutException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            return false;
        }
    }

    private static async Task VerifyAuthenticodeSignatureAsync(string executable, CancellationToken cancellationToken)
    {
        const string command = "$signature = Get-AuthenticodeSignature -LiteralPath $args[0]; " +
            "if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Cloudflare') { exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command, executable },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not verify the cloudflared download signature.");
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The cloudflared download does not have a valid Cloudflare code signature.");
    }

    private static bool IsValidSessionId(string id) => id.StartsWith("ses_", StringComparison.Ordinal) &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

        private sealed class ShareHost(
        Func<CancellationToken, Task<IReadOnlyList<OpenCodeActivityService.SessionTextMessage>>> loadMessages,
        Guid repositoryId, string sessionId, string title, Action changed) : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, DateTime> _viewers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
        private Task? _acceptLoop;
        public Guid RepositoryId => repositoryId;
        public string SessionId => sessionId;
        public string Title => title;
        public int ViewerCount
        {
            get
            {
                RemoveExpiredViewers();
                return _viewers.Count;
            }
        }
        public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public int Start()
        {
            _listener.Start(64);
            _acceptLoop = AcceptLoopAsync();
            return ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    if (_clients.Count >= 32)
                    {
                        client.Dispose();
                        continue;
                    }
                    _clients.TryAdd(client, 0);
                    _ = HandleClientAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var requestBytes = new List<byte>(1024);
                    var buffer = new byte[1];
                    while (requestBytes.Count < 8192)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(), requestTimeout.Token);
                        if (count == 0)
                            return;
                        requestBytes.Add(buffer[0]);
                        var length = requestBytes.Count;
                        if (length >= 4 && requestBytes[length - 4] == '\r' && requestBytes[length - 3] == '\n' &&
                            requestBytes[length - 2] == '\r' && requestBytes[length - 1] == '\n')
                            break;
                    }
                    if (requestBytes.Count >= 8192)
                    {
                        await RespondAsync(stream, 400, "text/plain; charset=utf-8", "Request too large", requestTimeout.Token);
                        return;
                    }
                    if (requestBytes.Count < 4)
                        return;
                    var requestHeader = Encoding.ASCII.GetString(requestBytes.ToArray());
                    var lines = requestHeader.Split("\r\n", StringSplitOptions.None);
                    if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
                        return;
                    var request = lines[0].Split(' ', 3);
                    if (request.Length != 3)
                    {
                        await RespondAsync(stream, 400, "text/plain; charset=utf-8", "Bad request", requestTimeout.Token);
                        return;
                    }

                    var contentLength = 0;
                    var transferEncodingChunked = false;
                    foreach (var header in lines.Skip(1))
                    {
                        if (string.IsNullOrEmpty(header))
                            break;
                        var separator = header.IndexOf(':');
                        if (separator > 0 && header[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(header[(separator + 1)..].Trim(), out contentLength);
                        if (separator > 0 && header[..separator].Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                            header[(separator + 1)..].Contains("chunked", StringComparison.OrdinalIgnoreCase))
                            transferEncodingChunked = true;
                    }
                    if (request[0] == "POST" && request[1] == $"/api/{Token}/presence" &&
                        !transferEncodingChunked && contentLength is > 0 and <= 256)
                    {
                        var bodyBytes = new byte[contentLength];
                        var read = 0;
                        while (read < bodyBytes.Length)
                        {
                            var count = await stream.ReadAsync(bodyBytes.AsMemory(read), requestTimeout.Token);
                            if (count == 0) break;
                            read += count;
                        }
                        if (read == bodyBytes.Length)
                        {
                            try
                            {
                                using var payload = JsonDocument.Parse(bodyBytes);
                                if (payload.RootElement.TryGetProperty("id", out var viewerId) &&
                                    viewerId.ValueKind == JsonValueKind.String && viewerId.GetString() is { Length: > 0 and <= 64 } id &&
                                    id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                                {
                                    _viewers[id] = DateTime.UtcNow;
                                    changed();
                                }
                            }
                            catch (JsonException) { }
                        }
                        await RespondAsync(stream, 204, "text/plain; charset=utf-8", string.Empty, requestTimeout.Token);
                        return;
                    }
                    if (request[1] == $"/s/{Token}" && request[0] == "GET")
                    {
                        await RespondAsync(stream, 200, "text/html; charset=utf-8", ViewerHtml, requestTimeout.Token);
                        return;
                    }

                    var routePrefix = $"/api/{Token}/";
                    if (!request[1].StartsWith(routePrefix, StringComparison.Ordinal))
                    {
                        await RespondAsync(stream, 404, "text/plain; charset=utf-8", "Not found", requestTimeout.Token);
                        return;
                    }
                    var route = request[1][routePrefix.Length..];
                    if (route == "messages" && request[0] == "GET")
                    {
                        var messages = await loadMessages(requestTimeout.Token);
                        await RespondAsync(stream, 200, "application/json; charset=utf-8",
                            JsonSerializer.Serialize(new { title, messages }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), requestTimeout.Token);
                    }
                    else if (route == "viewers" && request[0] == "GET")
                    {
                        RemoveExpiredViewers();
                        await RespondAsync(stream, 200, "application/json; charset=utf-8",
                            JsonSerializer.Serialize(new { count = _viewers.Count }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), requestTimeout.Token);
                    }
                    else
                    {
                        await RespondAsync(stream, 404, "text/plain; charset=utf-8", "Not found", requestTimeout.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                finally { _clients.TryRemove(client, out _); }
            }
        }

        private void RemoveExpiredViewers()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
            foreach (var viewer in _viewers.Where(pair => pair.Value < cutoff).Select(pair => pair.Key))
                if (_viewers.TryRemove(viewer, out _))
                    changed();
        }

        private static async Task RespondAsync(NetworkStream stream, int status, string contentType, string body,
            CancellationToken cancellationToken)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var description = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", _ => "Not Found" };
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {description}\r\n" +
                $"Content-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n" +
                "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(bodyBytes, cancellationToken);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Keys)
            {
                try { client.Dispose(); }
                catch (SocketException) { }
            }
            try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _viewers.Clear();
            _stop.Dispose();
        }

        private const string ViewerHtml = """
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="referrer" content="no-referrer"><title>CodeBadger session share</title>
            <style>
            :root{color-scheme:dark;font:16px system-ui,sans-serif;background:#10141d;color:#e8edf5}body{max-width:900px;margin:0 auto;padding:24px}
            header{position:sticky;top:0;background:#10141d;padding:14px 0;border-bottom:1px solid #303847;z-index:1}h1{font-size:1.2rem;margin:0 0 6px}
            #status{color:#94a3b8;font-size:.9rem}.message{white-space:pre-wrap;overflow-wrap:anywhere;padding:16px;margin:14px 0;border:1px solid #303847;border-radius:10px;line-height:1.5}
            .user{border-left:3px solid #60a5fa}.assistant{border-left:3px solid #34d399}.role{font-size:.76rem;text-transform:uppercase;letter-spacing:.08em;color:#94a3b8;margin-bottom:8px}
            #empty{color:#94a3b8;text-align:center;padding:40px}.warning{color:#fbbf24;font-size:.82rem;margin-top:8px}
            </style><body><header><h1 id="title">Shared session</h1><div id="status">Connecting…</div><div class="warning">Read-only session transcript. Anyone with this link can view it.</div></header>
            <main id="messages"><div id="empty">Loading session…</div></main>
            <script>
            const base='/api/'+location.pathname.split('/').filter(Boolean).at(-1);
            const viewer=crypto.randomUUID();let last='';
            async function refresh(){try{const r=await fetch(base+'/messages',{cache:'no-store'});if(!r.ok)throw Error();const data=await r.json();document.title=data.title+' — CodeBadger';document.getElementById('title').textContent=data.title;
            const key=JSON.stringify(data.messages);if(key!==last){last=key;const main=document.getElementById('messages');main.replaceChildren();if(!data.messages.length){const empty=document.createElement('div');empty.id='empty';empty.textContent='No text messages in this session yet.';main.append(empty)}for(const message of data.messages){const article=document.createElement('article');article.className='message '+(message.role==='user'?'user':'assistant');const role=document.createElement('div');role.className='role';role.textContent=message.role==='user'?'User':'Assistant';const text=document.createElement('div');text.textContent=message.text;article.append(role,text);main.append(article)}}
            const viewers=await fetch(base+'/viewers',{cache:'no-store'}).then(x=>x.json());document.getElementById('status').textContent=viewers.count+' viewer'+(viewers.count===1?'':'s')+' connected · updated '+new Date().toLocaleTimeString();}
            catch{document.getElementById('status').textContent='Share is unavailable or has been revoked.'}}
            async function heartbeat(){try{await fetch(base+'/presence',{method:'POST',headers:{'Content-Type':'text/plain'},body:JSON.stringify({id:viewer}),keepalive:true})}catch{}}
            heartbeat();setInterval(heartbeat,5000);refresh();setInterval(refresh,5000);addEventListener('pagehide',()=>navigator.sendBeacon(base+'/presence',JSON.stringify({id:viewer})));</script></body></html>
            """;
    }
}
