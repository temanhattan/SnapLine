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

    public async Task ConsumeAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        if (!Items.Contains(item)) return;
        await storage.DeleteAsync(item, cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                Items.Remove(item);
                ItemsChanged?.Invoke(this, EventArgs.Empty);
                completion.SetResult();
            }))
            throw new InvalidOperationException("The UI dispatcher is no longer available.");

        await completion.Task;
    }

    public async Task RemoveFromLineAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                Items.Remove(item);
                ItemsChanged?.Invoke(this, EventArgs.Empty);
                completion.SetResult();
            }))
            throw new InvalidOperationException("The UI dispatcher is no longer available.");

        await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task OnScreenshotDetectedAsync(object sender, ScreenshotDetectedEventArgs args)
    {
        var item = await storage.StoreAsync(args.FilePath);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                item.CurrentState = ScreenshotState.Active;
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
