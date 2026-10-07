using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Entry point for actions initiated from a clothesline item.</summary>
public interface IUserActionService
{
    Task ViewAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task EditAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task CopyAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task<bool> SaveAsAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task OpenWithAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task CompleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
}
