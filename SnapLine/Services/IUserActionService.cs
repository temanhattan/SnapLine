using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Entry point for actions initiated from a clothesline item.</summary>
public interface IUserActionService
{
    Task CompleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
}
