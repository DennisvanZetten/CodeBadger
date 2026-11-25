using CodeBadger.Models;

namespace CodeBadger.Services;

/// <summary>
/// Agent-specific transport for human-in-the-loop requests. The terminal only renders
/// the shared interaction contract, so each agent can use its own API, CLI, or hooks.
/// </summary>
public interface ICodeBadgerAgentInteractionProvider
{
    bool Supports(string agentTool);

    Task<IReadOnlyList<CodeBadgerAgentInteraction>> GetPendingInteractionsAsync(
        string agentSessionId, string repositoryPath, CancellationToken cancellationToken);

    Task WaitForCompletionAsync(string agentSessionId, string repositoryPath,
        Func<IReadOnlyList<CodeBadgerAgentInteraction>, Task> onPendingInteractions,
        CancellationToken cancellationToken);

    Task InterruptAsync(string agentSessionId, string repositoryPath, CancellationToken cancellationToken);

    Task ReplyAsync(string agentSessionId, string repositoryPath, CodeBadgerAgentInteraction interaction,
        CodeBadgerAgentInteractionAnswer answer, CancellationToken cancellationToken);
}
