using System;

namespace CodeBadger.Services;

public static class PlanBuildHandoff
{
    public static bool IsSameRequest(string? plannedRequest, string? currentRequest) =>
        !string.IsNullOrWhiteSpace(plannedRequest) &&
        !string.IsNullOrWhiteSpace(currentRequest) &&
        string.Equals(NormalizeRequest(plannedRequest), NormalizeRequest(currentRequest), StringComparison.OrdinalIgnoreCase);

    public static string CreateWorkerPrompt(string request, string plan) =>
        $"Implement the following user request according to the plan below. Treat the user request as the source of truth if anything in the plan is incomplete or conflicts with it. Preserve all stated constraints and acceptance criteria. Make the required code changes, run appropriate checks, and report the result.\n\nOriginal user request:\n{request}\n\nPlan:\n{plan}";

    private static string NormalizeRequest(string request) =>
        request.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
