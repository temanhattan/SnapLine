using Microsoft.UI.Xaml;
using SnapLine.Services;
using WinRT.Interop;

namespace SnapLine;

public partial class App : Application
{
    private const string MutexName = "Local\\SnapLine.SingleInstance";
    private const string ActivateEventName = "Local\\SnapLine.Activate";
    private MainWindow? _window;
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _activateEvent;
    private bool _isPrimaryInstance;
    private CancellationTokenSource? _activationListenerCancellation;

    public App()
    {
        InitializeComponent();
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimaryInstance);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out _);
        Services = AppServices.Create(() => MainWindowHandle);
    }

    public AppServices Services { get; }
    public nint MainWindowHandle => _window is null ? nint.Zero : WindowNative.GetWindowHandle(_window);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_isPrimaryInstance)
        {
            try { _activateEvent.Set(); }
            catch (ObjectDisposedException) { }
            Exit();
            return;
        }

        _window ??= new MainWindow();
        StartActivationListener();
        _window.Activate();
        _window.ShowPreviewAfterContentLoads();
        await Services.ScreenshotStorage.InitializeAsync();
        await Services.Clothesline.StartAsync();
    }

    private void StartActivationListener()
    {
        _activationListenerCancellation = new CancellationTokenSource();
        var token = _activationListenerCancellation.Token;
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_activateEvent.WaitOne(500)) continue;
                    if (token.IsCancellationRequested) break;
                    _window?.DispatcherQueue.TryEnqueue(() => _window?.ShowPreview());
                }
                catch (ObjectDisposedException) { break; }
            }
        }, token);
    }
}
