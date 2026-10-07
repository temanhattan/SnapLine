using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SnapLine.Services;
using WinRT.Interop;

namespace SnapLine;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IClotheslineService _clothesline;

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

        _clothesline.Items.CollectionChanged += (_, _) => UpdateVisibilityAndBounds();
        ExtendFrameIntoClientArea(WindowNative.GetWindowHandle(this));
        UpdateVisibilityAndBounds();
    }

    public IClotheslineService Clothesline => _clothesline;

    private void UpdateVisibilityAndBounds()
    {
        var count = _clothesline.Items.Count;
        if (count == 0)
        {
            _appWindow.Hide();
            return;
        }

        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        const int height = 218;
        var width = Math.Min(Math.Max(320, 160 + count * 144), workArea.Width - 32);
        width = Math.Max(240, width);
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + 8;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        _appWindow.Show();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
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
