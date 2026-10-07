# SnapLine

SnapLine is a Windows screenshot clothesline, ported from the macOS utility Tendedero. It is a background WinUI 3 app for Windows 10/11, built with C# and .NET 10.

## What it does

- Shows screenshots as small cards near the top of the current monitor. A global `Ctrl+Alt+T` shortcut toggles the line; the tray icon provides Show/Hide, Clear, Open Screenshots Folder, Settings, and Quit.
- A click copies the image to the clipboard and leaves it on the line. A double-click opens it in the default image app. Hold for 450 ms to open the built-in markup editor. Drag files to another app or Explorer; right-click for Copy, Open, Markup, Show in Explorer, and applicable save/discard actions.
- Watches the Windows Screenshots folder, Desktop screenshot-named images, and Game Bar captures. Clipboard bitmap notifications support Snipping Tool and other capture tools that publish an image.
- Stores clipboard captures in `%APPDATA%\SnapLine\Screenshots` and remembers line items across restarts. Captures already saved elsewhere remain at their original paths. Removing an inbox capture sends it to the Recycle Bin; removing an existing file only takes it off the line.
- Offers English and Spanish strings, configurable startup and sounds, and an original SnapLine icon.

## Windows behavior and limits

Windows does not expose a supported global API for changing Snipping Tool's save or floating-preview preferences. SnapLine's optional first-run capture handling is app-managed: it watches clipboard images and standard screenshot folders, and can keep clipboard captures in its own Screenshots folder. It does not change Windows capture settings, suppress Windows notifications, or guarantee detection from every third-party capture app. The notification-area Settings panel can enable/disable capture handling and sounds, and configure launch at sign-in.

The line approximates macOS Spaces/fullscreen behavior by checking Windows fullscreen and notification states. Windows desktop composition, capture flows, Trash semantics, and system sounds differ from macOS. See [SnapLine/PORTING_ANALYSIS.md](SnapLine/PORTING_ANALYSIS.md) for the source audit, parity matrix, research links, and remaining limitations.

## Build

Install the .NET 10 SDK and Windows 10/11 SDK, then run:

```powershell
dotnet build SnapLine.sln
```

The generated icon can be recreated with `powershell -ExecutionPolicy Bypass -File scripts/New-SnapLineIcon.ps1`.

## Attribution and license

SnapLine is an independent Windows port. The original Tendedero source is © 2026 Alejandro Buján and licensed under MIT; see [LICENSES/Tendedero-MIT.txt](LICENSES/Tendedero-MIT.txt). Tendedero's name and icon are excluded from that license and are not used by SnapLine. SnapLine's own project license remains in the root `LICENSE` file.
