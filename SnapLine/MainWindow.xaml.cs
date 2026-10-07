using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using SnapLine.Models;
using SnapLine.Services;
using WinRT.Interop;

namespace SnapLine;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IClotheslineService _clothesline;
    private Storyboard? _visibilityStoryboard;
    private TaskCompletionSource? _visibilityAnimationCompletion;
    private bool _isVisible;

    public MainWindow()
    {
        _clothesline = ((App)Application.Current).Services.Clothesline;
        InitializeComponent();

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.Closing += OnClosing;

        _clothesline.Items.CollectionChanged += (_, _) => OnItemsChanged();
        ExtendFrameIntoClientArea(WindowNative.GetWindowHandle(this));
        UpdateBounds();
        _appWindow.Hide();
    }

    public IClotheslineService Clothesline => _clothesline;

    private async void OnScreenshotTapped(object sender, TappedRoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is FrameworkElement { DataContext: ScreenshotItem item })
            await ((App)Application.Current).Services.UserActions.ViewAsync(item);
    }

    private async void OnScreenshotDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } || !File.Exists(item.FilePath))
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            args.Data.SetStorageItems(new List<IStorageItem> { file });
            args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
            args.DragUI.SetContentFromBitmapImage(item.ThumbnailImage);
        }
        catch (Exception)
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnScreenshotDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (args.DropResult == DataPackageOperation.None ||
            sender is not FrameworkElement { DataContext: ScreenshotItem item })
            return;

        await ((App)Application.Current).Services.UserActions.CompleteAsync(item);
    }

    private void OnScreenshotContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ScreenshotItem item } element) return;

        var actions = ((App)Application.Current).Services.UserActions;
        var menu = new MenuFlyout();
        AddAction(menu, "Open", () => actions.ViewAsync(item));
        AddAction(menu, "Edit", () => actions.EditAsync(item));
        AddAction(menu, "Copy", () => actions.CopyAsync(item));
        menu.Items.Add(new MenuFlyoutSeparator());
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
            if (!_isVisible) return;
            await AnimateOpacityAsync(0);
            if (_clothesline.Items.Count == 0)
            {
                _appWindow.Hide();
                _isVisible = false;
                ClotheslineRoot.Opacity = 1;
            }
            return;
        }

        UpdateBounds();
        if (!_isVisible)
        {
            ClotheslineRoot.Opacity = 0;
            _appWindow.Show();
            _isVisible = true;
        }
        await AnimateOpacityAsync(1);
    }

    private void UpdateBounds()
    {
        var count = _clothesline.Items.Count;
        if (count == 0) return;
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        const int height = 218;
        var width = Math.Min(Math.Max(320, 160 + count * 144), workArea.Width - 32);
        width = Math.Max(240, width);
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + 8;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
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
    }

    private static void ExtendFrameIntoClientArea(IntPtr hwnd)
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
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
