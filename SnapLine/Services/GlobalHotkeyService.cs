using System.Runtime.InteropServices;

namespace SnapLine.Services;

/// <summary>Registers one process-wide shortcut against the app window and forwards WM_HOTKEY.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint HotkeyModifiers = 0x0002 | 0x0001; // MOD_CONTROL | MOD_ALT
    private const uint VirtualKeyS = 0x53;
    private const uint HotkeyMessage = 0x0312;
    private const nuint HotkeyId = 0x534E;

    private readonly SubclassProcedure _subclassProcedure;
    private nint _windowHandle;
    private bool _subclassInstalled;
    private bool _registered;

    public GlobalHotkeyService() => _subclassProcedure = OnWindowMessage;

    public event EventHandler? Pressed;
    public bool IsRegistered => _registered;

    public bool RegisterShowClothesline(nint windowHandle)
    {
        if (_registered) return true;
        _windowHandle = windowHandle;
        _subclassInstalled = SetWindowSubclass(_windowHandle, _subclassProcedure, HotkeyId, 0);
        if (!_subclassInstalled) return false;

        _registered = RegisterHotKey(_windowHandle, (int)HotkeyId, HotkeyModifiers, VirtualKeyS);
        if (!_registered)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, HotkeyId);
            _subclassInstalled = false;
        }
        return _registered;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, (int)HotkeyId);
            _registered = false;
        }
        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, HotkeyId);
            _subclassInstalled = false;
        }
    }

    private nint OnWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        if (message == HotkeyMessage && wParam == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure callback, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
