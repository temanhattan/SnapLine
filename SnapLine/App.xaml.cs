using Microsoft.UI.Xaml;
using SnapLine.Services;
using WinRT.Interop;

namespace SnapLine;

public partial class App : Application
{
    private const string MutexName = "Local\\SnapLine.SingleInstance";
    private const string ActivateEventName = "Local\\SnapLine.Activate";
    private MainWindow? _window;
    private GlobalHotkeyService? _globalHotkey;
    private ITrayService? _tray;
    private RegisteredWaitHandle? _activationRegistration;
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _activateEvent;
    private bool _isPrimaryInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => AppLogger.Write("XAML", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Write("AppDomain", args.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Write("Task", args.Exception);
            args.SetObserved();
        };
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
            AppLogger.Write("Instance", "decision=second-instance signal-first-instance");
            try { _activateEvent.Set(); }
            catch (ObjectDisposedException) { }
            Exit();
            return;
        }

        _window ??= new MainWindow();
        _window.Activate();
        _window.HideForBackground();
        _tray = new WindowsTrayService(MainWindowHandle);
        _tray.ShowRequested += (_, _) => DispatchTray("show", () => _window?.ShowPreview());
        _tray.HideRequested += (_, _) => DispatchTray("hide", () => _window?.HideForBackground());
        _tray.SettingsRequested += (_, _) => DispatchTrayAsync("settings", async () => await _window!.ShowSettingsAsync());
        _tray.ClearRequested += (_, _) => DispatchTrayAsync("clear", async () => await _window!.TakeEverythingDownAsync());
        _tray.OpenScreenshotsFolderRequested += (_, _) => DispatchTray("open-screenshots", () =>
        {
            var folder = Services.ScreenshotStorage.TemporaryRootPath;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        });
        _tray.OpenLogFolderRequested += (_, _) => DispatchTray("open-logs", () =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapLine", "logs");
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        });
        _tray.ExitRequested += (_, _) => _window?.DispatcherQueue.TryEnqueue(async () =>
        {
            AppLogger.Write("Quit", "step=dispatcher-start");
            _window?.HideForBackground();
            _globalHotkey?.Dispose();
            _window?.Shutdown();
            using var quitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Services.Clothesline.StopAsync(quitCts.Token); }
            catch (Exception exception) { AppLogger.Write("Quit", exception); }
            _tray?.Dispose();
            AppLogger.Write("Quit", "step=tray-disposed");
            Exit();
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                Environment.Exit(0);
            });
        });
        _tray.Start();
        _globalHotkey = new GlobalHotkeyService();
        _globalHotkey.Pressed += (_, _) => _window?.TogglePreview();
        if (!_globalHotkey.RegisterShowClothesline(MainWindowHandle))
            _window.ShowShortcutUnavailable();
        StartActivationListener();
        await Services.ScreenshotStorage.InitializeAsync();
        await Services.Clothesline.StartAsync();
        if (!AppPreferences.SetupCompleted)
            await _window.ShowSetupOfferAsync();
    }

    private void DispatchTray(string action, Action callback)
    {
        AppLogger.Write("Tray", $"stage=dispatch-requested action={action}");
        if (!(_window?.DispatcherQueue.TryEnqueue(() =>
        {
            try { callback(); AppLogger.Write("Tray", $"stage=action-completed action={action}"); }
            catch (Exception exception) { AppLogger.Write("Tray", $"stage=action-failed action={action}"); AppLogger.Write("Tray", exception); }
        }) ?? false))
            AppLogger.Write("Tray", $"stage=dispatch-failed action={action}");
    }

    private void DispatchTrayAsync(string action, Func<Task> callback)
    {
        AppLogger.Write("Tray", $"stage=dispatch-requested action={action}");
        if (!(_window?.DispatcherQueue.TryEnqueue(async () =>
        {
            try { await callback(); AppLogger.Write("Tray", $"stage=action-completed action={action}"); }
            catch (Exception exception) { AppLogger.Write("Tray", $"stage=action-failed action={action}"); AppLogger.Write("Tray", exception); }
        }) ?? false))
            AppLogger.Write("Tray", $"stage=dispatch-failed action={action}");
    }

    private void StartActivationListener()
    {
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, timedOut) =>
            {
                if (timedOut) return;
                _window?.DispatcherQueue.TryEnqueue(() => _window?.ShowPreview());
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

}
