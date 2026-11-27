using CodeBadger.Models;
using System.Diagnostics;

namespace CodeBadger.Services;

public sealed class CodexAgentConnector(CodeBadgerCliAgentRunner runner) : ICodeBadgerAgentConnector
{
    public bool Supports(string agentTool) => AgentTools.Normalize(agentTool) == AgentTools.Codex;

    public Task<CodeBadgerCliAgentRunner.AgentReply> SendPromptAsync(string repositoryPath, string? sessionId,
        string role, string prompt, IReadOnlyList<string> images, string? model,
        Func<string, Task>? onText, CancellationToken cancellationToken, Action<Process>? onProcessStarted) =>
        runner.SendPromptAsync(AgentTools.Codex, repositoryPath, sessionId, role, prompt, images,
            cancellationToken, onText, model, onProcessStarted);
}
