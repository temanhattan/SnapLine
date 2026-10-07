using SnapLine.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnapLine.Services;

/// <summary>
/// Keeps originals and UI-sized JPEG thumbnails in a per-run directory under LocalAppData.
/// Startup removes abandoned sessions while leaving active sessions owned by other instances alone.
/// </summary>
public sealed class TemporaryScreenshotStorage : IScreenshotStorage
{
    private const uint ThumbnailMaxWidth = 480;
    private const uint ThumbnailMaxHeight = 320;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnapLine", "Temporary", Guid.NewGuid().ToString("N"));
    private FileStream? _sessionLease;
    private bool _initialized;

    public string TemporaryRootPath => _sessionDirectory;
    private string StorageRootPath => Path.GetDirectoryName(_sessionDirectory)!;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            Directory.CreateDirectory(StorageRootPath);
            using var cleanupMutex = new Mutex(initiallyOwned: false, "Local\\SnapLine.TemporaryStorageCleanup");
            try { cleanupMutex.WaitOne(); }
            catch (AbandonedMutexException) { }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(StorageRootPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(entry) && !IsAbandonedSession(entry))
                        continue;
                    TryDeleteEntry(entry);
                }

                Directory.CreateDirectory(_sessionDirectory);
                _sessionLease = new FileStream(Path.Combine(_sessionDirectory, ".session.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                _initialized = true;
            }
            finally
            {
                cleanupMutex.ReleaseMutex();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsAbandonedSession(string directory)
    {
        try
        {
            using var lease = new FileStream(Path.Combine(directory, ".session.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<ScreenshotItem> StoreAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
            throw new FileNotFoundException("The detected screenshot could not be found.", fullSourcePath);

        var id = Guid.NewGuid();
        var itemDirectory = Path.Combine(_sessionDirectory, id.ToString("N"));
        Directory.CreateDirectory(itemDirectory);
        var extension = Path.GetExtension(fullSourcePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".img";
        var originalPath = Path.Combine(itemDirectory, $"original{extension}");
        var thumbnailPath = Path.Combine(itemDirectory, "thumbnail.jpg");

        try
        {
            await CopyFileAsync(fullSourcePath, originalPath, cancellationToken);
            await CreateThumbnailAsync(originalPath, thumbnailPath, cancellationToken);
            return new ScreenshotItem(id, originalPath, thumbnailPath, DateTimeOffset.UtcNow);
        }
        catch
        {
            TryDeleteEntry(itemDirectory);
            throw;
        }
    }

    public async Task<string> SaveAsync(ScreenshotItem item, string destinationPath, CancellationToken cancellationToken = default)
    {
        EnsureOwnedItem(item);
        cancellationToken.ThrowIfCancellationRequested();
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("A destination directory is required.", nameof(destinationPath));
        if (string.Equals(Path.GetFullPath(item.FilePath), fullDestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The save destination must be outside temporary storage.");
        if (IsWithinDirectory(fullDestinationPath, StorageRootPath))
            throw new InvalidOperationException("Choose a destination outside SnapLine temporary storage.");

        Directory.CreateDirectory(destinationDirectory);
        var stagingPath = Path.Combine(destinationDirectory, $".snapline-{Guid.NewGuid():N}.tmp");
        var expectedLength = new FileInfo(item.FilePath).Length;
        try
        {
            await CopyFileAsync(item.FilePath, stagingPath, cancellationToken);
            if (!File.Exists(stagingPath) || new FileInfo(stagingPath).Length != expectedLength)
                throw new IOException("The saved image did not pass verification. The screenshot is still available in SnapLine.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, fullDestinationPath, overwrite: true);
            if (!File.Exists(fullDestinationPath) || new FileInfo(fullDestinationPath).Length != expectedLength)
                throw new IOException("The saved image could not be confirmed. The screenshot is still available in SnapLine.");
        }
        catch
        {
            TryDeleteEntry(stagingPath);
            throw;
        }

        item.CurrentState = ScreenshotState.Saved;
        try
        {
            // The permanent copy is verified; temporary cleanup must not turn a
            // successful save into a failed UI action. Any leftover is session-cleaned.
            await DeleteAsync(item, CancellationToken.None);
            item.CurrentState = ScreenshotState.Saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fullDestinationPath;
    }

    public async Task ReplaceImageAsync(ScreenshotItem item, byte[] pixels, uint width, uint height, CancellationToken cancellationToken = default)
    {
        EnsureOwnedItem(item);
        if (width == 0 || height == 0 || pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("The edited image pixel data is invalid.", nameof(pixels));

        await _gate.WaitAsync(cancellationToken);
        var directory = Path.GetDirectoryName(item.FilePath)!;
        var extension = Path.GetExtension(item.FilePath);
        var stagedOriginal = Path.Combine(directory, $"edited-{Guid.NewGuid():N}{extension}");
        var stagedThumbnail = Path.Combine(directory, $"thumbnail-{Guid.NewGuid():N}.jpg");
        var backupOriginal = Path.Combine(directory, $"original-backup-{Guid.NewGuid():N}{extension}");
        var oldThumbnail = item.ThumbnailPath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(stagedOriginal, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var randomAccess = stream.AsRandomAccessStream())
            {
                var encoderId = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? BitmapEncoder.JpegEncoderId
                    : BitmapEncoder.PngEncoderId;
                var encoder = await BitmapEncoder.CreateAsync(encoderId, randomAccess);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
                await encoder.FlushAsync();
            }

            await CreateThumbnailAsync(stagedOriginal, stagedThumbnail, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(item.FilePath, backupOriginal);
            File.Move(stagedOriginal, item.FilePath);
            var newThumbnail = Path.Combine(directory, $"thumbnail-{Guid.NewGuid():N}.jpg");
            File.Move(stagedThumbnail, newThumbnail);
            item.UpdateThumbnailPath(newThumbnail);
            item.CurrentState = ScreenshotState.Active;
            TryDeleteEntry(backupOriginal);
            TryDeleteEntry(oldThumbnail);
        }
        catch
        {
            if (File.Exists(backupOriginal))
            {
                TryDeleteEntry(item.FilePath);
                File.Move(backupOriginal, item.FilePath);
            }
            TryDeleteEntry(stagedOriginal);
            TryDeleteEntry(stagedThumbnail);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        EnsureOwnedItem(item);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemDirectory = Path.GetDirectoryName(item.FilePath)!;
            if (Directory.Exists(itemDirectory)) Directory.Delete(itemDirectory, recursive: true);
            item.CurrentState = ScreenshotState.Deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureOwnedItem(ScreenshotItem item)
    {
        var itemPath = Path.GetFullPath(item.FilePath);
        var root = Path.GetFullPath(_sessionDirectory) + Path.DirectorySeparatorChar;
        if (!itemPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The screenshot does not belong to this temporary storage session.");
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken, bool overwrite = false)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken);
    }

    private static async Task CreateThumbnailAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
        using var sourceStream = await sourceFile.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);

        var sourceWidth = decoder.OrientedPixelWidth;
        var sourceHeight = decoder.OrientedPixelHeight;
        if (sourceWidth == 0 || sourceHeight == 0)
            throw new InvalidDataException("The screenshot has invalid dimensions.");

        var scale = Math.Min(1d, Math.Min((double)ThumbnailMaxWidth / sourceWidth, (double)ThumbnailMaxHeight / sourceHeight));
        var width = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        cancellationToken.ThrowIfCancellationRequested();
        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, width, height,
            decoder.DpiX, decoder.DpiY, pixelData.DetachPixelData());
        await encoder.FlushAsync();
        outputStream.Seek(0);

        await using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new DataReader(outputStream.GetInputStreamAt(0));
        var buffer = new byte[64 * 1024];
        ulong remaining = outputStream.Size;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (uint)Math.Min((ulong)buffer.Length, remaining);
            await reader.LoadAsync(count);
            var chunk = new byte[(int)count];
            reader.ReadBytes(chunk);
            await fileStream.WriteAsync(chunk, cancellationToken);
            remaining -= count;
        }
    }

    private static void TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* Retried on the next startup. */ }
        catch (UnauthorizedAccessException) { /* Retried on the next startup. */ }
    }
}
