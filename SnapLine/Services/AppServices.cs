using Microsoft.UI.Dispatching;

namespace SnapLine.Services;

/// <summary>Composition root for application services.</summary>
public sealed class AppServices
{
    private AppServices()
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
    }

    public IScreenshotStorage ScreenshotStorage { get; }
    public IScreenshotDetector ScreenshotDetector { get; }
    public IClotheslineService Clothesline { get; }
    public static AppServices Create() => new();
}
