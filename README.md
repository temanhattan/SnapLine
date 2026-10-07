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

Temporary screenshots are held under `%LOCALAPPDATA%\SnapLine\Temporary` in a per-run directory. Startup removes leftover entries from previous runs; saving copies the original to the user-selected destination and removes its temporary files. Thumbnails are reduced to at most 480 × 320 pixels before encoding, so the UI does not need to decode the full-resolution original just to display an item.

The floating clothesline is borderless, topmost, transparent, and positioned along the top of the primary display. It remains hidden while empty, grows with the item count, and scrolls horizontally when the available width is reached. It fades in when the first screenshot arrives and fades out briefly after the last item is consumed. WinUI item transitions handle item insertion, removal, and repositioning. Win + Shift + S is detected through Windows clipboard bitmap updates; screenshots are copied into temporary storage and displayed in capture order. Clicking an item opens the original image with its Windows default handler and marks it as viewed; viewing leaves it on the clothesline. Right-clicking opens independent Open, Edit, Copy, Save As, Open With, and Delete actions. Dragging publishes the original as a native Windows storage item; successful drops consume it, while cancelled drags leave it in place. Operating system effects are isolated behind interfaces so the action service can be tested with substitutes.

The current project is a foundation only; screenshot detection, tray integration, and item interactions are not implemented yet. Closing the window hides it so the app can remain alive for background work. A tray affordance will be needed to make reopening the hidden window discoverable.

## Build prerequisites

Install the .NET 8 SDK, Visual Studio 2022 with the Windows App SDK/WinUI workload, and the Windows 10/11 SDK. Open `SnapLine.sln` and run the `SnapLine` project on Windows.
