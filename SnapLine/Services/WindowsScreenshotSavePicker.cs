using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SnapLine.Services;

public sealed class WindowsScreenshotSavePicker : IScreenshotSavePicker
{
    public async Task<string?> PickSavePathAsync(
        string suggestedName,
        string extension,
        nint ownerWindow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add("Image", new List<string> { extension });
        InitializeWithWindow.Initialize(picker, ownerWindow);
        var file = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file?.Path;
    }
}
