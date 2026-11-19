namespace CodeBadger.Models;

// Metadata only: never store tokens, passwords, HTTP headers or request bodies.
public sealed class SandboxPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string RepositoryPath { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}
