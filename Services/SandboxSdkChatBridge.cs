using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeBadger.Services;

/// <summary>Sandbox-initiated authenticated poll transport. Host queues prompts;
/// sandbox reports text/status only and cannot request arbitrary host operations.</summary>
public sealed class SandboxSdkChatBridge : IDisposable
{
    private readonly SandboxBrokerPipe _pipe;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<string> _prompts = new();
    private int _queued;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string PipeName => _pipe.HostName;
    public Task Ready => _ready.Task;
    public Task Completion { get; }
    public event Action<string, string>? Updated;

    public SandboxSdkChatBridge(string sid, ILogger logger)
    {
        _logger = logger;
        _pipe = new SandboxBrokerPipe(sid, maxMessages: 432000);
        Completion = _pipe.ServeAsync(HandleAsync);
    }

    public void Send(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000) throw new ArgumentException("Prompt must contain 1–32,000 characters.");
        if (Interlocked.Increment(ref _queued) > 4) { Interlocked.Decrement(ref _queued); throw new InvalidOperationException("The SDK prompt queue is full."); }
        _prompts.Enqueue(prompt);
    }

    private Task<string> HandleAsync(string message, CancellationToken cancellation)
    {
        if (message.Length > 1024 * 1024) throw new InvalidDataException("SDK poll exceeds its bound.");
        using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.GetProperty("operation").GetString() != "complete") throw new InvalidDataException("Invalid SDK frame.");
        var body = root.GetProperty("body");
        foreach (var field in body.EnumerateObject())
            if (field.Name is not ("events" or "acceptPrompt" or "stream")) throw new InvalidDataException("Unexpected SDK frame field.");
        var events = body.GetProperty("events");
        if (events.ValueKind != JsonValueKind.Array) throw new InvalidDataException("SDK events must be an array.");
        if (events.GetArrayLength() > 32) throw new InvalidDataException("Too many SDK events.");
        var acceptedEvents = 0;
        foreach (var item in events.EnumerateArray())
        {
            string type = item.GetProperty("type").GetString() ?? "";
            string text = item.GetProperty("text").GetString() ?? "";
            if (text.Length > 32000 || type is not ("ready" or "text" or "busy" or "idle" or "error" or "tool"))
                throw new InvalidDataException("Invalid SDK event.");
            if (type == "ready") _ready.TrySetResult();
            if (type is "busy" or "idle" or "tool" or "error")
                acceptedEvents++;
            foreach (Action<string, string> listener in Updated?.GetInvocationList() ?? [])
                try { listener(type, text); } catch { }
        }
        string? prompt = null;
        if (body.GetProperty("acceptPrompt").GetBoolean() && _prompts.TryDequeue(out prompt))
        {
            Interlocked.Decrement(ref _queued);
            _logger.LogInformation("Direct OpenCode SDK accepted a queued prompt; prompt content omitted.");
        }
        if (acceptedEvents > 0)
            _logger.LogInformation("Received {SdkEventCount} OpenCode SDK lifecycle/tool events.", acceptedEvents);
        return Task.FromResult(JsonSerializer.Serialize(new { response = new { prompt } }));
    }

    public void Dispose() => _pipe.Dispose();
}
