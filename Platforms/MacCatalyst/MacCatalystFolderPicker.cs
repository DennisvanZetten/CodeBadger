using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace CodeBadger;

/// <summary>Native folder selection for Mac Catalyst repository access.</summary>
internal static class MacCatalystFolderPicker
{
    // Keep security-scoped URLs alive for the selected folders while the app is running.
    private static readonly List<NSUrl> SelectedFolders = [];
    private static FolderPickerDelegate? _activeDelegate;

    public static Task<string?> PickFolderAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as UIWindow;
        var presenter = window?.RootViewController;
        if (presenter is null)
            return null;

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new UIDocumentPickerViewController([UTTypes.Folder])
        {
            AllowsMultipleSelection = false
        };
        _activeDelegate = new FolderPickerDelegate(url =>
        {
            _activeDelegate = null;
            if (url is null)
            {
                completion.TrySetResult(null);
                return;
            }

            if (url.StartAccessingSecurityScopedResource())
                SelectedFolders.Add(url);
            completion.TrySetResult(url.Path);
        });
        picker.Delegate = _activeDelegate;
        presenter.PresentViewController(picker, true, null);
        return await completion.Task;
    });

    private sealed class FolderPickerDelegate(Action<NSUrl?> complete) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) =>
            complete(urls.FirstOrDefault());

        public override void WasCancelled(UIDocumentPickerViewController controller) => complete(null);
    }
}
