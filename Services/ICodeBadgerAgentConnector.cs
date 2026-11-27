using System.Diagnostics;

namespace CodeBadger.Services;

/// <summary>Agent-specific adapter for sending one CodeBadger composer turn.</summary>
public interface ICodeBadgerAgentConnector
{
    bool Supports(string agentTool);

    Task<CodeBadgerCliAgentRunner.AgentReply> SendPromptAsync(string repositoryPath, string? sessionId,
        string role, string prompt, IReadOnlyList<string> images, string? model,
        Func<string, Task>? onText, CancellationToken cancellationToken, Action<Process>? onProcessStarted);
}
