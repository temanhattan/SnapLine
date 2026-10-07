using SnapLine.Models;

namespace SnapLine.Services;

/// <summary>Owns persisted screenshot files and their lifecycle.</summary>
public interface IScreenshotStorage
{
    string TemporaryRootPath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ScreenshotItem> StoreAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<string> SaveAsync(ScreenshotItem item, string destinationPath, CancellationToken cancellationToken = default);
    Task ReplaceImageAsync(ScreenshotItem item, byte[] pixels, uint width, uint height, CancellationToken cancellationToken = default);
    Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
}
