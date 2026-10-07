using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SnapLine.Models;

public enum ScreenshotState
{
    Pending,
    Active,
    Viewed,
    Shared,
    PendingDeletion,
    Saved,
    Deleted
}

public sealed class ScreenshotItem(Guid id, string filePath, string thumbnailPath, DateTimeOffset createdAt) : INotifyPropertyChanged
{
    private string _thumbnailPath = thumbnailPath;
    private ScreenshotState _currentState = ScreenshotState.Pending;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id { get; } = id;
    public string FilePath { get; } = filePath;
    public string OriginalImagePath => FilePath;
    public string ThumbnailPath => _thumbnailPath;
    public BitmapImage ThumbnailImage => new(new Uri(ThumbnailPath));
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public string CreatedAtLabel => CreatedAt.ToLocalTime().ToString("h:mm tt");
    public ScreenshotState CurrentState
    {
        get => _currentState;
        internal set
        {
            if (_currentState == value) return;
            _currentState = value;
            OnPropertyChanged();
        }
    }

    internal void UpdateThumbnailPath(string path)
    {
        _thumbnailPath = path;
        OnPropertyChanged(nameof(ThumbnailPath));
        OnPropertyChanged(nameof(ThumbnailImage));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
