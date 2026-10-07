using SnapLine.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnapLine.Services;

/// <summary>
/// Keeps originals and UI-sized JPEG thumbnails in a per-run directory under LocalAppData.
/// A new run clears prior run directories, including leftovers from a crash.
/// </summary>
public sealed class TemporaryScreenshotStorage : IScreenshotStorage
{
    private const uint ThumbnailMaxWidth = 480;
    private const uint ThumbnailMaxHeight = 320;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnapLine", "Temporary", Guid.NewGuid().ToString("N"));
    private bool _initialized;

    public string TemporaryRootPath => Path.GetDirectoryName(_sessionDirectory)!;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            // Every item is temporary until explicitly saved. Anything left from an earlier
            // process is therefore safe to remove, including files left by a crash.
            if (Directory.Exists(TemporaryRootPath))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(TemporaryRootPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryDeleteEntry(entry);
                }
            }

            Directory.CreateDirectory(_sessionDirectory);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
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
        Directory.CreateDirectory(destinationDirectory);
        if (string.Equals(Path.GetFullPath(item.FilePath), fullDestinationPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The save destination must be outside temporary storage.");
        await CopyFileAsync(item.FilePath, fullDestinationPath, cancellationToken, overwrite: true);
        await DeleteAsync(item, cancellationToken);
        item.CurrentState = ScreenshotState.Saved;
        return fullDestinationPath;
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
