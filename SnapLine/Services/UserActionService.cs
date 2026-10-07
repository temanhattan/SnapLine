using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Coordinates individual screenshot actions while delegating OS effects for testability.</summary>
public sealed class UserActionService(
    IClotheslineService clothesline,
    IScreenshotStorage storage,
    IScreenshotShell shell,
    IScreenshotClipboard clipboard,
    IScreenshotSavePicker savePicker,
    Func<nint> ownerWindowProvider) : IUserActionService
{
    public Task ViewAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        shell.Open(item.OriginalImagePath);
        item.CurrentState = ScreenshotState.Viewed;
        return Task.CompletedTask;
    }

    public Task EditAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        shell.Edit(item.OriginalImagePath);
        item.CurrentState = ScreenshotState.Viewed;
        return Task.CompletedTask;
    }

    public async Task CopyAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        await clipboard.CopyImageAsync(item.OriginalImagePath, cancellationToken);
        item.CurrentState = ScreenshotState.Shared;
    }

    public async Task<bool> SaveAsAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        var extension = Path.GetExtension(item.OriginalImagePath);
        var name = $"SnapLine-{item.CreatedAt.ToLocalTime():yyyyMMdd-HHmmss}";
        var destination = await savePicker.PickSavePathAsync(name, extension, ownerWindowProvider(), cancellationToken);
        if (string.IsNullOrWhiteSpace(destination)) return false;

        await storage.SaveAsync(item, destination, cancellationToken);
        await clothesline.RemoveFromLineAsync(item, cancellationToken);
        return true;
    }

    public Task OpenWithAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        shell.OpenWith(item.OriginalImagePath, ownerWindowProvider());
        return Task.CompletedTask;
    }

    public Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default) =>
        clothesline.ConsumeAsync(item, cancellationToken);

    public Task CompleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default) =>
        DeleteAsync(item, cancellationToken);

    private static void EnsureAvailable(ScreenshotItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.CurrentState == ScreenshotState.Deleted || !File.Exists(item.OriginalImagePath))
            throw new FileNotFoundException("This screenshot is no longer available.", item.OriginalImagePath);
    }
}
