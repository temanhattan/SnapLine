using System.Runtime.InteropServices;

namespace SnapLine.Services;

/// <summary>Uses the user's configured Windows system sounds as a local approximation of Tink/Pop.</summary>
public static class WindowsSoundEffects
{
    public static void Capture() => _ = MessageBeep(0x00000040); // MB_ICONASTERISK
    public static void Remove() => _ = MessageBeep(0x00000030); // MB_ICONEXCLAMATION

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
