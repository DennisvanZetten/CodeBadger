using System.Text.Json;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed class CodeBadgerTerminalStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _statePath = Path.Combine(FileSystem.AppDataDirectory, "codebadger-terminal-sessions.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CodeBadgerTerminalState> LoadAsync(Guid repositoryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_statePath))
                return new CodeBadgerTerminalState { RepositoryId = repositoryId };
            await using var stream = File.OpenRead(_statePath);
            var states = await JsonSerializer.DeserializeAsync<List<CodeBadgerTerminalState>>(
                stream, JsonOptions, cancellationToken) ?? [];
            var state = states.FirstOrDefault(candidate => candidate.RepositoryId == repositoryId)
                ?? new CodeBadgerTerminalState { RepositoryId = repositoryId };
            state.Sessions ??= [];
            state.SelectedOpenCodeSessionId ??= string.Empty;
            foreach (var session in state.Sessions)
            {
                session.AgentSessionIds ??= new Dictionary<string, string>(StringComparer.Ordinal);
                session.AgentSessionGenerations ??= new Dictionary<string, int>(StringComparer.Ordinal);
                session.AgentContextTokens ??= new Dictionary<string, long>(StringComparer.Ordinal);
                session.AgentContextModels ??= new Dictionary<string, string>(StringComparer.Ordinal);
                session.RoleModelOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
                session.RecentModels ??= [];
                session.Messages ??= [];
                foreach (var message in session.Messages)
                    message.Images ??= [];
                session.Mode = session.Mode == "Build" ? "Build" : "Plan";
                session.ActiveAgentRole ??= string.Empty;
            }
            if (state.SelectedSessionId != Guid.Empty && !state.Sessions.Any(session => session.Id == state.SelectedSessionId))
                state.SelectedSessionId = Guid.Empty;
            return state;
        }
        catch (JsonException)
        {
            return new CodeBadgerTerminalState { RepositoryId = repositoryId };
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CodeBadgerTerminalState state, CancellationToken cancellationToken = default)
    {
        if (state.RepositoryId == Guid.Empty)
            throw new ArgumentException("A repository ID is required to save terminal state.", nameof(state));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CodeBadgerTerminalState> states;
            if (File.Exists(_statePath))
            {
                await using var input = File.OpenRead(_statePath);
                states = await JsonSerializer.DeserializeAsync<List<CodeBadgerTerminalState>>(
                    input, JsonOptions, cancellationToken) ?? [];
            }
            else
            {
                states = [];
            }
            states.RemoveAll(candidate => candidate.RepositoryId == state.RepositoryId);
            states.Add(state);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporaryPath = _statePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, states, JsonOptions, cancellationToken);
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}
