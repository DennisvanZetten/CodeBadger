using CodeBadger.Models;

namespace CodeBadger.Services;

/// <summary>Creates and tracks native windows hosting a repository workspace.</summary>
public sealed class RepositoryDetachedWindowService(
    WindowTitleBarService titleBar)
{
    private readonly Dictionary<Guid, Window> _windows = [];
    private readonly HashSet<Guid> _docking = [];
    private readonly HashSet<Guid> _preservingSessions = [];

    public event Action? StateChanged;
    public event Action<Guid>? DockRequested;

    public bool IsDetached(Guid repositoryId) => _windows.ContainsKey(repositoryId) && !_docking.Contains(repositoryId);

    public bool ShouldPreserveSession(Guid repositoryId) => _preservingSessions.Contains(repositoryId);

    public bool IsDocking(Guid repositoryId) => _docking.Contains(repositoryId);

    public void BeginTransfer(Guid repositoryId) => _preservingSessions.Add(repositoryId);

    public void MarkWorkspaceAttached(Guid repositoryId)
    {
        _docking.Remove(repositoryId);
        _ = ClearSessionTransferAsync(repositoryId);
    }

    private async Task ClearSessionTransferAsync(Guid repositoryId)
    {
        await Task.Delay(5000);
        _preservingSessions.Remove(repositoryId);
    }

    public void RegisterDetached(Guid repositoryId, Window window)
    {
        if (_windows.TryGetValue(repositoryId, out var registered) && ReferenceEquals(registered, window))
            return;
        _windows[repositoryId] = window;
        window.Destroying += (_, _) =>
        {
            if (_windows.TryGetValue(repositoryId, out var current) && ReferenceEquals(current, window))
            {
                _windows.Remove(repositoryId);
                if (_docking.Contains(repositoryId))
                {
                    StateChanged?.Invoke();
                    return;
                }
                // Closing the floating window returns its workspace to the main tab strip.
                _docking.Add(repositoryId);
                _preservingSessions.Add(repositoryId);
                DockRequested?.Invoke(repositoryId);
                StateChanged?.Invoke();
            }
        };
        StateChanged?.Invoke();
    }

    public void Toggle(RepositoryEntry repository)
    {
        if (_windows.TryGetValue(repository.Id, out var existing))
        {
            Activate(existing);
            return;
        }

        var page = new MainPage($"/repository/{repository.Id}");
        var window = new Window(page)
        {
            Title = string.IsNullOrWhiteSpace(repository.Name) ? repository.Path : repository.Name,
            Width = 1100,
            Height = 760
        };
        titleBar.Configure(window, showSettings: false, title: window.Title);
        RegisterDetached(repository.Id, window);
        Application.Current?.OpenWindow(window);
    }

    public void Undock(RepositoryEntry repository)
    {
        if (!IsDetached(repository.Id))
        {
            BeginTransfer(repository.Id);
            Toggle(repository);
        }
    }

    public void Focus(Guid repositoryId)
    {
        if (_windows.TryGetValue(repositoryId, out var window))
            Activate(window);
    }

    public void Dock(Guid repositoryId)
    {
        if (!_windows.TryGetValue(repositoryId, out var window))
            return;
        _docking.Add(repositoryId);
        BeginTransfer(repositoryId);
        DockRequested?.Invoke(repositoryId);
        Application.Current?.CloseWindow(window);
        _ = ClearDockingAsync(repositoryId);
    }

    private async Task ClearDockingAsync(Guid repositoryId)
    {
        await Task.Delay(1500);
        if (_windows.ContainsKey(repositoryId))
            _docking.Remove(repositoryId);
    }

    private static void Activate(Window window)
    {
#if WINDOWS
        if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            nativeWindow.Activate();
#endif
    }
}
