using Microsoft.UI.Xaml.Media.Imaging;

namespace SnapLine.Models;

public enum ScreenshotState
{
    Pending,
    Active,
    Viewed,
    Shared,
    Saved,
    Deleted
}

public sealed class ScreenshotItem(Guid id, string filePath, string thumbnailPath, DateTimeOffset createdAt)
{
    public Guid Id { get; } = id;
    public string FilePath { get; } = filePath;
    public string OriginalImagePath => FilePath;
    public string ThumbnailPath { get; } = thumbnailPath;
    public BitmapImage ThumbnailImage => new(new Uri(ThumbnailPath));
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public string CreatedAtLabel => CreatedAt.ToLocalTime().ToString("h:mm tt");
    public ScreenshotState CurrentState { get; internal set; } = ScreenshotState.Pending;
}
