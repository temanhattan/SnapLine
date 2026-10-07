using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnapLine.Services;

public sealed class WindowsScreenshotShell : IScreenshotShell
{
    public void Open(string filePath) => Launch(filePath, "open");
    public void Edit(string filePath) => Launch(filePath, "edit");
    public void Reveal(string filePath) => Process.Start(new ProcessStartInfo
    {
        FileName = "explorer.exe",
        Arguments = $"/select,\"{Path.GetFullPath(filePath)}\"",
        UseShellExecute = true
    });

    public void OpenWith(string filePath, nint ownerWindow)
    {
        var info = new OpenAsInfo { FilePath = filePath, FileClass = nint.Zero, Flags = 0 };
        var result = SHOpenWithDialog(ownerWindow, ref info);
        Marshal.ThrowExceptionForHR(result);
    }

    private static void Launch(string filePath, string verb)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            Verb = verb,
            UseShellExecute = true
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public nint FileClass;
        public uint Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(nint ownerWindow, ref OpenAsInfo info);
}
