using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Composition root for application services.</summary>
public sealed class AppServices
{
    private readonly HashSet<Window> _editorWindows = [];

    private AppServices(Func<nint> ownerWindowProvider)
    {
        ScreenshotStorage = new TemporaryScreenshotStorage();
        ScreenshotDetector = new CompositeScreenshotDetector(
        [
            new ClipboardScreenshotDetector(ScreenshotStorage)
        ]);
        Clothesline = new ClotheslineService(
            ScreenshotDetector,
            ScreenshotStorage,
            DispatcherQueue.GetForCurrentThread());
        UserActions = new UserActionService(
            Clothesline,
            ScreenshotStorage,
            new WindowsScreenshotShell(),
            new WindowsScreenshotClipboard(),
            new WindowsScreenshotSavePicker(),
            ownerWindowProvider,
            OpenEditor);
    }

    public IScreenshotStorage ScreenshotStorage { get; }
    public IScreenshotDetector ScreenshotDetector { get; }
    public IClotheslineService Clothesline { get; }
    public IUserActionService UserActions { get; }
    public static AppServices Create(Func<nint> ownerWindowProvider) => new(ownerWindowProvider);

    private void OpenEditor(ScreenshotItem item)
    {
        var editor = new ScreenshotEditorWindow(item, ScreenshotStorage);
        _editorWindows.Add(editor);
        editor.Closed += (_, _) => _editorWindows.Remove(editor);
        editor.Activate();
    }
}
