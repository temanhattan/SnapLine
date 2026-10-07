using System.Text.Json;

namespace SnapLine.Services;

public static class AppPreferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapLine", "preferences.json");
    private static PreferenceState _state = Load();

    public static bool HandleScreenshots { get => _state.HandleScreenshots; set { _state.HandleScreenshots = value; Save(); } }
    public static bool SetupCompleted { get => _state.SetupCompleted; set { _state.SetupCompleted = value; Save(); } }
    public static bool SoundsEnabled { get => _state.SoundsEnabled; set { _state.SoundsEnabled = value; Save(); } }
    public static bool CaptureClipboard { get => _state.CaptureClipboard; set { _state.CaptureClipboard = value; Save(); } }

    private static PreferenceState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PreferenceState>(File.ReadAllText(FilePath)) ?? new PreferenceState();
        }
        catch (IOException) { }
        catch (JsonException) { }
        return new PreferenceState();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var staged = FilePath + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(_state));
            File.Move(staged, FilePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class PreferenceState
    {
        public bool HandleScreenshots { get; set; }
        public bool SetupCompleted { get; set; }
        public bool SoundsEnabled { get; set; } = true;
        public bool CaptureClipboard { get; set; } = true;
    }
}
