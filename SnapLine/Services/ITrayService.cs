namespace SnapLine.Services;

/// <summary>Provides a discoverable entry point for restoring or exiting the background app.</summary>
public interface ITrayService : IDisposable
{
    event EventHandler? ShowRequested;
    event EventHandler? HideRequested;
    event EventHandler? SettingsRequested;
    event EventHandler? ClearRequested;
    event EventHandler? OpenScreenshotsFolderRequested;
    event EventHandler? OpenLogFolderRequested;
    event EventHandler? ExitRequested;
    void Start();
}
