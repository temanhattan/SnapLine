using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Entry point for actions initiated from a clothesline item.</summary>
public interface IUserActionService
{
    event EventHandler? UndoStateChanged;
    ScreenshotItem? UndoableItem { get; }
    int PendingDeletionCount { get; }
    Task UndoDeleteAsync(CancellationToken cancellationToken = default);
    Task ViewAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task EditAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task CopyImageAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task CopyFileAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task<bool> SaveAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task<bool> SaveAsAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task OpenWithAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task DiscardAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task SaveToDesktopAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task RevealAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    bool IsInInbox(ScreenshotItem item);
    Task CompleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
}
