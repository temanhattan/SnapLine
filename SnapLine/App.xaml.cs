using Microsoft.UI.Xaml;
using SnapLine.Services;

namespace SnapLine;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = AppServices.Create();
    }

    public AppServices Services { get; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window ??= new MainWindow();
        await Services.ScreenshotStorage.InitializeAsync();
        await Services.Clothesline.StartAsync();
    }
}
