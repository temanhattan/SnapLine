using Microsoft.UI.Xaml.Media.Imaging;
using SnapLine.Services;
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
    private BitmapImage? _thumbnailImage;
    private bool _isCopied;
    private bool _isHovered;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id { get; } = id;
    public string FilePath { get; } = filePath;
    public string OriginalImagePath => FilePath;
    public string ThumbnailPath => _thumbnailPath;
    public BitmapImage ThumbnailImage => _thumbnailImage ??= new BitmapImage(new Uri(ThumbnailPath));
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (_isCopied == value) return;
            _isCopied = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CopiedVisibility));
        }
    }
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered == value) return;
            _isHovered = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiscardVisibility));
        }
    }
    public Microsoft.UI.Xaml.Visibility DiscardVisibility => _isHovered
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility CopiedVisibility => _isCopied
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string CopiedLabel => Localization.Get("Copied");
    public string DiscardLabel => Localization.Get("MoveToRecycleBin");
    public string GestureTip => Localization.Get("GestureTip");
    public double TiltDegrees { get; } = Random.Shared.NextDouble() * 5d - 2.5d;
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
        _thumbnailImage = null;
        OnPropertyChanged(nameof(ThumbnailPath));
        OnPropertyChanged(nameof(ThumbnailImage));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
