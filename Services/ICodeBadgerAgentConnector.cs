using System.Diagnostics;

namespace CodeBadger.Services;

public sealed record CodeBadgerAgentModel(string Reference, string Name, bool IsDefault = false);

/// <summary>Agent-specific adapter for sending one CodeBadger composer turn.</summary>
public interface ICodeBadgerAgentConnector
{
    bool Supports(string agentTool);

    Task<IReadOnlyList<CodeBadgerAgentModel>> GetAvailableModelsAsync(string repositoryPath,
        CancellationToken cancellationToken);

    Task<CodeBadgerAgentModel?> GetDefaultModelAsync(string repositoryPath, CancellationToken cancellationToken);

    Task<CodeBadgerCliAgentRunner.AgentReply> SendPromptAsync(string repositoryPath, string? sessionId,
        string role, string prompt, IReadOnlyList<string> images, string? model,
        Func<string, Task>? onText, CancellationToken cancellationToken, Action<Process>? onProcessStarted);
}
