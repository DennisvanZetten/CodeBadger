namespace CodeBadger.Services;

/// <summary>Host-side UI approval requests. No listener means rejection.</summary>
public sealed class SandboxApprovalService
{
    public event Action<SandboxApprovalRequest>? Requested;

    public async Task<bool> RequestAsync(Guid repositoryId, Uri origin, CancellationToken cancellationToken)
    {
        if (repositoryId == Guid.Empty || !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo))
            throw new ArgumentException("A repository and HTTPS origin are required.");
        var request = new SandboxApprovalRequest(repositoryId, new Uri(origin.GetLeftPart(UriPartial.Authority)), cancellationToken);
        var listeners = Requested?.GetInvocationList();
        if (listeners is null || listeners.Length != 1) return false;
        using var registration = cancellationToken.Register(() => request.Decide(false));
        cancellationToken.ThrowIfCancellationRequested();
        try { ((Action<SandboxApprovalRequest>)listeners[0])(request); }
        catch { request.Decide(false); }
        bool result = await request.Decision;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

public sealed class SandboxApprovalRequest
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid RepositoryId { get; }
    public Uri Origin { get; }
    public CancellationToken CancellationToken { get; }
    internal Task<bool> Decision => _decision.Task;

    internal SandboxApprovalRequest(Guid repositoryId, Uri origin, CancellationToken cancellationToken)
    {
        RepositoryId = repositoryId;
        Origin = origin;
        CancellationToken = cancellationToken;
    }

    public void Decide(bool allow) => _decision.TrySetResult(allow && !CancellationToken.IsCancellationRequested);
}
