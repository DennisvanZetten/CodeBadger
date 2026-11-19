using System.Text.Json;

namespace CodeBadger.Services;

/// <summary>
/// Host session binding between authenticated AppContainer IPC and the provider
/// broker. The sandbox can submit only completion JSON; it cannot choose the
/// repository, provider URL, authentication headers, key or approval outcome.
/// Create before starting the sandbox and dispose when its session stops.
/// </summary>
public sealed class SandboxProviderBridge : IDisposable
{
    private readonly SandboxBrokerPipe _pipe;
    private readonly SandboxNetworkBroker _broker;
    private readonly SandboxApprovalService _approvals;
    private readonly Guid _repositoryId;
    private readonly string _repositoryPath;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private int _requestCount;
    // Pass the host's explicit namespace path, not a LOCAL alias whose
    // resolution can differ between packaged hosts and sandbox subprocesses.
    public string PipeName => _pipe.HostName;
    public Task Completion { get; }
    public string? Failure { get; private set; }

    public SandboxProviderBridge(string appContainerSid, Guid repositoryId, string repositoryPath,
        Uri endpoint, string model, string hostApiKey, SandboxNetworkBroker broker, SandboxApprovalService approvals)
    {
        if (repositoryId == Guid.Empty || !Path.IsPathFullyQualified(repositoryPath))
            throw new ArgumentException("A repository ID and absolute path are required.");
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl))
            throw new ArgumentException("A host-configured model is required.");
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 ||
            !endpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("A host-configured HTTPS chat-completions endpoint is required.");
        if (string.IsNullOrWhiteSpace(hostApiKey) || hostApiKey.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A host-managed API key is required.");
        _repositoryId = repositoryId;
        _repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        _endpoint = endpoint;
        _model = model;
        _apiKey = hostApiKey;
        _broker = broker;
        _approvals = approvals;
        _pipe = new SandboxBrokerPipe(appContainerSid);
        Completion = _pipe.ServeAsync(HandleAsync, _lifetime.Token);
        // Observe transport failures without writing untrusted content or keys
        // into logs. The session owner can inspect Completion for failure status.
        _ = Completion.ContinueWith(task =>
        {
            var error = task.Exception?.GetBaseException();
            Failure = error is null ? null : "Sandbox provider pipe failed (" + error.GetType().Name +
                ", HRESULT 0x" + error.HResult.ToString("X8") + "). Restart the sandbox and report this code.";
        }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<string> HandleAsync(string message, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _requestCount) > 100)
            throw new UnauthorizedAccessException("Session provider request quota exceeded.");
        using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Provider request must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Name is not ("operation" or "body"))
                throw new InvalidDataException("Unexpected or duplicate provider bridge field.");
        }
        if (!root.TryGetProperty("operation", out var operation) || operation.GetString() != "complete" ||
            !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Only completion requests are supported.");
        var bodyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (!bodyNames.Add(property.Name)) throw new InvalidDataException("Duplicate completion fields are not allowed.");
        if (!body.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != _model)
            throw new UnauthorizedAccessException("The sandbox cannot select another provider model.");
        string response = await _broker.CompleteWithApprovalAsync(_repositoryId, _repositoryPath, _endpoint,
            _apiKey, body.GetRawText(), _approvals, cancellationToken);
        using var parsed = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 64 });
        return JsonSerializer.Serialize(new { response = parsed.RootElement });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _pipe.Dispose();
    }
}
