using System.Text.Json;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed class OpenCodeAgentInteractionProvider(OpenCodeActivityService openCodeActivity)
    : ICodeBadgerAgentInteractionProvider
{
    public bool Supports(string agentTool) => AgentTools.Normalize(agentTool) == AgentTools.OpenCode;

    public async Task<IReadOnlyList<CodeBadgerAgentInteraction>> GetPendingInteractionsAsync(
        string agentSessionId, string repositoryPath, CancellationToken cancellationToken) =>
        (await openCodeActivity.GetPendingInteractionsAsync(agentSessionId, repositoryPath, cancellationToken))
        .Select(interaction => new CodeBadgerAgentInteraction(
            interaction.Id,
            interaction.Kind == "permission" ? CodeBadgerAgentInteractionKind.Permission : CodeBadgerAgentInteractionKind.Question,
            interaction.Title,
            interaction.Details,
            interaction.Fields.Select(field => new CodeBadgerAgentInteractionField(
                field.Key,
                field.Title,
                field.Description,
                field.Type,
                field.Required,
                field.Options.Select(option => new CodeBadgerAgentInteractionOption(option.Value, option.Label)).ToArray(),
                field.Custom)).ToArray(),
            interaction.Resources))
        .ToArray();

    public async Task WaitForCompletionAsync(string agentSessionId, string repositoryPath,
        Func<IReadOnlyList<CodeBadgerAgentInteraction>, Task> onPendingInteractions,
        CancellationToken cancellationToken)
    {
        var completion = openCodeActivity.WaitForSessionAsync(agentSessionId, repositoryPath, cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interactions = await GetPendingInteractionsAsync(agentSessionId, repositoryPath, cancellationToken);
            await onPendingInteractions(interactions);
            if (completion.IsCompleted && interactions.Count == 0)
                break;
            await Task.Delay(interactions.Count > 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1), cancellationToken);
        }
        await completion;
        await onPendingInteractions([]);
    }

    public Task InterruptAsync(string agentSessionId, string repositoryPath, CancellationToken cancellationToken) =>
        openCodeActivity.InterruptSessionAsync(agentSessionId, repositoryPath, cancellationToken);

    public Task ReplyAsync(string agentSessionId, string repositoryPath, CodeBadgerAgentInteraction interaction,
        CodeBadgerAgentInteractionAnswer answer, CancellationToken cancellationToken) =>
        interaction.Kind switch
        {
            CodeBadgerAgentInteractionKind.Permission => openCodeActivity.ReplyToPermissionAsync(
                agentSessionId, repositoryPath, interaction.Id, answer.Decision switch
                {
                    "approve-once" => "once",
                    "approve-always" => "always",
                    "reject" => "reject",
                    _ => throw new ArgumentException("An explicit permission decision is required.", nameof(answer))
                }, answer.Message, cancellationToken),
            CodeBadgerAgentInteractionKind.Question => openCodeActivity.ReplyToFormAsync(
                agentSessionId, repositoryPath, interaction.Id,
                JsonSerializer.SerializeToElement(answer.Values ?? new Dictionary<string, object?>()), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(interaction))
        };
}
