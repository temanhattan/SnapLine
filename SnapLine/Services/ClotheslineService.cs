using System.Collections.ObjectModel;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using SnapLine.Models;

namespace SnapLine.Services;

public sealed class ClotheslineService(
    IScreenshotDetector detector,
    IScreenshotStorage storage,
    DispatcherQueue dispatcherQueue) : IClotheslineService
{
    public ObservableCollection<ScreenshotItem> Items { get; } = [];
    public event EventHandler? ItemsChanged;
    private readonly object _captureHashLock = new();
    private readonly Dictionary<string, DateTimeOffset> _recentCaptureHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _recentCapturePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapLine", "settings.json");
    private DispatcherQueueTimer? _missingFileTimer;
    private int _maxItems = 8;
    public int MaxItems
    {
        get => _maxItems;
        set
        {
            _maxItems = Math.Clamp(value, 3, 12);
            EnforceCapacity(Guid.Empty);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await storage.InitializeAsync(cancellationToken);
        await RestorePathsAsync(cancellationToken);
        Items.CollectionChanged += (_, _) => SavePaths();
        _missingFileTimer = dispatcherQueue.CreateTimer();
        _missingFileTimer.Interval = TimeSpan.FromSeconds(30);
        _missingFileTimer.Tick += (_, _) => RemoveMissingFiles();
        _missingFileTimer.Start();
        if (AppPreferences.HandleScreenshots)
            await SetCaptureHandlingAsync(true, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        detector.ScreenshotDetected -= OnScreenshotDetectedAsync;
        _missingFileTimer?.Stop();
        await detector.StopAsync(cancellationToken);
    }

    public async Task SetCaptureHandlingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            detector.ScreenshotDetected -= OnScreenshotDetectedAsync;
            detector.ScreenshotDetected += OnScreenshotDetectedAsync;
            await detector.StartAsync(cancellationToken);
        }
        else
        {
            detector.ScreenshotDetected -= OnScreenshotDetectedAsync;
            await detector.StopAsync(cancellationToken);
        }
        AppPreferences.HandleScreenshots = enabled;
    }

    public async Task ConsumeAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        if (!Items.Contains(item)) return;
        await RemoveFromLineAsync(item, cancellationToken);
    }

    public async Task RemoveFromLineAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                Items.Remove(item);
                item.CurrentState = ScreenshotState.Active;
                ItemsChanged?.Invoke(this, EventArgs.Empty);
                SavePaths();
                completion.SetResult();
            }))
            throw new InvalidOperationException("The UI dispatcher is no longer available.");

        await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task RestoreAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                if (!Items.Contains(item))
                {
                    item.CurrentState = ScreenshotState.Active;
                    Items.Add(item);
                    EnforceCapacity(item.Id);
                    ItemsChanged?.Invoke(this, EventArgs.Empty);
                    SavePaths();
                }
                completion.SetResult();
            }))
            throw new InvalidOperationException("The UI dispatcher is no longer available.");

        await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task OnScreenshotDetectedAsync(object sender, ScreenshotDetectedEventArgs args)
    {
        var fullPath = Path.GetFullPath(args.FilePath);
        if (!File.Exists(fullPath))
        {
            AppLogger.Write("Capture candidate", $"source={sender.GetType().Name} decision=missing path={fullPath}");
            return;
        }
        lock (_captureHashLock)
        {
            var now = args.CapturedAt;
            foreach (var expired in _recentCapturePaths.Where(pair => now - pair.Value > TimeSpan.FromSeconds(5))
                         .Select(pair => pair.Key).ToArray())
                _recentCapturePaths.Remove(expired);
            if (_recentCapturePaths.TryGetValue(fullPath, out var previous) &&
                now - previous <= TimeSpan.FromSeconds(5))
            {
                AppLogger.Write("Capture candidate", $"source={sender.GetType().Name} decision=duplicate-of-path path={fullPath}");
                return;
            }
            _recentCapturePaths[fullPath] = now;
        }

        await using (var source = new FileStream(args.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
            var now = args.CapturedAt;
            lock (_captureHashLock)
            {
                foreach (var expired in _recentCaptureHashes.Where(pair => now - pair.Value > TimeSpan.FromSeconds(3))
                             .Select(pair => pair.Key).ToArray())
                    _recentCaptureHashes.Remove(expired);
                if (string.Equals(args.Source, "clipboard", StringComparison.Ordinal) &&
                    _recentCaptureHashes.TryGetValue(hash, out var previous) &&
                    Math.Abs((now - previous).TotalSeconds) <= 3)
                {
                    AppLogger.Write("Capture candidate", $"source={sender.GetType().Name} decision=duplicate-of-content path={fullPath}");
                    return;
                }
                _recentCaptureHashes[hash] = now;
                ClipboardCaptureMemory.Remember(hash, now);
            }
        }

        AppLogger.Write("Capture candidate", $"source={sender.GetType().Name} decision=added path={fullPath}");
        var item = await storage.StoreAsync(fullPath);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                item.CurrentState = ScreenshotState.Active;
                Items.Add(item);
                EnforceCapacity(item.Id);
                ItemsChanged?.Invoke(this, EventArgs.Empty);
                SavePaths();
                if (AppPreferences.SoundsEnabled) WindowsSoundEffects.Capture();
                completion.SetResult();
            }))
        {
            return;
        }

        await completion.Task;
    }

    private async Task RestorePathsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath)) return;
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(_settingsPath, cancellationToken)) ?? [];
            foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(MaxItems))
            {
                var item = await storage.StoreAsync(path, cancellationToken);
                item.CurrentState = ScreenshotState.Active;
                Items.Add(item);
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private void EnforceCapacity(Guid newestId)
    {
        while (Items.Count > Math.Max(1, MaxItems))
        {
            var oldest = Items.FirstOrDefault(item => item.Id != newestId) ?? Items[0];
            Items.Remove(oldest);
        }
    }

    private void SavePaths()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Items.Select(item => item.OriginalImagePath).ToArray()));
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        catch (Exception exception) { AppLogger.Write("Persist screenshot paths", exception); }
    }

    private void RemoveMissingFiles()
    {
        var missing = Items.Where(item => !File.Exists(item.OriginalImagePath)).ToArray();
        if (missing.Length == 0) return;
        foreach (var item in missing) Items.Remove(item);
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        SavePaths();
    }
}
