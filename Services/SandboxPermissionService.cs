using System.Text.Json;
using CodeBadger.Models;

namespace CodeBadger.Services;

/// <summary>
/// Durable network approval metadata. A broker must still enforce approvals on
/// every request; recording an approval does not grant Windows network rights.
/// </summary>
public sealed class SandboxPermissionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WindowsTerminalSessionService _terminals;
    private readonly WindowsSandboxSessionService _sandboxes;
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "sandbox-permissions.json");
    public event Action<Guid>? PermissionsChanged;
    public event Action<Guid>? PermissionsRevoking;

    public SandboxPermissionService(WindowsTerminalSessionService terminals, WindowsSandboxSessionService sandboxes)
    {
        _terminals = terminals;
        _sandboxes = sandboxes;
    }

    public async Task<IReadOnlyList<SandboxPermission>> GetAsync(Guid repositoryId, string repositoryPath)
    {
        var canonicalPath = CanonicalPath(repositoryPath);
        await _gate.WaitAsync();
        try
        {
            return (await LoadAsync()).Where(permission => permission.RepositoryId == repositoryId &&
                string.Equals(permission.RepositoryPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(permission => permission.GrantedAt).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsAllowedAsync(Guid repositoryId, string repositoryPath, Uri destination)
    {
        string origin = Origin(destination);
        return (await GetAsync(repositoryId, repositoryPath)).Any(permission => permission.RevokedAt is null && permission.Origin == origin);
    }

    // Call only after the host UI has approved a specific destination. The
    // sandbox must not be able to call this method through its IPC interface.
    public async Task GrantAsync(Guid repositoryId, string repositoryPath, Uri destination)
    {
        if (repositoryId == Guid.Empty) throw new ArgumentException("A repository ID is required.", nameof(repositoryId));
        string origin = Origin(destination);
        string canonicalPath = CanonicalPath(repositoryPath);
        await _gate.WaitAsync();
        try
        {
            var permissions = await LoadAsync();
            if (permissions.Any(permission => permission.RepositoryId == repositoryId && permission.RepositoryPath == canonicalPath &&
                permission.Origin == origin && permission.RevokedAt is null)) return;
            permissions.Add(new SandboxPermission { RepositoryId = repositoryId, RepositoryPath = canonicalPath, Origin = origin });
            await SaveAsync(permissions);
        }
        finally { _gate.Release(); }
        PermissionsChanged?.Invoke(repositoryId);
    }

    public async Task RevokeAsync(Guid repositoryId, string repositoryPath, Guid permissionId)
    {
        string canonicalPath = CanonicalPath(repositoryPath);
        await _gate.WaitAsync();
        try
        {
            var permissions = await LoadAsync();
            var permission = permissions.FirstOrDefault(permission => permission.RepositoryId == repositoryId &&
                permission.Id == permissionId && permission.RepositoryPath == canonicalPath);
            if (permission is null || permission.RevokedAt is not null) return;
            PermissionsRevoking?.Invoke(repositoryId);
            await _sandboxes.StopAsync(repositoryId);
            // Stop before committing a revocation. No "revoked" success is
            // reported while a known terminal is intentionally left running.
            await _terminals.StopAsync(repositoryId, requireTermination: true);
            permission.RevokedAt = DateTimeOffset.UtcNow;
            await SaveAsync(permissions);
        }
        finally { _gate.Release(); }
        PermissionsChanged?.Invoke(repositoryId);
    }

    private async Task<List<SandboxPermission>> LoadAsync()
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        // Corruption fails closed; do not silently reset the user's approvals.
        return await JsonSerializer.DeserializeAsync<List<SandboxPermission>>(stream)
            ?? throw new InvalidDataException("The sandbox permission store is invalid.");
    }

    private async Task SaveAsync(List<SandboxPermission> permissions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, permissions, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute repository path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string Origin(Uri destination)
    {
        if (!destination.IsAbsoluteUri || destination.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(destination.UserInfo))
            throw new ArgumentException("Only HTTPS destinations without embedded credentials are supported.", nameof(destination));
        return new UriBuilder(Uri.UriSchemeHttps, destination.IdnHost, destination.Port).Uri.GetLeftPart(UriPartial.Authority);
    }
}
