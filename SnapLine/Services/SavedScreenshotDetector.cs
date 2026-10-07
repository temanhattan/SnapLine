using System.Runtime.InteropServices;

namespace SnapLine.Services;

/// <summary>
/// Picks up screenshots that Windows writes to its standard screenshot folders.
/// Clipboard capture remains the primary path for Snipping Tool and Win+Shift+S.
/// </summary>
public sealed class SavedScreenshotDetector : IScreenshotDetector
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".gif" };
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _reportedOrder = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event ScreenshotDetectedHandler? ScreenshotDetected;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_watchers.Count > 0) return Task.CompletedTask;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        AddWatcher(desktop, requireScreenshotName: true);
        AddWatcher(GetScreenshotsFolder(pictures), requireScreenshotName: false);
        AddWatcher(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures"), requireScreenshotName: false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        lock (_gate)
        {
            _reported.Clear();
            _reportedOrder.Clear();
            foreach (var pending in _pendingCandidates.Values)
                pending.Cancel();
            _pendingCandidates.Clear();
        }
        return Task.CompletedTask;
    }

    private void AddWatcher(string folder, bool requireScreenshotName)
    {
        if (!Directory.Exists(folder)) return;
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = false
        };
        watcher.Created += OnCreated;
        watcher.Changed += OnCreated;
        watcher.Renamed += OnRenamed;
        watcher.Error += (_, _) => Rescan(folder, requireScreenshotName);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);

        void OnCreated(object? _, FileSystemEventArgs args) => QueueCandidate(args.FullPath, requireScreenshotName);
        void OnRenamed(object? _, RenamedEventArgs args) => QueueCandidate(args.FullPath, requireScreenshotName);
    }

    private void Rescan(string folder, bool requireScreenshotName)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
                QueueCandidate(path, requireScreenshotName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetScreenshotsFolder(string fallback)
    {
        // FOLDERID_Screenshots; resolve through the Known Folder API so user redirection is honored.
        var id = new Guid("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
        nint path = nint.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, nint.Zero, out path) == 0 && path != nint.Zero)
                return Marshal.PtrToStringUni(path) ?? Path.Combine(fallback, "Screenshots");
        }
        catch (DllNotFoundException) { }
        finally { if (path != nint.Zero) Marshal.FreeCoTaskMem(path); }
        return Path.Combine(fallback, "Screenshots");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out nint path);

    private void QueueCandidate(string path, bool requireScreenshotName)
    {
        var extension = Path.GetExtension(path);
        if (!ImageExtensions.Contains(extension)) return;
        if (requireScreenshotName && !LooksLikeScreenshotName(Path.GetFileNameWithoutExtension(path))) return;

        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_pendingCandidates.TryGetValue(fullPath, out var previous))
                previous.Cancel();
            var debounce = new CancellationTokenSource();
            _pendingCandidates[fullPath] = debounce;
            _ = ProcessWhenReadyAsync(fullPath, debounce.Token);
        }
    }

    private async Task ProcessWhenReadyAsync(string path, CancellationToken cancellationToken)
    {
        // FileSystemWatcher fires before the screenshot encoder necessarily closes the file.
        long previousLength = -1;
        DateTime previousWrite = DateTime.MinValue;
        var stableSamples = 0;
        var delivered = false;
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(150, cancellationToken);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    stableSamples = 0;
                    continue;
                }
                if (info.Length == previousLength && info.LastWriteTimeUtc == previousWrite)
                    stableSamples++;
                else
                    stableSamples = 0;
                previousLength = info.Length;
                previousWrite = info.LastWriteTimeUtc;
                if (stableSamples < 2) continue;

                try
                {
                    using var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                catch (IOException)
                {
                    stableSamples = 0;
                    continue;
                }
                var handler = ScreenshotDetected;
                if (handler is null)
                {
                    delivered = true;
                    return;
                }
                var args = new ScreenshotDetectedEventArgs(path, DateTimeOffset.UtcNow, "file");
                try
                {
                    foreach (ScreenshotDetectedHandler subscriber in handler.GetInvocationList())
                        await subscriber(this, args);
                    lock (_gate)
                    {
                        _reported.Add(Path.GetFullPath(path));
                        _reportedOrder.Enqueue(Path.GetFullPath(path));
                        while (_reportedOrder.Count > 2048)
                            _reported.Remove(_reportedOrder.Dequeue());
                    }
                    delivered = true;
                    return;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    AppLogger.Write("Screenshot dispatch retry", exception);
                    stableSamples = 0;
                }
            }
            AppLogger.Write("Screenshot watcher", $"File did not settle or could not be read: {path}");
        }
        catch (Exception exception)
        {
            AppLogger.Write("Screenshot watcher", exception);
        }
        finally
        {
            if (!delivered)
            {
                lock (_gate)
                {
                    if (_pendingCandidates.TryGetValue(Path.GetFullPath(path), out var pending))
                    {
                        _pendingCandidates.Remove(Path.GetFullPath(path));
                        pending.Dispose();
                    }
                }
            }
        }
    }

    private static bool LooksLikeScreenshotName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("screenshot") || lower.Contains("screen shot") ||
               lower.Contains("snip") || lower.Contains("capture");
    }
}
