using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Coordinates individual screenshot actions while delegating OS effects for testability.</summary>
public sealed class UserActionService(
    IClotheslineService clothesline,
    IScreenshotStorage storage,
    IScreenshotShell shell,
    IScreenshotClipboard clipboard,
    IScreenshotSavePicker savePicker,
    Func<nint> ownerWindowProvider,
    Action<ScreenshotItem> openEditor) : IUserActionService
{
    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(5);
    private readonly object _pendingLock = new();
    private readonly Dictionary<Guid, PendingDelete> _pendingDeletes = [];
    private readonly List<Guid> _pendingOrder = [];

    public event EventHandler? UndoStateChanged;

    public ScreenshotItem? UndoableItem
    {
        get
        {
            lock (_pendingLock)
                return _pendingOrder.Count == 0 ? null : _pendingDeletes[_pendingOrder[^1]].Item;
        }
    }

    public int PendingDeletionCount
    {
        get { lock (_pendingLock) return _pendingDeletes.Count; }
    }

    public async Task UndoDeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingDelete? pending;
        lock (_pendingLock)
        {
            if (_pendingOrder.Count == 0) return;
            var id = _pendingOrder[^1];
            _pendingOrder.RemoveAt(_pendingOrder.Count - 1);
            if (!_pendingDeletes.Remove(id, out pending)) return;
            pending.UndoRequested = true;
            pending.Cancellation.Cancel();
        }

        if (File.Exists(pending.Item.OriginalImagePath))
            await clothesline.RestoreAsync(pending.Item, cancellationToken);
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

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
        openEditor(item);
        return Task.CompletedTask;
    }

    public async Task CopyImageAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        await clipboard.CopyImageAsync(item.OriginalImagePath, cancellationToken);
        await CompleteClipboardCopyAsync(item, cancellationToken);
    }

    public async Task CopyFileAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        await clipboard.CopyFileAsync(item.OriginalImagePath, cancellationToken);
        await CompleteClipboardCopyAsync(item, cancellationToken);
    }

    private async Task CompleteClipboardCopyAsync(ScreenshotItem item, CancellationToken cancellationToken)
    {
        // Keep the temporary original in this session because the clipboard may still
        // reference it for delayed rendering or a later file paste. It is removed at
        // normal session cleanup, while the screenshot disappears from the clothesline now.
        item.CurrentState = ScreenshotState.Shared;
        await clothesline.RemoveFromLineAsync(item, cancellationToken);
    }

    public Task<bool> SaveAsync(ScreenshotItem item, CancellationToken cancellationToken = default) =>
        SaveToChosenPathAsync(item, cancellationToken);

    public Task<bool> SaveAsAsync(ScreenshotItem item, CancellationToken cancellationToken = default) =>
        SaveToChosenPathAsync(item, cancellationToken);

    private async Task<bool> SaveToChosenPathAsync(ScreenshotItem item, CancellationToken cancellationToken)
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

    public async Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureAvailable(item, cancellationToken);
        if (item.CurrentState == ScreenshotState.PendingDeletion) return;

        var pending = new PendingDelete(item);
        lock (_pendingLock)
        {
            item.CurrentState = ScreenshotState.PendingDeletion;
            _pendingDeletes[item.Id] = pending;
            _pendingOrder.Add(item.Id);
        }

        try
        {
            await clothesline.RemoveFromLineAsync(item, cancellationToken);
        }
        catch
        {
            lock (_pendingLock)
            {
                _pendingDeletes.Remove(item.Id);
                _pendingOrder.Remove(item.Id);
                item.CurrentState = ScreenshotState.Active;
            }
            pending.Cancellation.Dispose();
            throw;
        }

        UndoStateChanged?.Invoke(this, EventArgs.Empty);
        _ = FinalizeDeleteAsync(pending);
    }

    private async Task FinalizeDeleteAsync(PendingDelete pending)
    {
        try
        {
            await Task.Delay(UndoDuration, pending.Cancellation.Token);
            lock (_pendingLock)
            {
                if (pending.UndoRequested || !_pendingDeletes.Remove(pending.Item.Id)) return;
                _pendingOrder.Remove(pending.Item.Id);
            }
            UndoStateChanged?.Invoke(this, EventArgs.Empty);
            await storage.DeleteAsync(pending.Item, CancellationToken.None);
        }
        catch (OperationCanceledException) when (pending.UndoRequested)
        {
            // The temporary file remains available for the restored item.
        }
        catch (Exception)
        {
            // Startup cleanup can remove an orphan if the filesystem temporarily refuses deletion.
        }
        finally
        {
            lock (_pendingLock)
            {
                _pendingDeletes.Remove(pending.Item.Id);
                _pendingOrder.Remove(pending.Item.Id);
            }
            pending.Cancellation.Dispose();
            UndoStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class PendingDelete(ScreenshotItem item)
    {
        public ScreenshotItem Item { get; } = item;
        public CancellationTokenSource Cancellation { get; } = new();
        public volatile bool UndoRequested;
    }

    public Task CompleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default) =>
        clothesline.ConsumeAsync(item, cancellationToken);

    private static void EnsureAvailable(ScreenshotItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.CurrentState == ScreenshotState.Deleted || !File.Exists(item.OriginalImagePath))
            throw new FileNotFoundException("This screenshot is no longer available.", item.OriginalImagePath);
    }
}
