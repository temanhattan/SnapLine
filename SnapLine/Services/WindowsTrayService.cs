using System.Runtime.InteropServices;

namespace SnapLine.Services;

/// <summary>Minimal native notification-area icon and context menu for the background utility.</summary>
public sealed class WindowsTrayService : ITrayService
{
    private const uint CallbackMessage = 0x8001;
    private const uint WmCommand = 0x0111;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmNull = 0;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private const uint NidAdd = 0;
    private const uint NidDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint MfString = 0;
    private readonly SubclassProc _proc;
    private readonly nint windowHandle;
    private readonly nint trayIcon;
    private bool _started;

    public WindowsTrayService(nint windowHandle)
    {
        this.windowHandle = windowHandle;
        _proc = Dispatch;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SnapLine.ico");
        trayIcon = LoadImage(nint.Zero, iconPath, 1, 0, 0, 0x0010);
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? OpenScreenshotsFolderRequested;
    public event EventHandler? OpenLogFolderRequested;
    public event EventHandler? ExitRequested;

    public void Start()
    {
        if (_started) return;
        if (!SetWindowSubclass(windowHandle, _proc, 0x534C, 0))
            throw new InvalidOperationException("Could not attach the SnapLine tray menu to its window.");
        var data = CreateData();
        if (!Shell_NotifyIcon(NidAdd, ref data))
        {
            RemoveWindowSubclass(windowHandle, _proc, 0x534C);
            throw new InvalidOperationException("Windows could not create the SnapLine notification-area icon.");
        }
        _started = true;
    }

    public void Dispose()
    {
        if (!_started) return;
        var data = CreateData();
        _ = Shell_NotifyIcon(NidDelete, ref data);
        RemoveWindowSubclass(windowHandle, _proc, 0x534C);
        if (trayIcon != nint.Zero) _ = DestroyIcon(trayIcon);
        _started = false;
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = windowHandle,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = CallbackMessage,
        Icon = trayIcon != nint.Zero ? trayIcon : LoadIcon(nint.Zero, new nint(32512)),
        Tip = Localization.Get("TrayTip")
    };

    private nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (message == CallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            AppLogger.Write("Tray", $"stage=callback notification={notification}");
            if (notification == WmLeftButtonUp) ShowRequested?.Invoke(this, EventArgs.Empty);
            else if (notification == WmContextMenu || notification == WmRightButtonUp) ShowMenu();
            return 0;
        }
        if (message == TaskbarCreated)
        {
            var data = CreateData();
            if (Shell_NotifyIcon(NidAdd, ref data))
                AppLogger.Write("Tray", "stage=taskbar-restart icon-readded");
            else
                AppLogger.Write("Tray", "stage=taskbar-restart icon-readd-failed");
            return 0;
        }
        if (message == WmCommand)
        {
            var command = unchecked((int)(wParam.ToUInt64() & 0xFFFF));
            AppLogger.Write("Tray", $"stage=command-selected id={command}");
            switch (command)
            {
                case 1001: AppLogger.Write("Tray", "stage=handler-invoked action=show"); ShowRequested?.Invoke(this, EventArgs.Empty); return 0;
                case 1002: AppLogger.Write("Tray", "stage=handler-invoked action=hide"); HideRequested?.Invoke(this, EventArgs.Empty); return 0;
                case 1003: AppLogger.Write("Tray", "stage=handler-invoked action=settings"); SettingsRequested?.Invoke(this, EventArgs.Empty); return 0;
                case 1004: AppLogger.Write("Tray", "stage=handler-invoked action=quit"); ExitRequested?.Invoke(this, EventArgs.Empty); return 0;
                case 1005: AppLogger.Write("Tray", "stage=handler-invoked action=clear"); ClearRequested?.Invoke(this, EventArgs.Empty); return 0;
                case 1006: AppLogger.Write("Tray", "stage=handler-invoked action=open-screenshots"); OpenScreenshotsFolderRequested?.Invoke(this, EventArgs.Empty); return 0;
                case 1007: AppLogger.Write("Tray", "stage=handler-invoked action=open-logs"); OpenLogFolderRequested?.Invoke(this, EventArgs.Empty); return 0;
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero) return;
        try
        {
            AppLogger.Write("Tray", "stage=menu-opened");
            AppendMenu(menu, MfString, 1001, Localization.Get("ShowLine"));
            AppendMenu(menu, MfString, 1002, Localization.Get("HideLine"));
            AppendMenu(menu, MfString, 1005, Localization.Get("TakeEverythingDown"));
            AppendMenu(menu, MfString, 1006, Localization.Get("OpenScreenshotsFolder"));
            AppendMenu(menu, MfString, 1003, Localization.Get("Settings"));
            AppendMenu(menu, MfString, 1004, Localization.Get("Quit"));
            AppendMenu(menu, MfString, 1007, Localization.Get("OpenLogFolder"));
            GetCursorPos(out var point);
            _ = SetForegroundWindow(windowHandle);
            _ = TrackPopupMenu(menu, 0, point.X, point.Y, 0, windowHandle, nint.Zero);
            _ = PostMessage(windowHandle, WmNull, 0, 0);
            AppLogger.Write("Tray", "stage=menu-closed");
        }
        finally { DestroyMenu(menu); }
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
