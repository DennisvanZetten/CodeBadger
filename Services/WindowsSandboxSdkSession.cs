using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

public sealed partial class WindowsSandboxSessionService
{
    private async Task StartSdkOpenCodeAsync(Guid repositoryId, string repositoryPath, IReadOnlyList<string> files,
        Uri endpoint, string model, string key, SandboxNetworkBroker broker, SandboxApprovalService approvals)
    {
        _logger.LogInformation("Preparing direct OpenCode SDK session for repository {RepositoryId} with model {Model} and {FileCount} selected files.",
            repositoryId, model, files.Count);
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
                    chat = new SandboxSdkChatBridge(sid, _logger);
                    chat.Updated += (type, text) =>
                    {
                        if (type == "tool")
                            _logger.LogInformation("OpenCode SDK invoked a tool for repository {RepositoryId}.", repositoryId);
                        else if (type == "busy")
                            _logger.LogInformation("OpenCode SDK began processing a request for repository {RepositoryId}.", repositoryId);
                        else if (type == "idle")
                            _logger.LogInformation("OpenCode SDK finished processing a request for repository {RepositoryId}.", repositoryId);
                        else if (type == "text")
                            _logger.LogInformation("OpenCode SDK response excerpt for repository {RepositoryId}: {ResponseExcerpt}",
                                repositoryId, CreateLogExcerpt(text));
                        else if (type == "tool")
                        {
                            var separator = text.IndexOf(':');
                            var toolName = separator > 0 ? text[..separator].Trim() : "unknown";
                            _logger.LogInformation("OpenCode SDK tool activity for repository {RepositoryId}: {ToolName}.",
                                repositoryId, toolName);
                        }
                        else if (type == "error")
                            _logger.LogWarning("OpenCode SDK reported an error for repository {RepositoryId}.", repositoryId);
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
            _logger.LogInformation("Submitting prompt to direct OpenCode SDK session for repository {RepositoryId}. Prompt excerpt: {PromptExcerpt}",
                repositoryId, CreateLogExcerpt(prompt));
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

    private static string CreateLogExcerpt(string text)
    {
        const int maximumLength = 4000;
        var excerpt = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(https?://)[^/@\s]+:[^/@\s]+@", "$1[redacted]@");
        excerpt = System.Text.RegularExpressions.Regex.Replace(excerpt,
            @"(?i)(token|authorization|password|api[_-]?key)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        return excerpt.Length <= maximumLength ? excerpt : excerpt[..maximumLength] + "… [truncated]";
    }
}
