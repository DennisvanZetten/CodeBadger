using CodeBadger.Models;
using System.Diagnostics;

namespace CodeBadger.Services;

public sealed class CodexAgentConnector(CodeBadgerCliAgentRunner runner) : ICodeBadgerAgentConnector
{
    public bool Supports(string agentTool) => AgentTools.Normalize(agentTool) == AgentTools.Codex;

    public Task<IReadOnlyList<CodeBadgerAgentModel>> GetAvailableModelsAsync(string repositoryPath,
        CancellationToken cancellationToken) => runner.GetAvailableModelsAsync(AgentTools.Codex,
        repositoryPath, cancellationToken);

    public Task<CodeBadgerAgentModel?> GetDefaultModelAsync(string repositoryPath, CancellationToken cancellationToken) =>
        runner.GetDefaultModelAsync(AgentTools.Codex, repositoryPath, cancellationToken);

    public Task<CodeBadgerCliAgentRunner.AgentReply> SendPromptAsync(string repositoryPath, string? sessionId,
        string role, string prompt, IReadOnlyList<string> images, string? model,
        Func<string, Task>? onText, CancellationToken cancellationToken, Action<Process>? onProcessStarted) =>
        runner.SendPromptAsync(AgentTools.Codex, repositoryPath, sessionId, role, prompt, images,
            cancellationToken, onText, model, onProcessStarted);
}
