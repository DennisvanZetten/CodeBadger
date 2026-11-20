using CodeBadger.Models;
using System.Runtime.InteropServices;

namespace CodeBadger.Services;

/// <summary>Creates and tracks native windows hosting a repository workspace.</summary>
public sealed class RepositoryDetachedWindowService(
    WindowTitleBarService titleBar)
{
    private readonly Dictionary<Guid, Window> _windows = [];
    private readonly HashSet<Guid> _docking = [];
    private readonly HashSet<Guid> _preservingSessions = [];
    private readonly Dictionary<Guid, (int PointerX, int PointerY, int WindowX, int WindowY)> _windowDrags = [];
    private readonly Dictionary<Guid, (int PointerX, int PointerY)> _pendingWindowDrags = [];

    public event Action? StateChanged;
    public event Action<Guid>? DockRequested;
    public event Action<bool>? DockDragChanged;

    public bool IsDetached(Guid repositoryId) => _windows.ContainsKey(repositoryId) && !_docking.Contains(repositoryId);

    public bool ShouldPreserveSession(Guid repositoryId) => _preservingSessions.Contains(repositoryId);

    public bool IsDocking(Guid repositoryId) => _docking.Contains(repositoryId);

    public void SetDockDragActive(bool active) => DockDragChanged?.Invoke(active);

    public void BeginDetachedWindowDrag(Guid repositoryId, int pointerX, int pointerY)
    {
#if WINDOWS
        if (_windows.TryGetValue(repositoryId, out var window) &&
            window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
        {
            var position = nativeWindow.AppWindow.Position;
            var start = _pendingWindowDrags.Remove(repositoryId, out var pending) ? pending : (pointerX, pointerY);
            _windowDrags[repositoryId] = (start.Item1, start.Item2, position.X, position.Y);
            MoveDetachedWindow(repositoryId, pointerX, pointerY);
        }
#endif
    }

    public bool MoveDetachedWindow(Guid repositoryId, int pointerX, int pointerY)
    {
#if WINDOWS
        if (!_windowDrags.ContainsKey(repositoryId))
            BeginDetachedWindowDrag(repositoryId, pointerX, pointerY);
        if (_windowDrags.TryGetValue(repositoryId, out var drag) && _windows.TryGetValue(repositoryId, out var window) &&
            window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
        {
            nativeWindow.AppWindow.Move(new Windows.Graphics.PointInt32(
                drag.WindowX + pointerX - drag.PointerX,
                drag.WindowY + pointerY - drag.PointerY));
            return true;
        }
#endif
        return false;
    }

    public void EndDetachedWindowDrag(Guid repositoryId) => _windowDrags.Remove(repositoryId);

    public bool TryDockAtCursor(Guid repositoryId)
    {
        EndDetachedWindowDrag(repositoryId);
        var overMainWindow = false;
#if WINDOWS
        if (GetCursorPos(out var cursor))
        {
            var mainWindow = Application.Current?.Windows.FirstOrDefault(window =>
                window.Page is MainPage page && page.DetachedRepositoryId is null);
            if (mainWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                var position = nativeWindow.AppWindow.Position;
                var size = nativeWindow.AppWindow.Size;
                overMainWindow = cursor.X >= position.X && cursor.X < position.X + size.Width &&
                                 cursor.Y >= position.Y && cursor.Y < position.Y + size.Height;
            }
        }
#endif
        DockDragChanged?.Invoke(false);
        if (!overMainWindow)
            return false;
        Dock(repositoryId);
        return true;
    }

#if WINDOWS
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
#endif

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

    public void Undock(RepositoryEntry repository, int? pointerX = null, int? pointerY = null)
    {
        if (!IsDetached(repository.Id))
        {
            BeginTransfer(repository.Id);
            if (pointerX is { } x && pointerY is { } y)
                _pendingWindowDrags[repository.Id] = (x, y);
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
