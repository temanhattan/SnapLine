using SnapLine.Models;
using System.Collections.ObjectModel;

namespace SnapLine.Services;

/// <summary>Coordinates detected files, storage, and the active temporary item collection.</summary>
public interface IClotheslineService
{
    ObservableCollection<ScreenshotItem> Items { get; }
    int MaxItems { get; set; }
    event EventHandler? ItemsChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SetCaptureHandlingAsync(bool enabled, CancellationToken cancellationToken = default);
    Task ConsumeAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task RemoveFromLineAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task RestoreAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
}
