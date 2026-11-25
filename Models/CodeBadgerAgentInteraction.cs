namespace CodeBadger.Models;

public enum CodeBadgerAgentInteractionKind
{
    Permission,
    Question
}

public sealed record CodeBadgerAgentInteractionOption(string Value, string Label);

public sealed record CodeBadgerAgentInteractionField(
    string Key,
    string Title,
    string Description,
    string Type,
    bool Required,
    IReadOnlyList<CodeBadgerAgentInteractionOption> Options,
    bool AllowsCustomValue);

public sealed record CodeBadgerAgentInteraction(
    string Id,
    CodeBadgerAgentInteractionKind Kind,
    string Title,
    string Details,
    IReadOnlyList<CodeBadgerAgentInteractionField> Fields,
    IReadOnlyList<string> Resources);

public sealed record CodeBadgerAgentInteractionAnswer(
    string? Decision = null,
    string? Message = null,
    IReadOnlyDictionary<string, object?>? Values = null);
