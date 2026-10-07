using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnapLine.Services;

public sealed class WindowsScreenshotClipboard : IScreenshotClipboard
{
    public async Task CopyImageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData("SnapLine.ClipboardCopyMarker", "1");
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public async Task CopyFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData("SnapLine.ClipboardCopyMarker", "1");
        package.SetStorageItems(new List<IStorageItem> { file });
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
