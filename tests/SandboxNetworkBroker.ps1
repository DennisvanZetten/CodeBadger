# Tests broker denial, destination checks and revocation cancellation without
# sending requests to the Internet. Not an OpenCode IPC integration test.
$ErrorActionPreference = 'Stop'
$support = @'
namespace CodeBadger.Services
{
    public sealed class SandboxPermissionService
    {
        public event Action<Guid>? PermissionsRevoking;
        public int Grants { get; private set; }
        public Task<bool> IsAllowedAsync(Guid id, string path, Uri destination) => Task.FromResult(false);
        public Task GrantAsync(Guid id, string path, Uri destination) { Grants++; return Task.CompletedTask; }
        public void Revoke(Guid id) => PermissionsRevoking?.Invoke(id);
    }
}
public static class BrokerTests
{
    public static async Task Run()
    {
        var permissions = new CodeBadger.Services.SandboxPermissionService();
        var approvals = new CodeBadger.Services.SandboxApprovalService();
        Guid approvalRepository = Guid.NewGuid();
        if (await approvals.RequestAsync(approvalRepository, new Uri("https://example.com"), CancellationToken.None))
            throw new Exception("Approval without a host UI did not fail closed.");
        approvals.Requested += request => request.Decide(true);
        if (!await approvals.RequestAsync(approvalRepository, new Uri("https://example.com"), CancellationToken.None))
            throw new Exception("Host approval was not returned.");
        using (var canceledApproval = new CancellationTokenSource())
        {
            canceledApproval.Cancel();
            bool canceledRequest = false;
            try { await approvals.RequestAsync(approvalRepository, new Uri("https://example.com"), canceledApproval.Token); }
            catch (OperationCanceledException) { canceledRequest = true; }
            if (!canceledRequest) throw new Exception("Canceled approval was allowed.");
        }
        using var broker = new CodeBadger.Services.SandboxNetworkBroker(permissions);
        Guid id = Guid.NewGuid();
        int prompts = 0;
        foreach (string target in new[] { "http://example.com", "https://127.0.0.1", "https://[::1]", "https://10.0.0.1", "https://169.254.169.254", "https://example.com:8443", "https://user:password@example.com", "https://example.local" })
        {
            bool denied = false;
            try { await broker.GetAsync(id, "C:\\repository", new Uri(target), (origin, token) => { prompts++; return Task.FromResult(false); }); }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException) { denied = true; }
            if (!denied) throw new Exception("Invalid destination accepted: " + target);
        }
        if (prompts != 0) throw new Exception("Invalid destinations prompted for approval.");
        Uri seen = null;
        bool rejected = false;
        try
        {
            await broker.GetAsync(id, "C:\\repository", new Uri("https://example.com/path?token=test-only"),
                (origin, token) => { seen = origin; return Task.FromResult(false); });
        }
        catch (CodeBadger.Services.SandboxBrokerException) { rejected = true; }
        if (!rejected || seen.AbsoluteUri != "https://example.com/" || permissions.Grants != 0)
            throw new Exception("Rejected access was granted or prompt leaked a query.");
        rejected = false;
        try
        {
            await broker.CompleteAsync(id, "C:\\repository", new Uri("https://example.com/v1/chat/completions"), "test-host-key",
                "{\"model\":\"test-model\",\"messages\":[],\"stream\":false}", (origin, token) => Task.FromResult(false));
        }
        catch (CodeBadger.Services.SandboxBrokerException) { rejected = true; }
        if (!rejected || permissions.Grants != 0) throw new Exception("Completion bypassed approval.");
        foreach (string body in new[] { "{}", "{\"model\":\"test\",\"messages\":[],\"stream\":true}" })
        {
            bool invalid = false;
            try { await broker.CompleteAsync(id, "C:\\repository", new Uri("https://example.com/v1/chat/completions"), "test-key", body, (origin, token) => Task.FromResult(false)); }
            catch (ArgumentException) { invalid = true; }
            if (!invalid) throw new Exception("Invalid completion was accepted.");
        }
        var entered = new TaskCompletionSource<bool>();
        var pending = broker.GetAsync(id, "C:\\repository", new Uri("https://example.com"), async (origin, token) =>
        {
            entered.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        await entered.Task;
        permissions.Revoke(id);
        bool canceled = false;
        try { await pending; } catch (CodeBadger.Services.SandboxBrokerException) { canceled = true; }
        if (!canceled || permissions.Grants != 0) throw new Exception("Revocation did not cancel pending approval.");
        foreach (string ip in new[] { "0.0.0.0", "127.1.2.3", "10.0.0.1", "172.16.1.2", "192.168.1.1", "100.64.0.1", "169.254.1.2", "198.18.0.1", "224.0.0.1", "::1", "fc00::1", "fe80::1", "::ffff:127.0.0.1", "2001:db8::1", "2002::1" })
            if (CodeBadger.Services.SandboxNetworkBroker.IsPublicAddress(System.Net.IPAddress.Parse(ip)))
                throw new Exception("Non-public IP accepted: " + ip);
        foreach (string ip in new[] { "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111" })
            if (!CodeBadger.Services.SandboxNetworkBroker.IsPublicAddress(System.Net.IPAddress.Parse(ip)))
                throw new Exception("Public IP incorrectly blocked: " + ip);
        Console.WriteLine("Sandbox broker denial and revocation tests passed.");
    }
}
'@
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxNetworkBroker.cs')
$source = $source.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$usings = "#nullable enable annotations`nusing System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Net.Http; using System.Threading; using System.Threading.Tasks;"
$approval = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxApprovalService.cs')
$approval = $approval.Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
$errorSource = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\SandboxBrokerException.cs')).Replace('namespace CodeBadger.Services;', 'namespace CodeBadger.Services {') + '}'
Add-Type -TypeDefinition ($usings + $source + $approval + $errorSource + $support)
$null = [BrokerTests]::Run().GetAwaiter().GetResult()
