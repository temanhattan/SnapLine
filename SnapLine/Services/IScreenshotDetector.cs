namespace SnapLine.Services;

/// <summary>Reports newly created screenshot files. Detection mechanisms are supplied separately.</summary>
public interface IScreenshotDetector
{
    event ScreenshotDetectedHandler? ScreenshotDetected;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public delegate Task ScreenshotDetectedHandler(object sender, ScreenshotDetectedEventArgs args);

public sealed class ScreenshotDetectedEventArgs(string filePath, DateTimeOffset capturedAt)
{
    public string FilePath { get; } = filePath;
    public DateTimeOffset CapturedAt { get; } = capturedAt;
}
