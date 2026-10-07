namespace SnapLine.Services;

/// <summary>Operating-system effects are behind interfaces so action orchestration can be tested independently.</summary>
public interface IScreenshotShell
{
    void Open(string filePath);
    void Edit(string filePath);
    void OpenWith(string filePath, nint ownerWindow);
}

public interface IScreenshotClipboard
{
    Task CopyImageAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface IScreenshotSavePicker
{
    Task<string?> PickSavePathAsync(string suggestedName, string extension, nint ownerWindow,
        CancellationToken cancellationToken = default);
}
