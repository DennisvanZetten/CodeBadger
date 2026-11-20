using System.Collections.Concurrent;
using System.Text;

namespace CodeBadger.Services;

/// <summary>
/// Manages isolated, no-network terminals and host-owned review snapshots.
/// No automatic OpenCode startup: provider transport must be prepared first.
/// </summary>
public sealed partial class WindowsSandboxSessionService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Review> _reviews = new();
    private readonly ConcurrentDictionary<Guid, List<string>> _recentOutput = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _startGates = new();
    private bool _disposed;
    public event Action<Guid, string>? OutputReceived;
    public event Action<Guid>? StateChanged;
    public event Action<Guid, string>? ProviderFailed;
    public event Action<Guid, string, string>? ChatUpdated;

    public IReadOnlyList<string> GetRecentOutput(Guid repositoryId) =>
        _recentOutput.TryGetValue(repositoryId, out var output)
            ? CopyRecentOutput(output)
            : [];

    public void ClearRecentOutput(Guid repositoryId) => _recentOutput.TryRemove(repositoryId, out _);

    private static string[] CopyRecentOutput(List<string> output)
    {
        lock (output)
            return output.ToArray();
    }

    private sealed class Session
    {
        public required string RepositoryPath { get; init; }
        public required WindowsSandboxTerminal Terminal { get; init; }
        public Task Reader { get; set; } = Task.CompletedTask;
        public SandboxProviderBridge? Bridge { get; init; }
        public SandboxSdkChatBridge? Chat { get; init; }
        public volatile bool Stopping;
    }
    private sealed record Review(string RepositoryPath, IReadOnlyList<SandboxWorkspace.FileChange> Changes);

    public bool IsRunning(Guid repositoryId) => _sessions.ContainsKey(repositoryId);
    public SandboxWorkspace.AccessMode? GetMode(Guid repositoryId) => _sessions.TryGetValue(repositoryId, out var session)
        ? session.Terminal.Process.Mode : null;

    public IReadOnlyList<SandboxWorkspace.FileChange> GetChanges(Guid repositoryId, string repositoryPath)
        => _reviews.TryGetValue(repositoryId, out var review) &&
            string.Equals(review.RepositoryPath, CanonicalPath(repositoryPath), StringComparison.OrdinalIgnoreCase)
            ? review.Changes : [];

    /// <summary>Host-only entrypoint; executable and selections must be reviewed by the user.</summary>
    public async Task StartAsync(Guid repositoryId, string repositoryPath, string executable, string arguments,
        IReadOnlyList<string> selectedFiles)
        => await StartCoreAsync(repositoryId, repositoryPath, executable, arguments, selectedFiles, null);

    public async Task StartOpenCodeAsync(Guid repositoryId, string repositoryPath, string executable,
        IReadOnlyList<string> selectedFiles, Uri endpoint, string model, string hostApiKey,
        SandboxNetworkBroker broker, SandboxApprovalService approvals, SandboxWorkspace.Limits? limits = null)
    {
        var assessment = await Task.Run(() => SandboxWorkspace.Assess(repositoryPath, selectedFiles, limits));
        if (assessment.Mode == SandboxWorkspace.AccessMode.Direct)
        {
            await StartSdkOpenCodeAsync(repositoryId, repositoryPath, assessment.Files, endpoint, model, hostApiKey, broker, approvals);
            return;
        }
        string pluginSource;
        string transportSource;
        using (var reader = new StreamReader(await FileSystem.OpenAppPackageFileAsync("sandbox/server.mjs")))
            pluginSource = await reader.ReadToEndAsync();
        using (var reader = new StreamReader(await FileSystem.OpenAppPackageFileAsync("sandbox/provider-transport.mjs")))
            transportSource = await reader.ReadToEndAsync();
        await StartCoreAsync(repositoryId, repositoryPath, executable, "--standalone", selectedFiles, (sid, folder) =>
        {
            var bridge = new SandboxProviderBridge(sid, repositoryId, repositoryPath, endpoint, model, hostApiKey, broker, approvals);
            try
            {
                SandboxOpenCodeConfiguration.Stage(folder, bridge.PipeName, model, pluginSource, transportSource);
                return bridge;
            }
            catch { bridge.Dispose(); throw; }
        }, limits);
    }

    private async Task StartCoreAsync(Guid repositoryId, string repositoryPath, string executable, string arguments,
        IReadOnlyList<string> selectedFiles, Func<string, string, SandboxProviderBridge>? prepare, SandboxWorkspace.Limits? limits = null)
    {
        var startGate = _startGates.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        await startGate.WaitAsync();
        try
        {
            if (IsRunning(repositoryId))
                return;
            await StartCoreLockedAsync(repositoryId, repositoryPath, executable, arguments, selectedFiles, prepare, limits);
        }
        finally { startGate.Release(); }
    }

    private async Task StartCoreLockedAsync(Guid repositoryId, string repositoryPath, string executable, string arguments,
        IReadOnlyList<string> selectedFiles, Func<string, string, SandboxProviderBridge>? prepare, SandboxWorkspace.Limits? limits)
    {
        if (repositoryId == Guid.Empty) throw new ArgumentException("A repository ID is required.");
        string path = CanonicalPath(repositoryPath);
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(repositoryId)) throw new InvalidOperationException("Stop the existing sandbox session first.");
            if (_reviews.TryGetValue(repositoryId, out var review) && review.Changes.Count > 0)
                throw new InvalidOperationException("Review or explicitly discard captured changes before starting another session.");
            SandboxProviderBridge? bridge = null;
            WindowsSandboxTerminal terminal;
            try
            {
                var assessment = await Task.Run(() => SandboxWorkspace.Assess(path, selectedFiles, limits));
                if (prepare is not null && assessment.Mode == SandboxWorkspace.AccessMode.Direct)
                    throw new IOException("The selection grew past its copy limits during startup. Restart to use direct SDK chat; no repository access was granted.");
                await WindowsRepositoryAccessGuardian.RecoverPendingAsync(FileSystem.AppDataDirectory, FileSystem.OpenAppPackageFileAsync);
                string? recovery = assessment.Mode == SandboxWorkspace.AccessMode.Direct
                    ? await WindowsRepositoryAccessGuardian.PrepareAsync(FileSystem.AppDataDirectory, FileSystem.OpenAppPackageFileAsync) : null;
                Func<string, string, string>? callback = prepare is null ? null : (sid, folder) =>
                {
                    bridge = prepare(sid, folder);
                    return arguments;
                };
                terminal = await Task.Run(() => recovery != null
                    ? WindowsSandboxTerminal.StartDirectPrepared(executable, arguments, path, assessment.Files, callback, recovery)
                    : prepare is null
                    ? WindowsSandboxTerminal.Start(executable, arguments, path, selectedFiles)
                    : WindowsSandboxTerminal.StartPrepared(executable, path, selectedFiles, (sid, folder) =>
                    {
                        bridge = prepare(sid, folder);
                        return arguments;
                    }, limits: limits));
            }
            catch { bridge?.Dispose(); throw; }
            var session = new Session { RepositoryPath = path, Terminal = terminal, Bridge = bridge };
            _sessions[repositoryId] = session;
            session.Reader = Task.Run(() => ReadOutput(repositoryId, session));
            if (bridge is not null) _ = MonitorBridgeAsync(repositoryId, session);
            if (terminal.Process.RepositoryGuardianCompletion is not null) _ = MonitorGuardianAsync(repositoryId, session);
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke(repositoryId);
    }

    public async Task WriteInputAsync(Guid repositoryId, string text)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(repositoryId, out var session)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await session.Terminal.Input.WriteAsync(bytes);
            await session.Terminal.Input.FlushAsync();
        }
        finally { _gate.Release(); }
    }

    public void Resize(Guid repositoryId, int columns, int rows)
    {
        _gate.Wait();
        try { if (_sessions.TryGetValue(repositoryId, out var session)) session.Terminal.Resize(columns, rows); }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(Guid repositoryId)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(repositoryId, out var session)) return;
            _recentOutput.TryRemove(repositoryId, out _);
            // Never report successful revocation while descendants are running.
            session.Stopping = true;
            await Task.Run(session.Terminal.Process.Stop);
            session.Bridge?.Dispose();
            session.Chat?.Dispose();
            if (session.Terminal.Process.Mode == SandboxWorkspace.AccessMode.Copy)
            {
                var changes = await Task.Run(() => session.Terminal.Process.StopAndCaptureChanges());
                _reviews[repositoryId] = new Review(session.RepositoryPath, changes);
            }
            // Reader remains active while ConPTY emits final output on close.
            await Task.Run(session.Terminal.CloseConsole);
            await session.Reader.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(session.Terminal.Dispose);
            _sessions.TryRemove(repositoryId, out _);
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke(repositoryId);
    }

    public async Task DiscardReviewAsync(Guid repositoryId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_sessions.ContainsKey(repositoryId)) throw new InvalidOperationException("Stop the sandbox before discarding its review.");
            _reviews.TryRemove(repositoryId, out _);
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke(repositoryId);
    }

    private void ReadOutput(Guid repositoryId, Session session)
    {
        byte[] buffer = new byte[8192];
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            int count;
            while ((count = session.Terminal.Output.Read(buffer, 0, buffer.Length)) != 0)
            {
                int decoded = decoder.GetChars(buffer, 0, count, characters, 0, flush: false);
                if (decoded > 0) PublishOutput(repositoryId, new string(characters, 0, decoded));
            }
            int remaining = decoder.GetChars([], 0, 0, characters, 0, flush: true);
            if (remaining > 0) PublishOutput(repositoryId, new string(characters, 0, remaining));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task MonitorBridgeAsync(Guid repositoryId, Session session)
    {
        try { await session.Bridge!.Completion; }
        catch (Exception error)
        {
            if (!_sessions.TryGetValue(repositoryId, out var active) || !ReferenceEquals(active, session)) return;
            if (error is OperationCanceledException or ObjectDisposedException) return;
            string message = "Sandbox provider pipe failed (" + error.GetType().Name + ", HRESULT 0x" +
                error.HResult.ToString("X8") + "). Restart the sandbox and report this code.";
            foreach (Action<Guid, string> listener in ProviderFailed?.GetInvocationList() ?? [])
            {
                try { listener(repositoryId, message); } catch { }
            }
        }
    }

    private async Task MonitorGuardianAsync(Guid repositoryId, Session session)
    {
        await session.Terminal.Process.RepositoryGuardianCompletion!;
        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(repositoryId, out var active) || !ReferenceEquals(active, session)) return;
            await Task.Run(session.Terminal.Process.Stop);
            session.Bridge?.Dispose();
            session.Chat?.Dispose();
            foreach (Action<Guid, string> listener in ProviderFailed?.GetInvocationList() ?? [])
            {
                try { listener(repositoryId, "Repository guardian stopped unexpectedly. Sandbox processes were stopped; ACL recovery must be verified before another direct session."); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    private void PublishOutput(Guid repositoryId, string text)
    {
        var output = _recentOutput.GetOrAdd(repositoryId, _ => []);
        lock (output)
        {
            output.Add(text);
            while (output.Count > 2000)
                output.RemoveAt(0);
        }
        // A broken UI subscriber must not terminate the output drain and
        // deadlock ConPTY shutdown. Output is not logged or persisted here.
        foreach (Action<Guid, string> listener in OutputReceived?.GetInvocationList() ?? [])
        {
            try { listener(repositoryId, text); } catch { }
        }
    }

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute repository path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; }
        finally { _gate.Release(); }
        foreach (Guid id in _sessions.Keys) await StopAsync(id);
    }
}
