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
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        package.SetStorageItems(new List<IStorageItem> { file });
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
