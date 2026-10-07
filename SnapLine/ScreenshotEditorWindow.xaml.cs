using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SnapLine.Models;
using SnapLine.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Foundation;
using Windows.UI;

namespace SnapLine;

/// <summary>A lightweight, pixel-sized annotation surface for temporary screenshots.</summary>
public sealed partial class ScreenshotEditorWindow : Window
{
    private readonly ScreenshotItem _item;
    private readonly IScreenshotStorage _storage;
    private readonly Stack<EditorState> _undo = new();
    private readonly List<Rect> _blurRegions = [];
    private byte[] _pixels = [];
    private uint _width;
    private uint _height;
    private EditMode _mode = EditMode.Draw;
    private Point _start;
    private Polyline? _activeStroke;
    private Rectangle? _selectionPreview;
    private EditorState? _operationStart;
    private Rect? _cropSelection;

    public ScreenshotEditorWindow(ScreenshotItem item, IScreenshotStorage storage)
    {
        _item = item;
        _storage = storage;
        InitializeComponent();
        Title = Localization.Get("EditorTitle");
        CropButton.Content = Localization.Get("Crop");
        DrawButton.Content = Localization.Get("Draw");
        HighlightButton.Content = Localization.Get("Highlight");
        TextButton.Content = Localization.Get("Text");
        BlurButton.Content = Localization.Get("Blur");
        RedactButton.Content = Localization.Get("Redact");
        ApplyCropButton.Content = Localization.Get("ApplyCrop");
        UndoButton.Content = Localization.Get("Undo");
        CancelButton.Content = Localization.Get("Cancel");
        SaveButton.Content = Localization.Get("SaveChanges");
        ModeLabel.Text = $"{Localization.Get("Tool")}: {Localization.Get("Draw")}";
        ((UIElement)Content).KeyDown += OnEditorKeyDown;
        SourceImage.Source = item.ThumbnailImage;
        ((FrameworkElement)Content).Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ((UIElement)Content).Focus(FocusState.Programmatic);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_item.OriginalImagePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            _width = decoder.OrientedPixelWidth;
            _height = decoder.OrientedPixelHeight;
            var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            _pixels = pixelData.DetachPixelData();

            ImageSurface.Width = _width;
            ImageSurface.Height = _height;
            SourceImage.Width = _width;
            SourceImage.Height = _height;
            AnnotationCanvas.Width = _width;
            AnnotationCanvas.Height = _height;
            ImageViewbox.MaxWidth = Math.Max(400, Content is FrameworkElement root ? root.ActualWidth - 64 : 1200);
            ImageViewbox.MaxHeight = Math.Max(300, Content is FrameworkElement root2 ? root2.ActualHeight - 100 : 700);
            await RefreshPreviewAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Localization.Get("CouldNotOpen"), exception.Message);
            Close();
        }
    }

    private async void OnEditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            args.Handled = true;
        }
        else if (ctrl && args.Key == Windows.System.VirtualKey.Z)
        {
            OnUndo(this, new RoutedEventArgs());
            args.Handled = true;
        }
        else if ((ctrl && args.Key == Windows.System.VirtualKey.S) ||
                 args.Key == Windows.System.VirtualKey.Enter)
        {
            await SaveAsync();
            args.Handled = true;
        }
    }

    private void OnCropMode(object sender, RoutedEventArgs args) => SetMode(EditMode.Crop);
    private void OnDrawMode(object sender, RoutedEventArgs args) => SetMode(EditMode.Draw);
    private void OnHighlightMode(object sender, RoutedEventArgs args) => SetMode(EditMode.Highlight);
    private void OnTextMode(object sender, RoutedEventArgs args) => SetMode(EditMode.Text);
    private void OnBlurMode(object sender, RoutedEventArgs args) => SetMode(EditMode.Blur);
    private void OnRedactMode(object sender, RoutedEventArgs args) => SetMode(EditMode.Redact);

    private void SetMode(EditMode mode)
    {
        _mode = mode;
        ModeLabel.Text = $"{Localization.Get("Tool")}: {Localization.Get(mode.ToString())}";
        _cropSelection = null;
        RemoveSelectionPreview();
    }

    private async void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = ClampPoint(args.GetCurrentPoint(AnnotationCanvas).Position);
        if (_mode == EditMode.Text)
        {
            await AddTextAsync(point);
            args.Handled = true;
            return;
        }

        _start = point;
        _operationStart = CaptureState();
        AnnotationCanvas.CapturePointer(args.Pointer);

        if (_mode is EditMode.Draw or EditMode.Highlight)
        {
            _activeStroke = new Polyline
            {
                Stroke = new SolidColorBrush(_mode == EditMode.Draw
                    ? Colors.White
                    : Color.FromArgb(150, 255, 222, 45)),
                StrokeThickness = _mode == EditMode.Draw ? 7 : 34,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _activeStroke.Points.Add(point);
            AnnotationCanvas.Children.Add(_activeStroke);
        }
        else
        {
            ShowSelection(point, point);
        }
        args.Handled = true;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_operationStart is null) return;
        var point = ClampPoint(args.GetCurrentPoint(AnnotationCanvas).Position);
        if (_activeStroke is not null)
            _activeStroke.Points.Add(point);
        else
            ShowSelection(_start, point);
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_operationStart is null) return;
        var point = ClampPoint(args.GetCurrentPoint(AnnotationCanvas).Position);
        AnnotationCanvas.ReleasePointerCapture(args.Pointer);

        if (_activeStroke is not null)
        {
            if (_activeStroke.Points.Count > 1)
                _undo.Push(_operationStart);
            else
                AnnotationCanvas.Children.Remove(_activeStroke);
            _activeStroke = null;
        }
        else
        {
            var rect = MakeRect(_start, point);
            RemoveSelectionPreview();
            if (rect.Width >= 2 && rect.Height >= 2)
            {
                if (_mode == EditMode.Crop)
                {
                    _cropSelection = rect;
                    ShowSelection(new Point(rect.X, rect.Y), new Point(rect.Right, rect.Bottom));
                }
                else if (_mode == EditMode.Blur)
                {
                    _undo.Push(_operationStart);
                    _blurRegions.Add(rect);
                    AddRegionOutline(rect, "Blur");
                }
                else if (_mode == EditMode.Redact)
                {
                    _undo.Push(_operationStart);
                    AddRedaction(rect);
                }
            }
        }

        _operationStart = null;
        args.Handled = true;
    }

    private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_activeStroke is not null)
            AnnotationCanvas.Children.Remove(_activeStroke);
        _activeStroke = null;
        _operationStart = null;
        RemoveSelectionPreview();
        AnnotationCanvas.ReleasePointerCapture(args.Pointer);
    }

    private async Task AddTextAsync(Point point)
    {
        var input = new TextBox { PlaceholderText = Localization.Get("TypeLabel"), AcceptsReturn = true, MinWidth = 300 };
        var dialog = new ContentDialog
        {
            Title = Localization.Get("AddText"),
            Content = input,
            PrimaryButtonText = Localization.Get("AddLabel"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;

        _undo.Push(CaptureState());
        var text = new TextBlock
        {
            Text = input.Text,
            FontSize = Math.Clamp(_width / 35d, 28, 64),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(80, _width - point.X),
            Shadow = new ThemeShadow()
        };
        Canvas.SetLeft(text, point.X);
        Canvas.SetTop(text, point.Y);
        AnnotationCanvas.Children.Add(text);
    }

    private void OnApplyCrop(object sender, RoutedEventArgs args) => _ = ApplyCropAsync();

    private async Task ApplyCropAsync()
    {
        if (_cropSelection is not { } selection)
        {
            await ShowErrorAsync(Localization.Get("ChooseCrop"), Localization.Get("SelectCrop"));
            return;
        }

        try
        {
            var state = CaptureState();
            var composed = await ComposeCurrentImageAsync();
            var left = (uint)Math.Clamp(Math.Floor(selection.Left), 0, _width - 1);
            var top = (uint)Math.Clamp(Math.Floor(selection.Top), 0, _height - 1);
            var right = (uint)Math.Clamp(Math.Ceiling(selection.Right), left + 1, _width);
            var bottom = (uint)Math.Clamp(Math.Ceiling(selection.Bottom), top + 1, _height);
            var newWidth = right - left;
            var newHeight = bottom - top;
            var cropped = new byte[(long)newWidth * newHeight * 4];
            for (uint row = 0; row < newHeight; row++)
                System.Buffer.BlockCopy(composed, (int)(((top + row) * _width + left) * 4), cropped,
                    (int)(row * newWidth * 4), (int)(newWidth * 4));

            _undo.Push(state);
            _pixels = cropped;
            _width = newWidth;
            _height = newHeight;
            _blurRegions.Clear();
            _cropSelection = null;
            AnnotationCanvas.Children.Clear();
            await ResizeSurfaceAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Localization.Get("CropFailed"), exception.Message);
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs args)
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop();
        _pixels = state.Pixels;
        _width = state.Width;
        _height = state.Height;
        _blurRegions.Clear();
        _blurRegions.AddRange(state.BlurRegions);
        _cropSelection = null;
        AnnotationCanvas.Children.Clear();
        await ResizeSurfaceAsync();
        foreach (var element in state.Elements)
            AnnotationCanvas.Children.Add(element);
        foreach (var region in state.BlurRegions)
            AddRegionOutline(region, "Blur");
    }

    private async void OnSave(object sender, RoutedEventArgs args) => await SaveAsync();

    private async Task SaveAsync()
    {
        try
        {
            var pixels = await ComposeCurrentImageAsync();
            await _storage.ReplaceImageAsync(_item, pixels, _width, _height);
            Close();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(Localization.Get("CouldNotSave"), exception.Message);
        }
    }

    private async Task<byte[]> ComposeCurrentImageAsync()
    {
        var result = (byte[])_pixels.Clone();
        var hiddenSelection = _selectionPreview;
        if (hiddenSelection is not null)
            AnnotationCanvas.Children.Remove(hiddenSelection);
        var regionOutlines = AnnotationCanvas.Children.OfType<FrameworkElement>()
            .Where(e => Equals(e.Tag, "BlurRegionOutline")).ToArray();
        foreach (var element in regionOutlines)
            AnnotationCanvas.Children.Remove(element);

        try
        {
            AnnotationCanvas.UpdateLayout();
            var render = new RenderTargetBitmap();
            await render.RenderAsync(AnnotationCanvas, checked((int)_width), checked((int)_height));
            var buffer = await render.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(buffer);
            var overlay = new byte[buffer.Length];
            reader.ReadBytes(overlay);
            if (overlay.Length != result.Length)
                throw new InvalidDataException("The annotation layer did not render at the screenshot's full resolution.");
            CompositePremultipliedBgra(result, overlay);
        }
        finally
        {
            if (hiddenSelection is not null)
                AnnotationCanvas.Children.Add(hiddenSelection);
            foreach (var element in regionOutlines)
                AnnotationCanvas.Children.Add(element);
        }

        foreach (var region in _blurRegions)
            BlurRegion(result, region);
        return result;
    }

    private void CompositePremultipliedBgra(byte[] destination, byte[] overlay)
    {
        var count = Math.Min(destination.Length, overlay.Length) / 4;
        for (var index = 0; index < count; index++)
        {
            var offset = index * 4;
            var inverseAlpha = 255 - overlay[offset + 3];
            destination[offset] = (byte)Math.Min(255, overlay[offset] + (destination[offset] * inverseAlpha + 127) / 255);
            destination[offset + 1] = (byte)Math.Min(255, overlay[offset + 1] + (destination[offset + 1] * inverseAlpha + 127) / 255);
            destination[offset + 2] = (byte)Math.Min(255, overlay[offset + 2] + (destination[offset + 2] * inverseAlpha + 127) / 255);
            destination[offset + 3] = 255;
        }
    }

    private void BlurRegion(byte[] pixels, Rect region)
    {
        var left = (int)Math.Clamp(Math.Floor(region.Left), 0, _width - 1);
        var top = (int)Math.Clamp(Math.Floor(region.Top), 0, _height - 1);
        var right = (int)Math.Clamp(Math.Ceiling(region.Right), left + 1, _width);
        var bottom = (int)Math.Clamp(Math.Ceiling(region.Bottom), top + 1, _height);
        var regionWidth = right - left;
        var regionHeight = bottom - top;
        var radius = Math.Clamp(Math.Min(regionWidth, regionHeight) / 35, 5, 18);
        var horizontal = new byte[regionWidth * regionHeight * 4];
        var prefixB = new int[Math.Max(regionWidth, regionHeight) + 1];
        var prefixG = new int[prefixB.Length];
        var prefixR = new int[prefixB.Length];

        for (var y = 0; y < regionHeight; y++)
        {
            prefixB[0] = prefixG[0] = prefixR[0] = 0;
            for (var x = 0; x < regionWidth; x++)
            {
                var offset = ((top + y) * (int)_width + left + x) * 4;
                prefixB[x + 1] = prefixB[x] + pixels[offset];
                prefixG[x + 1] = prefixG[x] + pixels[offset + 1];
                prefixR[x + 1] = prefixR[x] + pixels[offset + 2];
            }

            for (var x = 0; x < regionWidth; x++)
            {
                var first = Math.Max(0, x - radius);
                var afterLast = Math.Min(regionWidth, x + radius + 1);
                var samples = afterLast - first;
                var target = (y * regionWidth + x) * 4;
                horizontal[target] = (byte)((prefixB[afterLast] - prefixB[first]) / samples);
                horizontal[target + 1] = (byte)((prefixG[afterLast] - prefixG[first]) / samples);
                horizontal[target + 2] = (byte)((prefixR[afterLast] - prefixR[first]) / samples);
                horizontal[target + 3] = 255;
            }
        }

        for (var x = 0; x < regionWidth; x++)
        {
            prefixB[0] = prefixG[0] = prefixR[0] = 0;
            for (var y = 0; y < regionHeight; y++)
            {
                var offset = (y * regionWidth + x) * 4;
                prefixB[y + 1] = prefixB[y] + horizontal[offset];
                prefixG[y + 1] = prefixG[y] + horizontal[offset + 1];
                prefixR[y + 1] = prefixR[y] + horizontal[offset + 2];
            }

            for (var y = 0; y < regionHeight; y++)
            {
                var first = Math.Max(0, y - radius);
                var afterLast = Math.Min(regionHeight, y + radius + 1);
                var samples = afterLast - first;
                var target = ((top + y) * (int)_width + left + x) * 4;
                pixels[target] = (byte)((prefixB[afterLast] - prefixB[first]) / samples);
                pixels[target + 1] = (byte)((prefixG[afterLast] - prefixG[first]) / samples);
                pixels[target + 2] = (byte)((prefixR[afterLast] - prefixR[first]) / samples);
            }
        }
    }

    private void AddRedaction(Rect rect)
    {
        var fill = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = new SolidColorBrush(Colors.Black) };
        Canvas.SetLeft(fill, rect.X);
        Canvas.SetTop(fill, rect.Y);
        AnnotationCanvas.Children.Add(fill);
    }

    private void AddRegionOutline(Rect rect, string label)
    {
        var outline = new Rectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 155, 207, 255)),
            StrokeThickness = 3,
            Tag = "BlurRegionOutline",
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, rect.X);
        Canvas.SetTop(outline, rect.Y);
        AnnotationCanvas.Children.Add(outline);
        var labelBlock = new TextBlock { Text = label, Foreground = new SolidColorBrush(Colors.White), FontSize = 18, IsHitTestVisible = false };
        Canvas.SetLeft(labelBlock, rect.X + 4);
        Canvas.SetTop(labelBlock, rect.Y + 4);
        labelBlock.Tag = "BlurRegionOutline";
        AnnotationCanvas.Children.Add(labelBlock);
    }

    private void ShowSelection(Point start, Point end)
    {
        RemoveSelectionPreview();
        var rect = MakeRect(start, end);
        if (rect.Width < 1 || rect.Height < 1) return;
        _selectionPreview = new Rectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_selectionPreview, rect.X);
        Canvas.SetTop(_selectionPreview, rect.Y);
        AnnotationCanvas.Children.Add(_selectionPreview);
    }

    private void RemoveSelectionPreview()
    {
        if (_selectionPreview is not null)
            AnnotationCanvas.Children.Remove(_selectionPreview);
        _selectionPreview = null;
    }

    private EditorState CaptureState() => new(_pixels, _width, _height,
        AnnotationCanvas.Children.Where(e => !Equals((e as FrameworkElement)?.Tag, "BlurRegionOutline") && !ReferenceEquals(e, _selectionPreview)).ToList(),
        [.. _blurRegions]);

    private Point ClampPoint(Point point) => new(Math.Clamp(point.X, 0, _width), Math.Clamp(point.Y, 0, _height));

    private static Rect MakeRect(Point first, Point second) => new(
        Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

    private async Task ResizeSurfaceAsync()
    {
        ImageSurface.Width = SourceImage.Width = AnnotationCanvas.Width = _width;
        ImageSurface.Height = SourceImage.Height = AnnotationCanvas.Height = _height;
        ImageViewbox.UpdateLayout();
        await RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, _width, _height, 96, 96, _pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        SourceImage.Source = bitmap;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Localization.Get("Ok"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnCancel(object sender, RoutedEventArgs args) => Close();

    private enum EditMode { Crop, Draw, Highlight, Text, Blur, Redact }
    private sealed record EditorState(byte[] Pixels, uint Width, uint Height, List<UIElement> Elements, List<Rect> BlurRegions);
}
