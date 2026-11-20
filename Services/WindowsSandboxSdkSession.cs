namespace CodeBadger.Services;

public sealed partial class WindowsSandboxSessionService
{
    private async Task StartSdkOpenCodeAsync(Guid repositoryId, string repositoryPath, IReadOnlyList<string> files,
        Uri endpoint, string model, string key, SandboxNetworkBroker broker, SandboxApprovalService approvals)
    {
        if (repositoryId == Guid.Empty) throw new ArgumentException("A repository ID is required.");
        string path = CanonicalPath(repositoryPath);
        // Load and verify runtime before granting any repository rights.
        byte[] archive = await SandboxSdkRuntime.LoadArchiveAsync();
        async Task<string> Asset(string name)
        {
            using var reader = new StreamReader(await FileSystem.OpenAppPackageFileAsync("sandbox/" + name));
            return await reader.ReadToEndAsync();
        }
        string runner = await Asset("sdk-runner.mjs"), plugin = await Asset("server.mjs"), transport = await Asset("provider-transport.mjs");
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(repositoryId)) throw new InvalidOperationException("Stop the existing sandbox session first.");
            if (_reviews.TryGetValue(repositoryId, out var review) && review.Changes.Count > 0)
                throw new InvalidOperationException("Review or discard captured changes first.");
            string recovery = await WindowsRepositoryAccessGuardian.PrepareAsync(FileSystem.AppDataDirectory, FileSystem.OpenAppPackageFileAsync);
            SandboxProviderBridge? bridge = null;
            SandboxSdkChatBridge? chat = null;
            WindowsSandboxTerminal? terminal = null;
            Session? session = null;
            try
            {
                terminal = await Task.Run(() => WindowsSandboxTerminal.StartDirectRuntime(path, files, (sid, folder) =>
                {
                    bridge = new SandboxProviderBridge(sid, repositoryId, path, endpoint, model, key, broker, approvals);
                    chat = new SandboxSdkChatBridge(sid);
                    chat.Updated += (type, text) =>
                    {
                        foreach (Action<Guid, string, string> listener in ChatUpdated?.GetInvocationList() ?? [])
                            try { listener(repositoryId, type, text); } catch { }
                    };
                    SandboxOpenCodeConfiguration.Stage(folder, bridge.PipeName, model, plugin, transport);
                    return SandboxSdkRuntime.Stage(folder, archive, runner, transport, chat.PipeName);
                }, (sid, folder) => "--preserve-symlinks --preserve-symlinks-main \"" + Path.Combine(folder, "sdk-runtime", "sdk-runner.mjs") + "\"", recovery));
                session = new Session { RepositoryPath = path, Terminal = terminal, Bridge = bridge, Chat = chat };
                // SDK diagnostics are not terminal protocol; discard them rather than
                // rendering raw diagnostic paths/payloads in the chat UI.
                session.Reader = Task.Run(() =>
                {
                    var buffer = new byte[8192];
                    try { while (terminal.Output.Read(buffer, 0, buffer.Length) > 0) { } }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                await chat!.Ready.WaitAsync(TimeSpan.FromSeconds(60));
                if (chat.Completion.IsCompleted || terminal.Process.RepositoryGuardianCompletion!.IsCompleted)
                    throw new IOException("SDK transport stopped during initialization. Startup refused.");
                _sessions[repositoryId] = session;
                _ = MonitorBridgeAsync(repositoryId, session);
                _ = MonitorGuardianAsync(repositoryId, session);
                _ = MonitorSdkTransportAsync(repositoryId, session);
            }
            catch
            {
                bridge?.Dispose(); chat?.Dispose();
                if (terminal is not null)
                {
                    await Task.Run(terminal.Process.Stop);
                    await Task.Run(terminal.CloseConsole);
                    if (session is not null) await session.Reader.WaitAsync(TimeSpan.FromSeconds(10));
                    await Task.Run(terminal.Dispose);
                }
                throw;
            }
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke(repositoryId);
    }

    public async Task SendChatAsync(Guid repositoryId, string prompt)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(repositoryId, out var session) || session.Chat is null)
                throw new InvalidOperationException("The direct SDK chat is not running.");
            session.Chat.Send(prompt);
        }
        finally { _gate.Release(); }
    }

    private async Task MonitorSdkTransportAsync(Guid repositoryId, Session session)
    {
        try { await session.Chat!.Completion; } catch { }
        if (session.Stopping || !_sessions.TryGetValue(repositoryId, out var active) || !ReferenceEquals(active, session)) return;
        foreach (Action<Guid, string> listener in ProviderFailed?.GetInvocationList() ?? [])
            try { listener(repositoryId, "SDK chat connection closed. The sandbox is being stopped and repository access revoked."); } catch { }
        try { await StopAsync(repositoryId); }
        catch
        {
            foreach (Action<Guid, string> listener in ProviderFailed?.GetInvocationList() ?? [])
                try { listener(repositoryId, "SDK chat stopped; repository access cleanup could not be confirmed. Recovery is required."); } catch { }
        }
    }
}
