using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnapLine.Models;
using SnapLine.Services;
using WinRT.Interop;
using System.Numerics;

namespace SnapLine;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IClotheslineService _clothesline;
    private readonly DispatcherQueueTimer _shortcutTimer;
    private Storyboard? _visibilityStoryboard;
    private TaskCompletionSource? _visibilityAnimationCompletion;
    private bool _isVisible;
    private bool _manualEmptyPreview;
    private bool _shortcutWasDown;

    public MainWindow()
    {
        _clothesline = ((App)Application.Current).Services.Clothesline;
        InitializeComponent();
        ((App)Application.Current).Services.UserActions.UndoStateChanged += OnUndoStateChanged;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _shortcutTimer = DispatcherQueue.CreateTimer();
        _shortcutTimer.Interval = TimeSpan.FromMilliseconds(50);
        _shortcutTimer.Tick += CheckShowShortcut;
        _shortcutTimer.Start();
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _appWindow.Closing += OnClosing;

        _clothesline.Items.CollectionChanged += (_, _) => OnItemsChanged();
        ExtendFrameIntoClientArea(WindowNative.GetWindowHandle(this));
    }

    public IClotheslineService Clothesline => _clothesline;
    public bool IsShowShortcutRegistered => true;

    public void ShowPreview()
    {
        var hasPendingUndo = ((App)Application.Current).Services.UserActions.UndoableItem is not null;
        UndoToast.Visibility = hasPendingUndo ? Visibility.Visible : Visibility.Collapsed;
        if (_clothesline.Items.Count == 0 && !hasPendingUndo)
        {
            _manualEmptyPreview = true;
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            _manualEmptyPreview = _clothesline.Items.Count == 0;
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        _ = RevealAsync();
    }

    public void ShowShortcutUnavailable()
    {
        _manualEmptyPreview = true;
        EmptyHint.Text = "Ctrl + Alt + S is unavailable. Close other SnapLine copies and restart.";
        EmptyHint.Visibility = Visibility.Visible;
        _ = RevealAsync();
    }

    public void ShowPreviewAfterContentLoads()
    {
        if (Content is not FrameworkElement content) return;
        if (content.IsLoaded)
        {
            DispatcherQueue.TryEnqueue(ShowPreview);
            return;
        }
        content.Loaded += (_, _) => DispatcherQueue.TryEnqueue(ShowPreview);
    }

    public void HideForBackground()
    {
        _manualEmptyPreview = false;
        _appWindow.Hide();
        _isVisible = false;
    }

    private async void OnScreenshotTapped(object sender, TappedRoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is FrameworkElement { DataContext: ScreenshotItem item })
            await ((App)Application.Current).Services.UserActions.ViewAsync(item);
    }

    private async void OnScreenshotDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element || !File.Exists(item.FilePath))
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        element.Shadow = new ThemeShadow();
        element.Translation = new Vector3(0, -5, 14);
        try
        {
            await AnimateItemAsync(element, translateY: -9, scale: 1.06, opacity: 0.94, durationMs: 95);
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            args.Data.SetStorageItems(new List<IStorageItem> { file });
            args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
            args.DragUI.SetContentFromBitmapImage(item.ThumbnailImage);
        }
        catch (Exception)
        {
            args.Cancel = true;
            await RestoreDragVisualAsync(element);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnScreenshotDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element)
            return;

        if (args.DropResult == DataPackageOperation.None)
        {
            await RestoreDragVisualAsync(element);
            return;
        }

        try
        {
            await AnimateItemAsync(element, translateY: 18, scale: 0.86, opacity: 0, durationMs: 115);
            await ((App)Application.Current).Services.UserActions.CompleteAsync(item);
        }
        catch (Exception exception)
        {
            await RestoreDragVisualAsync(element);
            await ShowActionErrorAsync(exception);
        }
        finally
        {
            element.Shadow = null;
            element.Translation = Vector3.Zero;
        }
    }

    private static async Task RestoreDragVisualAsync(FrameworkElement element)
    {
        await AnimateItemAsync(element, translateY: 0, scale: 1, opacity: 1, durationMs: 115);
        element.Shadow = null;
        element.Translation = Vector3.Zero;
    }

    private static Task AnimateItemAsync(FrameworkElement element, double translateY, double scale, double opacity, int durationMs)
    {
        if (element.RenderTransform is not CompositeTransform transform)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storyboard = new Storyboard();
        AddAnimation(storyboard, transform, "TranslateY", transform.TranslateY, translateY, durationMs);
        AddAnimation(storyboard, transform, "ScaleX", transform.ScaleX, scale, durationMs);
        AddAnimation(storyboard, transform, "ScaleY", transform.ScaleY, scale, durationMs);
        AddAnimation(storyboard, element, "Opacity", element.Opacity, opacity, durationMs);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private static void AddAnimation(Storyboard storyboard, DependencyObject target, string property,
        double from, double to, int durationMs)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private async Task ShowActionErrorAsync(Exception exception)
    {
        var dialog = new ContentDialog
        {
            Title = "Couldn't complete that action",
            Content = exception.Message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void OnUndoClicked(object sender, RoutedEventArgs args)
    {
        await RunActionAsync(() => ((App)Application.Current).Services.UserActions.UndoDeleteAsync());
    }

    private void OnUndoStateChanged(object? sender, EventArgs args)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnUndoStateChanged(sender, args));
            return;
        }

        var actions = ((App)Application.Current).Services.UserActions;
        if (actions.UndoableItem is not null)
        {
            UndoMessage.Text = actions.PendingDeletionCount > 1
                ? $"{actions.PendingDeletionCount} screenshots removed"
                : "Screenshot removed";
            UndoToast.Visibility = Visibility.Visible;
            if (_clothesline.Items.Count == 0)
            {
                _manualEmptyPreview = true;
                EmptyHint.Visibility = Visibility.Collapsed;
                _ = RevealAsync();
            }
            return;
        }

        UndoToast.Visibility = Visibility.Collapsed;
        if (_clothesline.Items.Count == 0)
        {
            _manualEmptyPreview = false;
            _ = FadeOutAndHideAsync();
        }
    }

    private void OnScreenshotContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element) return;

        var actions = ((App)Application.Current).Services.UserActions;
        var menu = new MenuFlyout();
        AddAction(menu, "Open", () => actions.ViewAsync(item));
        AddAction(menu, "Edit", () => actions.EditAsync(item));
        AddAction(menu, "Copy Image", () => actions.CopyImageAsync(item));
        AddAction(menu, "Copy File", () => actions.CopyFileAsync(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddAction(menu, "Save…", async () => { await actions.SaveAsync(item); });
        AddAction(menu, "Save As…", async () => { await actions.SaveAsAsync(item); });
        AddAction(menu, "Open With…", () => actions.OpenWithAsync(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddAction(menu, "Delete", () => actions.DeleteAsync(item));
        menu.ShowAt(element);
        args.Handled = true;
    }

    private void AddAction(MenuFlyout menu, string title, Func<Task> action)
    {
        var menuItem = new MenuFlyoutItem { Text = title };
        menuItem.Click += async (_, _) => await RunActionAsync(action);
        menu.Items.Add(menuItem);
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = "Couldn't complete that action",
                Content = exception.Message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private async void OnItemsChanged()
    {
        var count = _clothesline.Items.Count;
        if (count == 0)
        {
            if (((App)Application.Current).Services.UserActions.UndoableItem is not null)
            {
                _manualEmptyPreview = true;
                EmptyHint.Visibility = Visibility.Collapsed;
                await RevealAsync();
                return;
            }
            if (_manualEmptyPreview)
            {
                EmptyHint.Visibility = Visibility.Visible;
                await RevealAsync();
            }
            else
                await FadeOutAndHideAsync();
            return;
        }

        _manualEmptyPreview = false;
        EmptyHint.Visibility = Visibility.Collapsed;
        await RevealAsync();
    }

    private void CheckShowShortcut(DispatcherQueueTimer sender, object args)
    {
        var shortcutDown = IsKeyDown(VirtualKeyControl) && IsKeyDown(VirtualKeyMenu) && IsKeyDown(VirtualKeyS);
        if (shortcutDown && !_shortcutWasDown)
            ShowPreview();
        _shortcutWasDown = shortcutDown;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private async Task RevealAsync()
    {
        UpdateBounds();
        if (!_isVisible)
        {
            ClotheslineRoot.Opacity = 0;
            _appWindow.Show();
            _isVisible = true;
        }
        await AnimateOpacityAsync(1);
    }

    private async Task FadeOutAndHideAsync()
    {
        if (!_isVisible) return;
        await AnimateOpacityAsync(0);
        if (_clothesline.Items.Count == 0 && !_manualEmptyPreview)
        {
            _appWindow.Hide();
            _isVisible = false;
            ClotheslineRoot.Opacity = 1;
        }
    }

    private void UpdateBounds()
    {
        var count = _clothesline.Items.Count;
        if (count == 0 && !_manualEmptyPreview && UndoToast.Visibility != Visibility.Visible) return;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var bounds = display.OuterBounds;
        const int height = 218;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, height));
    }

    private Task AnimateOpacityAsync(double targetOpacity)
    {
        _visibilityStoryboard?.Stop();
        _visibilityAnimationCompletion?.TrySetResult();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = ClotheslineRoot.Opacity,
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(135)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, ClotheslineRoot);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_visibilityStoryboard, storyboard))
            {
                _visibilityStoryboard = null;
                _visibilityAnimationCompletion = null;
            }
            completion.TrySetResult();
        };

        _visibilityStoryboard = storyboard;
        _visibilityAnimationCompletion = completion;
        storyboard.Begin();
        return completion.Task;
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
        _isVisible = false;
        _manualEmptyPreview = ((App)Application.Current).Services.UserActions.UndoableItem is not null;
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private static void ExtendFrameIntoClientArea(nint hwnd)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyS = 0x53;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

}
