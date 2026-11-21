using System.Text.Json;
using CodeBadger.Models;

namespace CodeBadger.Services;

public sealed class IdeaListService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "ideas.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<IdeaItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return [];

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<IdeaItem>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IdeaItem> AddAsync(string title, string description, IdeaType type,
        CancellationToken cancellationToken = default)
    {
        var item = new IdeaItem
        {
            Title = title.Trim(),
            Description = description.Trim(),
            Type = type
        };
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAllCoreAsync(cancellationToken);
            items.Add(item);
            await SaveCoreAsync(items, cancellationToken);
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateStatusAsync(Guid id, IdeaStatus status, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAllCoreAsync(cancellationToken);
            var item = items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
                return false;
            item.Status = status;
            await SaveCoreAsync(items, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveTicketAsync(Guid id, string ticketNumber, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAllCoreAsync(cancellationToken);
            var item = items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
                return false;
            item.TicketNumber = ticketNumber.Trim();
            await SaveCoreAsync(items, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> LinkSessionAsync(Guid id, RepositoryEntry repository, string sessionId, string sessionTitle,
        string ticketNumber, string branchName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAllCoreAsync(cancellationToken);
            var item = items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
                return false;

            item.RepositoryId = repository.Id;
            item.RepositoryName = string.IsNullOrWhiteSpace(repository.Name)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.Path))
                : repository.Name;
            item.RepositoryPath = repository.Path;
            item.SessionId = sessionId;
            item.SessionTitle = sessionTitle;
            item.TicketNumber = ticketNumber;
            item.BranchName = branchName;
            item.Status = IdeaStatus.InProgress;
            await SaveCoreAsync(items, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadAllCoreAsync(cancellationToken);
            if (items.RemoveAll(item => item.Id == id) == 0)
                return false;
            await SaveCoreAsync(items, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<IdeaItem>> ReadAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<IdeaItem>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveCoreAsync(List<IdeaItem> items, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
