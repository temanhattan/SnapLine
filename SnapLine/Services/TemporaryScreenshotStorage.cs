using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using SnapLine.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnapLine.Services;

/// <summary>References screenshot files in place and keeps only UI thumbnails in SnapLine's cache.</summary>
public sealed class TemporaryScreenshotStorage : IScreenshotStorage
{
    private const uint ThumbnailMaxWidth = 480;
    private const uint ThumbnailMaxHeight = 320;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _inboxPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapLine", "Screenshots");
    private readonly string _thumbnailRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapLine", "Thumbnails");

    // Retained for the detector interface: clipboard-only captures land in the app-owned Inbox.
    public string TemporaryRootPath => _inboxPath;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_inboxPath);
        Directory.CreateDirectory(_thumbnailRoot);
        return Task.CompletedTask;
    }

    public async Task<ScreenshotItem> StoreAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The screenshot could not be found.", fullPath);
        var id = Guid.NewGuid();
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())));
        var thumbnailPath = Path.Combine(_thumbnailRoot, key + ".jpg");
        await CreateThumbnailAsync(fullPath, thumbnailPath, cancellationToken);
        return new ScreenshotItem(id, fullPath, thumbnailPath, File.GetCreationTimeUtc(fullPath));
    }

    public async Task<string> SaveAsync(ScreenshotItem item, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(item.OriginalImagePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return source;
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("A destination folder is required.", nameof(destinationPath));
        Directory.CreateDirectory(parent);
        await CopyFileAsync(source, destination, cancellationToken, overwrite: false);
        return destination;
    }

    public async Task ReplaceImageAsync(ScreenshotItem item, byte[] pixels, uint width, uint height, CancellationToken cancellationToken = default)
    {
        if (width == 0 || height == 0 || pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("The edited image pixels are invalid.", nameof(pixels));
        await _gate.WaitAsync(cancellationToken);
        var source = Path.GetFullPath(item.OriginalImagePath);
        var folder = Path.GetDirectoryName(source)!;
        var extension = Path.GetExtension(source);
        var staged = Path.Combine(folder, $".snapline-{Guid.NewGuid():N}{extension}");
        try
        {
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var stream = file.AsRandomAccessStream())
            {
                var encoderId = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
                var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, source, overwrite: true);
            var freshThumbnail = Path.Combine(_thumbnailRoot, Guid.NewGuid().ToString("N") + ".jpg");
            await CreateThumbnailAsync(source, freshThumbnail, cancellationToken);
            item.UpdateThumbnailPath(freshThumbnail);
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch (IOException) { }
            _gate.Release();
        }
    }

    public Task DeleteAsync(ScreenshotItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(item.OriginalImagePath))
            FileSystem.DeleteFile(item.OriginalImagePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        item.CurrentState = ScreenshotState.Deleted;
        return Task.CompletedTask;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken, bool overwrite)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken);
    }

    private static async Task CreateThumbnailAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = await StorageFile.GetFileFromPathAsync(sourcePath);
        using var stream = await source.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var sourceWidth = decoder.OrientedPixelWidth;
        var sourceHeight = decoder.OrientedPixelHeight;
        if (sourceWidth == 0 || sourceHeight == 0) throw new InvalidDataException("The image has invalid dimensions.");
        var scale = Math.Min(1d, Math.Min((double)ThumbnailMaxWidth / sourceWidth, (double)ThumbnailMaxHeight / sourceHeight));
        var width = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, width, height, decoder.DpiX, decoder.DpiY, data.DetachPixelData());
        await encoder.FlushAsync();
        output.Seek(0);
        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var buffer = new byte[64 * 1024];
        ulong remaining = output.Size;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (uint)Math.Min((ulong)buffer.Length, remaining);
            await reader.LoadAsync(count);
            var chunk = count == buffer.Length ? buffer : new byte[count];
            reader.ReadBytes(chunk);
            await file.WriteAsync(chunk, cancellationToken);
            remaining -= count;
        }
    }
}
