using Microsoft.UI.Dispatching;

namespace SnapLine.Services;

/// <summary>Composition root for application services.</summary>
public sealed class AppServices
{
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
            ownerWindowProvider);
    }

    public IScreenshotStorage ScreenshotStorage { get; }
    public IScreenshotDetector ScreenshotDetector { get; }
    public IClotheslineService Clothesline { get; }
    public IUserActionService UserActions { get; }
    public static AppServices Create(Func<nint> ownerWindowProvider) => new(ownerWindowProvider);
}
