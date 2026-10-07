using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace SnapLine.Services;

/// <summary>
/// Detects image captures published to the Windows clipboard, including Win + Shift + S.
/// Other capture providers can implement IScreenshotDetector and feed the same clothesline pipeline.
/// </summary>
public sealed class ClipboardScreenshotDetector(IScreenshotStorage storage, Func<bool> isEnabled) : IScreenshotDetector
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private bool _started;
    private uint _lastClipboardSequence;
    private string? _lastImageHash;
    private DateTimeOffset _lastImageAt;
    private static readonly HashSet<string> CaptureOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenclippinghost", "snippingtool", "screensketch", "explorer"
    };
    private static readonly HashSet<string> ClipboardHistoryOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "textinputhost", "shellexperiencehost", "searchhost", "startmenuexperiencehost"
    };

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
            var ownerName = GetClipboardOwnerName();
            var content = Clipboard.GetContent();
            var hasImage = content.Contains(StandardDataFormats.Bitmap);
            AppLogger.Write("Clipboard update",
                $"event=WM_CLIPBOARDUPDATE sequence={sequence} owner={ownerName ?? "<unknown>"} formats={GetFormats(content)} hasImage={hasImage}");
            if (sequence == _lastClipboardSequence)
            {
                AppLogger.Write("Clipboard update", "decision=ignored-duplicate-sequence");
                return;
            }
            if (!isEnabled())
            {
                AppLogger.Write("Clipboard update", "decision=ignored-setting-disabled");
                _lastClipboardSequence = sequence;
                return;
            }
            if (ClipboardWriteSuppression.Consumes(sequence))
            {
                AppLogger.Write("Clipboard update", "decision=ignored-own-write-sequence");
                _lastClipboardSequence = sequence;
                return;
            }
            if (string.Equals(ownerName, Environment.ProcessId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ownerName, "SnapLine", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Write("Clipboard update", "decision=ignored-own-owner");
                _lastClipboardSequence = sequence;
                return;
            }
            if (!hasImage)
            {
                AppLogger.Write("Clipboard update", "decision=ignored-no-image");
                _lastClipboardSequence = sequence;
                return;
            }
            var normalizedOwner = Path.GetFileNameWithoutExtension(ownerName ?? string.Empty);
            if (ClipboardHistoryOwners.Contains(normalizedOwner) || !CaptureOwners.Contains(normalizedOwner))
            {
                AppLogger.Write("Clipboard update", $"decision=ignored-owner-filter owner={ownerName ?? "<unknown>"}");
                _lastClipboardSequence = sequence;
                return;
            }
            if (content.Contains("SnapLine.ClipboardCopyMarker"))
            {
                _lastClipboardSequence = sequence;
                AppLogger.Write("Clipboard update", "decision=ignored-own-marker");
                return;
            }

            var bitmapReference = await content.GetBitmapAsync();
            using var bitmapStream = await bitmapReference.OpenReadAsync();
            stagingPath = Path.Combine(storage.TemporaryRootPath, $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid():N}.png");
            Directory.CreateDirectory(storage.TemporaryRootPath);
            var imageHash = await WriteStreamAndHashAsync(bitmapStream, stagingPath);
            var detectedAt = DateTimeOffset.UtcNow;
            if (ClipboardCaptureMemory.Contains(imageHash, detectedAt))
            {
                _lastClipboardSequence = sequence;
                AppLogger.Write("Clipboard update", "decision=ignored-remembered-line-hash");
                File.Delete(stagingPath);
                return;
            }
            if (string.Equals(imageHash, _lastImageHash, StringComparison.Ordinal) &&
                detectedAt - _lastImageAt < TimeSpan.FromSeconds(2))
            {
                _lastClipboardSequence = sequence;
                File.Delete(stagingPath);
                return;
            }

            _lastClipboardSequence = sequence;
            _lastImageHash = imageHash;
            _lastImageAt = detectedAt;
            AppLogger.Write("Clipboard update", "decision=accepted-capture");
            var handler = ScreenshotDetected;
            if (handler is not null)
            {
                var detected = new ScreenshotDetectedEventArgs(stagingPath, DateTimeOffset.UtcNow, "clipboard");
                foreach (ScreenshotDetectedHandler subscriber in handler.GetInvocationList())
                    await subscriber(this, detected);
            }
        }
        catch (Exception exception)
        {
            // Clipboard ownership can change while Windows is providing a delayed bitmap.
            // Ignore that update; the next clipboard event remains eligible for processing.
            AppLogger.Write("Clipboard capture", exception);
            if (stagingPath is not null)
            {
                try { File.Delete(stagingPath); } catch (IOException) { }
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private static async Task<string> WriteStreamAndHashAsync(IRandomAccessStream stream, string destinationPath)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        ulong remaining = stream.Size;
        try
        {
            while (remaining > 0)
            {
                var count = (uint)Math.Min((ulong)buffer.Length, remaining);
                await reader.LoadAsync(count);
                if (count == buffer.Length)
                {
                    reader.ReadBytes(buffer);
                    hash.AppendData(buffer);
                    await output.WriteAsync(buffer);
                }
                else
                {
                    var finalChunk = new byte[(int)count];
                    reader.ReadBytes(finalChunk);
                    hash.AppendData(finalChunk);
                    await output.WriteAsync(finalChunk);
                }
                remaining -= count;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private static string? GetClipboardOwnerName()
    {
        var owner = GetClipboardOwner();
        if (owner == nint.Zero) return null;
        GetWindowThreadProcessId(owner, out var processId);
        if (processId == 0) return null;
        if (processId == Environment.ProcessId) return "SnapLine";
        try { return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName; }
        catch (ArgumentException) { return null; }
    }

    private static string GetFormats(DataPackageView content)
    {
        var formats = new List<string>();
        foreach (var format in new[] { StandardDataFormats.Bitmap, StandardDataFormats.Text, StandardDataFormats.Html,
                                       StandardDataFormats.StorageItems })
            if (content.Contains(format)) formats.Add(format);
        return string.Join(",", formats);
    }
}
