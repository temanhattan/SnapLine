using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace SnapLine.Services;

/// <summary>
/// Detects image captures published to the Windows clipboard, including Win + Shift + S.
/// Other capture providers can implement IScreenshotDetector and feed the same clothesline pipeline.
/// </summary>
public sealed class ClipboardScreenshotDetector(IScreenshotStorage storage) : IScreenshotDetector
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private bool _started;
    private uint _lastClipboardSequence;

    public event ScreenshotDetectedHandler? ScreenshotDetected;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started) return Task.CompletedTask;
        Clipboard.ContentChanged += OnClipboardContentChanged;
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started) return Task.CompletedTask;
        Clipboard.ContentChanged -= OnClipboardContentChanged;
        _started = false;
        return Task.CompletedTask;
    }

    private async void OnClipboardContentChanged(object? sender, object args)
    {
        await _captureGate.WaitAsync();
        string? stagingPath = null;
        try
        {
            var sequence = GetClipboardSequenceNumber();
            if (sequence == _lastClipboardSequence) return;

            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap)) return;

            var bitmapReference = await content.GetBitmapAsync();
            using var bitmapStream = await bitmapReference.OpenReadAsync();
            var stagingDirectory = Path.Combine(storage.TemporaryRootPath, "incoming");
            Directory.CreateDirectory(stagingDirectory);
            stagingPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.png");
            await WriteStreamAsync(bitmapStream, stagingPath);

            _lastClipboardSequence = sequence;
            var handler = ScreenshotDetected;
            if (handler is not null)
            {
                var detected = new ScreenshotDetectedEventArgs(stagingPath, DateTimeOffset.UtcNow);
                foreach (ScreenshotDetectedHandler subscriber in handler.GetInvocationList())
                    await subscriber(this, detected);
            }
        }
        catch (Exception)
        {
            // Clipboard ownership can change while Windows is providing a delayed bitmap.
            // Ignore that update; the next clipboard event remains eligible for processing.
        }
        finally
        {
            if (stagingPath is not null)
            {
                try { File.Delete(stagingPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _captureGate.Release();
        }
    }

    private static async Task WriteStreamAsync(IRandomAccessStream stream, string destinationPath)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ulong remaining = stream.Size;
        while (remaining > 0)
        {
            var count = (uint)Math.Min(64 * 1024UL, remaining);
            await reader.LoadAsync(count);
            var bytes = new byte[(int)count];
            reader.ReadBytes(bytes);
            await output.WriteAsync(bytes);
            remaining -= count;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
