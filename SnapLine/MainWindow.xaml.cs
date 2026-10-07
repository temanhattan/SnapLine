using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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
    private Storyboard? _visibilityStoryboard;
    private TaskCompletionSource? _visibilityAnimationCompletion;
    private bool _isVisible;
    private bool _isRevealed;
    private bool _manualReveal;
    private bool _autoRevealSuppressed;
    private bool _fullscreenSuppressed;
    private DateTimeOffset _peekUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _hotZoneSince;
    private DateTimeOffset? _awaySince;
    private readonly DispatcherQueueTimer _pointerTimer;
    private readonly nint _windowHandle;
    private readonly WinEventDelegate _winEventCallback;
    private nint _foregroundHook;
    private bool _manualEmptyPreview;
    private readonly Dictionary<Guid, GestureState> _gestures = [];
    private readonly Dictionary<Guid, PendingClick> _pendingClicks = [];
    private const double HoldMilliseconds = 450;
    private const double DragThreshold = 4;
    private Guid? _lastClickItemId;
    private DateTimeOffset _lastClickAt;
    private Windows.Foundation.Point _lastClickPosition;

    public MainWindow()
    {
        _clothesline = ((App)Application.Current).Services.Clothesline;
        InitializeComponent();
        EmptyHint.Text = Localization.Get("EmptyHint");
        ((App)Application.Current).Services.UserActions.UndoStateChanged += OnUndoStateChanged;
        // The line must not obscure the desktop between cards.
        SystemBackdrop = null;

        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureNonActivatingOverlay(_windowHandle);
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
        ExtendFrameIntoClientArea(_windowHandle);

        _pointerTimer = DispatcherQueue.CreateTimer();
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(33);
        _pointerTimer.Tick += (_, _) => UpdatePointerVisibility();
        _pointerTimer.Start();
        _winEventCallback = OnForegroundWindowChanged;
        _foregroundHook = SetWinEventHook(0x0003, 0x0003, nint.Zero, _winEventCallback, 0, 0, 0x0000 | 0x0002);
    }

    public IClotheslineService Clothesline => _clothesline;
    public void ShowPreview()
    {
        _manualReveal = true;
        _autoRevealSuppressed = false;
        _peekUntil = DateTimeOffset.MaxValue;
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

    public void TogglePreview()
    {
        if (_isRevealed)
        {
            _manualReveal = false;
            _autoRevealSuppressed = true;
            _peekUntil = DateTimeOffset.MinValue;
            _manualEmptyPreview = false;
            _ = HideLineAsync();
        }
        else
        {
            ShowPreview();
        }
    }

    public async Task TakeEverythingDownAsync()
    {
        foreach (var item in _clothesline.Items.ToArray())
            await _clothesline.RemoveFromLineAsync(item);
    }

    public void ShowShortcutUnavailable()
    {
        _manualReveal = true;
        _peekUntil = DateTimeOffset.MaxValue;
        _manualEmptyPreview = true;
        EmptyHint.Text = Localization.Get("ShortcutUnavailable");
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
        _manualReveal = false;
        _peekUntil = DateTimeOffset.MinValue;
        _autoRevealSuppressed = true;
        _isRevealed = false;
        _appWindow.Hide();
        _isVisible = false;
    }

    public async Task ShowSettingsAsync()
    {
        var wasRevealed = _isRevealed;
        _manualReveal = true;
        _peekUntil = DateTimeOffset.MaxValue;
        await RevealAsync();
        if (!_isVisible) return;

        SetWindowActivationEnabled(true);
        _ = SetForegroundWindow(_windowHandle);
        var startup = new CheckBox
        {
            Content = Localization.Get("Startup"),
            IsChecked = WindowsStartupRegistration.IsEnabled()
        };
        var handleScreenshots = new CheckBox
        {
            Content = Localization.Get("HandleScreenshots"),
            IsChecked = AppPreferences.HandleScreenshots,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var sounds = new CheckBox
        {
            Content = Localization.Get("Sounds"),
            IsChecked = AppPreferences.SoundsEnabled,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var captureClipboard = new CheckBox
        {
            Content = Localization.Get("CaptureClipboard"),
            IsChecked = AppPreferences.CaptureClipboard,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var options = new StackPanel();
        options.Children.Add(startup);
        options.Children.Add(handleScreenshots);
        options.Children.Add(captureClipboard);
        options.Children.Add(sounds);
        var dialog = new ContentDialog
        {
            Title = Localization.Get("SettingsTitle"),
            Content = options,
            PrimaryButtonText = Localization.Get("Save"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                WindowsStartupRegistration.SetEnabled(startup.IsChecked == true);
                AppPreferences.SoundsEnabled = sounds.IsChecked == true;
                AppPreferences.CaptureClipboard = captureClipboard.IsChecked == true;
                await _clothesline.SetCaptureHandlingAsync(handleScreenshots.IsChecked == true);
            }
        }
        catch (Exception exception)
        {
            var error = new ContentDialog
            {
                Title = Localization.Get("ActionErrorTitle"),
                Content = exception.Message,
                CloseButtonText = Localization.Get("Ok"),
                XamlRoot = Content.XamlRoot
            };
            await error.ShowAsync();
        }
        finally
        {
            SetWindowActivationEnabled(false);
            if (!wasRevealed) HideForBackground();
        }
    }

    public async Task ShowSetupOfferAsync()
    {
        _manualReveal = true;
        _peekUntil = DateTimeOffset.MaxValue;
        await RevealAsync();
        SetWindowActivationEnabled(true);
        _ = SetForegroundWindow(_windowHandle);
        var dialog = new ContentDialog
        {
            Title = Localization.Get("SetupTitle"),
            Content = Localization.Get("SetupText"),
            PrimaryButtonText = Localization.Get("TurnOn"),
            CloseButtonText = Localization.Get("NotNow"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await _clothesline.SetCaptureHandlingAsync(true);
        }
        finally
        {
            AppPreferences.SetupCompleted = true;
            SetWindowActivationEnabled(false);
            HideForBackground();
        }
    }

    private void OnScreenshotPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element ||
            args.GetCurrentPoint(element).Properties.IsRightButtonPressed)
            return;

        var point = args.GetCurrentPoint(element).Position;
        element.CapturePointer(args.Pointer);
        var state = new GestureState(point);
        var elapsed = DateTimeOffset.UtcNow - _lastClickAt;
        var dpi = Math.Max(96u, GetDpiForWindow(_windowHandle));
        var doubleClickWidth = GetSystemMetrics(36) * 96d / dpi;
        var doubleClickHeight = GetSystemMetrics(37) * 96d / dpi;
        if (_lastClickItemId == item.Id && elapsed.TotalMilliseconds <= GetDoubleClickTime() &&
            Math.Abs(point.X - _lastClickPosition.X) <= doubleClickWidth &&
            Math.Abs(point.Y - _lastClickPosition.Y) <= doubleClickHeight)
        {
            state.DoubleClicked = true;
            _lastClickItemId = null;
            _gestures[item.Id] = state;
            _ = RunActionAsync(() => ((App)Application.Current).Services.UserActions.ViewAsync(item));
            return;
        }
        _lastClickItemId = null;
        _gestures[item.Id] = state;
        state.HoldTimer = DispatcherQueue.CreateTimer();
        state.HoldTimer.Interval = TimeSpan.FromMilliseconds(HoldMilliseconds);
        state.HoldTimer.IsRepeating = false;
        state.HoldTimer.Tick += async (_, _) =>
        {
            if (state.Dragged || state.Released) return;
            state.Held = true;
            CancelPendingClick(item.Id);
            await RunActionAsync(() => ((App)Application.Current).Services.UserActions.EditAsync(item));
        };
        state.HoldTimer.Start();
    }

    private void OnDeleteButtonPointerPressed(object sender, PointerRoutedEventArgs args) => args.Handled = true;
    private void OnDeleteButtonPointerReleased(object sender, PointerRoutedEventArgs args) => args.Handled = true;

    private async void OnScreenshotDeleteClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotItem item })
            await RunActionAsync(() => ((App)Application.Current).Services.UserActions.DeleteAsync(item));
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotItem item }) item.IsHovered = true;
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotItem item }) item.IsHovered = false;
    }

    private void OnScreenshotPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element ||
            !_gestures.TryGetValue(item.Id, out var state) || state.Released)
            return;

        var point = args.GetCurrentPoint(element).Position;
        var dx = point.X - state.Start.X;
        var dy = point.Y - state.Start.Y;
        if (dx * dx + dy * dy < DragThreshold * DragThreshold) return;
        state.Dragged = true;
        state.HoldTimer?.Stop();
        CancelPendingClick(item.Id);
    }

    private void OnScreenshotPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } ||
            !_gestures.Remove(item.Id, out var state)) return;

        state.Released = true;
        ((UIElement)sender).ReleasePointerCapture(args.Pointer);
        state.HoldTimer?.Stop();
        if (state.Held || state.Dragged || state.DoubleClicked) return;

        _lastClickItemId = item.Id;
        _lastClickAt = DateTimeOffset.UtcNow;
        _lastClickPosition = args.GetCurrentPoint((UIElement)sender).Position;
        _ = RunActionAsync(() => ((App)Application.Current).Services.UserActions.CopyImageAsync(item));
    }

    private void OnScreenshotPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } ||
            !_gestures.Remove(item.Id, out var state)) return;
        state.HoldTimer?.Stop();
        ((UIElement)sender).ReleasePointerCapture(args.Pointer);
    }

    private void CancelPendingClick(Guid itemId)
    {
        if (!_pendingClicks.Remove(itemId, out var timer)) return;
        timer.Timer.Stop();
        timer.Timer.Tick -= timer.Callback;
    }

    private sealed class GestureState(Windows.Foundation.Point start)
    {
        public Windows.Foundation.Point Start { get; } = start;
        public DispatcherQueueTimer? HoldTimer { get; set; }
        public bool Held { get; set; }
        public bool Dragged { get; set; }
        public bool Released { get; set; }
        public bool DoubleClicked { get; set; }
    }

    private sealed record PendingClick(DispatcherQueueTimer Timer,
        TypedEventHandler<DispatcherQueueTimer, object> Callback);

    private async void OnScreenshotDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element || !File.Exists(item.FilePath))
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        CancelPendingClick(item.Id);
        if (_gestures.TryGetValue(item.Id, out var gesture))
        {
            gesture.Dragged = true;
            gesture.HoldTimer?.Stop();
        }
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
            // Move is reported by Explorer when the user saves into a folder or
            // drops onto the Recycle Bin. Apps receive Copy and leave the item hung.
            if (args.DropResult == DataPackageOperation.Copy)
            {
                await RestoreDragVisualAsync(element);
                return;
            }
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
            Title = Localization.Get("ActionErrorTitle"),
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
        AddAction(menu, Localization.Get("Copy"), () => actions.CopyImageAsync(item));
        AddAction(menu, Localization.Get("Open"), () => actions.ViewAsync(item));
        AddAction(menu, Localization.Get("Markup"), () => actions.EditAsync(item));
        AddAction(menu, Localization.Get("ShowInExplorer"), () => actions.RevealAsync(item));
        if (actions.IsInInbox(item))
            AddAction(menu, Localization.Get("SaveToDesktop"), () => actions.SaveToDesktopAsync(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (actions.IsInInbox(item))
            AddAction(menu, Localization.Get("Discard"), () => actions.DiscardAsync(item));
        else
        {
            AddAction(menu, Localization.Get("TakeDown"), () => actions.DiscardAsync(item));
            AddAction(menu, Localization.Get("MoveToRecycleBin"), () => actions.DeleteAsync(item));
        }
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
            AppLogger.Write("User action", exception);
            var dialog = new ContentDialog
            {
                Title = Localization.Get("ActionErrorTitle"),
                Content = exception.Message,
                CloseButtonText = Localization.Get("Ok"),
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
        _manualReveal = false;
        _peekUntil = DateTimeOffset.UtcNow.AddSeconds(2.5);
        await RevealAsync();
    }

    private async Task RevealAsync()
    {
        if (IsFullscreenOnCursorDisplay())
        {
            _fullscreenSuppressed = true;
            return;
        }
        _isRevealed = true;
        _awaySince = null;
        _hotZoneSince = null;
        UpdateBounds();
        if (!_isVisible)
        {
            LineTranslation.Y = -222;
            ClotheslineRoot.Opacity = 0;
            _appWindow.Show();
            _isVisible = true;
        }
        await Task.WhenAll(AnimateOpacityAsync(1), AnimateLineAsync(true));
    }

    private async Task FadeOutAndHideAsync()
    {
        if (!_isVisible) return;
        await Task.WhenAll(AnimateOpacityAsync(0), AnimateLineAsync(false));
        if (_clothesline.Items.Count == 0 && !_manualEmptyPreview)
        {
            _appWindow.Hide();
            _isVisible = false;
            _isRevealed = false;
            ClotheslineRoot.Opacity = 1;
        }
    }

    private async Task HideLineAsync()
    {
        if (!_isVisible || !_isRevealed) return;
        _isRevealed = false;
        await Task.WhenAll(AnimateOpacityAsync(0), AnimateLineAsync(false));
        _appWindow.Hide();
        _isVisible = false;
        _isRevealed = false;
        ClotheslineRoot.Opacity = 1;
    }

    private void UpdatePointerVisibility()
    {
        if (!HasSomethingToShow()) return;
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2);
        if (monitor == nint.Zero) return;
        var monitorInfo = GetMonitorInfo(monitor);
        var now = DateTimeOffset.UtcNow;
        var topTaskbarHeight = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        var edgeBandHeight = topTaskbarHeight > 0 ? topTaskbarHeight : 3;
        var inEdgeZone = cursor.Y >= monitorInfo.Monitor.Top && cursor.Y < monitorInfo.Monitor.Top + edgeBandHeight;

        if (_autoRevealSuppressed && !inEdgeZone)
            _autoRevealSuppressed = false;

        if (IsFullscreen(monitor))
        {
            _hotZoneSince = null;
            _awaySince = null;
            if (_isVisible)
            {
                _fullscreenSuppressed = true;
                _ = HideLineAsync();
            }
            return;
        }

        if (_fullscreenSuppressed)
        {
            _fullscreenSuppressed = false;
            if (_manualReveal || now < _peekUntil)
            {
                _ = RevealAsync();
                return;
            }
        }

        if (_isVisible) EnsureCurrentMonitorGeometry();

        if (_isRevealed && inEdgeZone && MonitorFromWindow(_windowHandle, 2) != monitor)
        {
            UpdateBounds();
            return;
        }

        if (!_isRevealed)
        {
            if (inEdgeZone && !_autoRevealSuppressed)
            {
                _hotZoneSince ??= now;
                if (now - _hotZoneSince.Value >= TimeSpan.FromMilliseconds(250))
                    _ = RevealAsync();
            }
            else
            {
                _hotZoneSince = null;
            }
            return;
        }

        var bounds = _appWindow.Position;
        var size = _appWindow.Size;
        var inLineZone = cursor.X >= bounds.X && cursor.X < bounds.X + size.Width &&
                         cursor.Y >= bounds.Y && cursor.Y < bounds.Y + size.Height;
        var inHotZone = cursor.Y >= monitorInfo.Monitor.Top && cursor.Y < monitorInfo.Monitor.Top + size.Height;
        var busy = _manualReveal || now < _peekUntil;
        if (inLineZone || inHotZone || busy)
        {
            if (_manualReveal && inLineZone)
            {
                _manualReveal = false;
                _peekUntil = DateTimeOffset.MinValue;
            }
            _awaySince = null;
            return;
        }

        _awaySince ??= now;
        if (now - _awaySince.Value >= TimeSpan.FromMilliseconds(500))
        {
            _awaySince = null;
            _ = HideLineAsync();
        }
    }

    private bool HasSomethingToShow() => _clothesline.Items.Count > 0 || _manualEmptyPreview ||
        ((App)Application.Current).Services.UserActions.UndoableItem is not null || _manualReveal;

    private bool IsFullscreenOnCursorDisplay()
    {
        GetCursorPos(out var cursor);
        return IsFullscreen(MonitorFromPoint(cursor, 2));
    }

    private void OnForegroundWindowChanged(nint hook, uint eventType, nint hwnd, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        DispatcherQueue.TryEnqueue(UpdatePointerVisibility);
    }

    private static bool IsFullscreen(nint monitor)
    {
        if (SHQueryUserNotificationState(out var notificationState) >= 0 && notificationState is >= 1 and <= 4)
            return true;
        if (monitor == nint.Zero) return false;
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
        if (!GetWindowRect(foreground, out var rect)) return false;
        var info = GetMonitorInfo(monitor);
        const int tolerance = 2;
        var coversDisplay = Math.Abs(rect.Left - info.Monitor.Left) <= tolerance &&
                            Math.Abs(rect.Top - info.Monitor.Top) <= tolerance &&
                            Math.Abs(rect.Right - info.Monitor.Right) <= tolerance &&
                            Math.Abs(rect.Bottom - info.Monitor.Bottom) <= tolerance;
        if (!coversDisplay) return false;
        const int GwlStyle = -16;
        const long WsCaption = 0x00C00000;
        var className = new System.Text.StringBuilder(128);
        _ = GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        return (GetWindowLongPtr(foreground, GwlStyle).ToInt64() & WsCaption) == 0;
    }

    private void UpdateBounds()
    {
        var count = _clothesline.Items.Count;
        if (count == 0 && !_manualEmptyPreview && UndoToast.Visibility != Visibility.Visible) return;
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2);
        if (monitor == nint.Zero) return;
        var info = GetMonitorInfo(monitor);
        var dpiResult = GetDpiForMonitor(monitor, 0, out _, out var dpiY);
        if (dpiResult != 0 || dpiY == 0) dpiY = 96;
        var height = (int)Math.Round(218d * dpiY / 96d);
        var width = info.Monitor.Right - info.Monitor.Left;
        _clothesline.MaxItems = Math.Clamp((int)((width / (dpiY / 96d) - 200) / 174d), 3, 12);
        MoveIfBoundsChanged(info.Monitor.Left, info.Work.Top, width, height);
    }

    private void EnsureCurrentMonitorGeometry()
    {
        var monitor = MonitorFromWindow(_windowHandle, 2);
        if (monitor == nint.Zero) return;
        var info = GetMonitorInfo(monitor);
        var dpiResult = GetDpiForMonitor(monitor, 0, out _, out var dpiY);
        if (dpiResult != 0 || dpiY == 0) dpiY = 96;
        var height = (int)Math.Round(218d * dpiY / 96d);
        _clothesline.MaxItems = Math.Clamp((int)(((info.Monitor.Right - info.Monitor.Left) / (dpiY / 96d) - 200) / 174d), 3, 12);
        MoveIfBoundsChanged(info.Monitor.Left, info.Work.Top, info.Monitor.Right - info.Monitor.Left, height);
    }

    private void MoveIfBoundsChanged(int x, int y, int width, int height)
    {
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        if (position.X == x && position.Y == y && size.Width == width && size.Height == height) return;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    private static void ConfigureNonActivatingOverlay(nint hwnd)
    {
        const int GwlExStyle = -20;
        const long WsExToolWindow = 0x00000080;
        const long WsExNoActivate = 0x08000000;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(hwnd, GwlExStyle, new nint(style | WsExToolWindow | WsExNoActivate));
        _ = SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
    }

    private void SetWindowActivationEnabled(bool enabled)
    {
        const int GwlExStyle = -20;
        const long WsExNoActivate = 0x08000000;
        var style = GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
        style = enabled ? style & ~WsExNoActivate : style | WsExNoActivate;
        _ = SetWindowLongPtr(_windowHandle, GwlExStyle, new nint(style));
        _ = SetWindowPos(_windowHandle, nint.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
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

    private Task AnimateLineAsync(bool reveal)
    {
        var animation = new DoubleAnimation
        {
            From = LineTranslation.Y,
            To = reveal ? 0 : -222,
            Duration = new Duration(TimeSpan.FromMilliseconds(reveal ? 420 : 220)),
            EasingFunction = reveal
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, LineTranslation);
        Storyboard.SetTargetProperty(animation, "Y");
        storyboard.Children.Add(animation);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storyboard.Completed += (_, _) => completion.TrySetResult();
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

    public void Shutdown()
    {
        AppLogger.Write("Quit", "step=hide-line");
        _pointerTimer.Stop();
        if (_foregroundHook != nint.Zero)
        {
            _ = UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        }
        AppLogger.Write("Quit", "step=window-hooks-removed");
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
    private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int objectId, int childId,
        uint eventThread, uint eventTime);

    private static NativeMonitorInfo GetMonitorInfo(nint monitor)
    {
        var info = new NativeMonitorInfo { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
        _ = GetMonitorInfoNative(monitor, ref info);
        return info;
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern nint MonitorFromPoint(CursorPoint point, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder className, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoNative(nint monitor, ref NativeMonitorInfo info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint eventHookModule,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

}
