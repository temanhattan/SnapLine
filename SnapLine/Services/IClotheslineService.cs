using SnapLine.Models;
using System.Collections.ObjectModel;

namespace SnapLine.Services;

/// <summary>Coordinates detected files, storage, and the active temporary item collection.</summary>
public interface IClotheslineService
{
    ObservableCollection<ScreenshotItem> Items { get; }
    event EventHandler? ItemsChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ConsumeAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
    Task RemoveFromLineAsync(ScreenshotItem item, CancellationToken cancellationToken = default);
}
