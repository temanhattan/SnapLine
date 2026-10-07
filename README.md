# SnapLine

SnapLine is a native Windows desktop utility built with C#, .NET, WinUI 3, and the Windows App SDK.

## Project structure

- `Services/IScreenshotDetector` — identifies newly captured screenshot files.
- `Services/ClipboardScreenshotDetector` — listens for bitmap clipboard updates from Snipping Tool (Win + Shift + S).
- `Services/CompositeScreenshotDetector` — allows multiple capture providers to feed the same pipeline.
- `Services/IScreenshotStorage` — owns the stored-file lifecycle.
- `Models/ScreenshotItem` — identity, temporary original path, thumbnail path, capture time, and pending/active/viewed/shared/saved/deleted state.
- `Services/IClotheslineService` — coordinates detection, storage, and item state.
- `Services/IUserActionService` — handles actions initiated from the UI.
- `MainWindow` — WinUI presentation layer.

Temporary screenshots are held under `%LOCALAPPDATA%\SnapLine\Temporary` in a per-run directory. Startup removes abandoned sessions but leaves files owned by another active process intact; saving copies the original to the user-selected destination and removes its temporary files. Thumbnails are reduced to at most 480 × 320 pixels before encoding. Clipboard captures with identical image data within two seconds are treated as duplicate notifications, and SnapLine's own Copy command is tagged so it is not re-imported as a new capture.

The floating clothesline is borderless, topmost, and uses a Desktop Acrylic backdrop so the desktop shows through. It spans the display width and sits near its top edge. Press `Ctrl+Alt+S` to reveal it. An empty preview is shown at startup; screenshot detection listens for clipboard bitmap updates from Win + Shift + S. Clicking an item opens the original image with its Windows default handler. Right-clicking offers Open, Edit, Copy Image, Copy File, Save, Save As, Open With, and Delete. Save and Save As both ask the user to choose a destination. The destination is written and verified before the temporary screenshot is removed; cancelling or failing to save leaves it on the clothesline. Copy Image publishes bitmap clipboard data; Copy File publishes a normal Windows file-drop item. Both remove the screenshot from the clothesline while keeping its temporary source available for the clipboard paste until the app session ends. Dragging publishes the original as a native Windows storage item; successful drops consume it, while cancelled drags leave it in place.

The project is configured as an unpackaged, self-contained Windows App SDK app. The executable includes the Windows App SDK and .NET runtime, so it does not require a separate Windows App Runtime installation. There is not yet a system-tray affordance.

## Build prerequisites

Install the .NET 10 SDK and Windows 10/11 SDK. Build with `dotnet build SnapLine.sln`. To launch the self-contained x64 build, run `SnapLine/bin/Debug/net10.0-windows10.0.19041.0/win-x64/SnapLine.exe`.
