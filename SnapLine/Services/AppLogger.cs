using System.Text;

namespace SnapLine.Services;

/// <summary>Small append-only local logger for unexpected app and capture failures.</summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapLine", "logs", "snapline.log");

    public static void Write(string area, Exception exception) => Write(area, exception.ToString());

    public static void Write(string area, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:O}] [{area}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception) { /* Logging must never take down the app. */ }
    }
}
