using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using SnapLine.Models;

namespace SnapLine.Services;

public sealed class ClotheslineService(
    IScreenshotDetector detector,
    IScreenshotStorage storage,
    DispatcherQueue dispatcherQueue) : IClotheslineService
{
    public ObservableCollection<ScreenshotItem> Items { get; } = [];
    public event EventHandler? ItemsChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await storage.InitializeAsync(cancellationToken);
        detector.ScreenshotDetected += OnScreenshotDetectedAsync;
        await detector.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        detector.ScreenshotDetected -= OnScreenshotDetectedAsync;
        await detector.StopAsync(cancellationToken);
    }

    private async Task OnScreenshotDetectedAsync(object sender, ScreenshotDetectedEventArgs args)
    {
        var item = await storage.StoreAsync(args.FilePath);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                Items.Add(item);
                ItemsChanged?.Invoke(this, EventArgs.Empty);
                completion.SetResult();
            }))
        {
            await storage.DeleteAsync(item);
            return;
        }

        await completion.Task;
    }
}
