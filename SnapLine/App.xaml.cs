using Microsoft.UI.Xaml;
using SnapLine.Services;
using WinRT.Interop;

namespace SnapLine;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = AppServices.Create(() => MainWindowHandle);
    }

    public AppServices Services { get; }
    public nint MainWindowHandle => _window is null ? nint.Zero : WindowNative.GetWindowHandle(_window);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window ??= new MainWindow();
        await Services.ScreenshotStorage.InitializeAsync();
        await Services.Clothesline.StartAsync();
    }
}
