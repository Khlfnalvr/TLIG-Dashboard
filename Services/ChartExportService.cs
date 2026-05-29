using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace TLIGDashboard.Services;

internal sealed record ChartExportOptions(
    int Width,
    int Height,
    string Format,
    int? StartSec = null,
    int? EndSec = null);

internal static class ChartExportService
{
    public static async Task SaveChartFlowAsync(
        XamlRoot root,
        FrameworkElement renderElement,
        Canvas mainCanvas,
        string defaultName,
        DateTime[] timestamps,
        Func<ChartExportOptions, Task> applyTrimAsync,
        Func<Task> restoreTrimAsync,
        Action<Action> subscribeHistoryUpdated,
        Action<Action> unsubscribeHistoryUpdated,
        DispatcherQueue dispatcherQueue)
    {
        var opts = await ShowExportDialogAsync(
            root,
            renderElement,
            timestamps,
            subscribeHistoryUpdated,
            unsubscribeHistoryUpdated,
            dispatcherQueue);
        if (opts is null) return;

        try
        {
            await applyTrimAsync(opts);

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            switch (opts.Format)
            {
                case "png": picker.FileTypeChoices.Add(LocalizationManager.Instance.Get("Export_FileTypePng"), new[] { ".png" }); break;
                case "jpg": picker.FileTypeChoices.Add(LocalizationManager.Instance.Get("Export_FileTypeJpeg"), new[] { ".jpg" }); break;
                case "svg": picker.FileTypeChoices.Add(LocalizationManager.Instance.Get("Export_FileTypeSvg"), new[] { ".svg" }); break;
            }

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            try
            {
                if (opts.Format == "svg")
                    await SaveAsSvg(mainCanvas, file, opts.Width, opts.Height);
                else
                    await SaveAsRaster(renderElement, file, opts, opts.Format == "jpg");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save failed: {ex}");
                var err = new ContentDialog
                {
                    Title = LocalizationManager.Instance.Get("Ui_SaveFailed"),
                    Content = ex.Message,
                    CloseButtonText = LocalizationManager.Instance.Get("Ui_Ok"),
                    XamlRoot = root
                };
                try { await err.ShowAsync(); } catch { }
            }
        }
        finally
        {
            await restoreTrimAsync();
        }
    }

    private static async Task<ChartExportOptions?> ShowExportDialogAsync(
        XamlRoot root,
        FrameworkElement previewElement,
        DateTime[] timestamps,
        Action<Action> subscribeHistoryUpdated,
        Action<Action> unsubscribeHistoryUpdated,
        DispatcherQueue dispatcherQueue)
    {
        var lang = LocalizationManager.Instance;
        var totalSec = timestamps.Length > 1
            ? (int)(timestamps[^1] - timestamps[0]).TotalSeconds
            : 0;

        double trimStart = 0;
        double trimEnd = totalSec;
        bool draggingStart = false;
        bool draggingEnd = false;

        var aspect = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        aspect.Items.Add(new ComboBoxItem { Content = lang.Get("Export_Aspect43"), Tag = "1.3333" });
        aspect.Items.Add(new ComboBoxItem { Content = lang.Get("Export_Aspect32"), Tag = "1.5" });
        aspect.Items.Add(new ComboBoxItem { Content = lang.Get("Export_Aspect169"), Tag = "1.7778" });
        aspect.Items.Add(new ComboBoxItem { Content = lang.Get("Export_AspectGolden"), Tag = "1.6180" });
        aspect.Items.Add(new ComboBoxItem { Content = lang.Get("Export_Aspect11"), Tag = "1.0" });
        aspect.Items.Add(new ComboBoxItem { Content = lang.Get("Export_AspectCustom"), Tag = "0" });

        var widthBox = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = 800,
            Minimum = 200,
            Maximum = 4000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 50,
            LargeChange = 100
        };
        var heightBox = new NumberBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = 600,
            Minimum = 150,
            Maximum = 4000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 25,
            LargeChange = 100,
            IsEnabled = false
        };

        var format = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        format.Items.Add(new ComboBoxItem { Content = lang.Get("Export_FormatPng"), Tag = "png" });
        format.Items.Add(new ComboBoxItem { Content = lang.Get("Export_FormatJpg"), Tag = "jpg" });
        format.Items.Add(new ComboBoxItem { Content = lang.Get("Export_FormatSvg"), Tag = "svg" });

        var previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxHeight = 200,
            Margin = new Thickness(0, 6, 0, 8)
        };

        var trimCanvas = new Canvas { Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        var trimTrack = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorTertiaryBrush"],
            Height = 8,
            CornerRadius = new CornerRadius(4)
        };
        Canvas.SetTop(trimTrack, 13);
        trimCanvas.Children.Add(trimTrack);

        var trimSelection = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0.45,
            Height = 8,
            CornerRadius = new CornerRadius(4)
        };
        Canvas.SetTop(trimSelection, 13);
        trimCanvas.Children.Add(trimSelection);

        var startThumb = new Border
        {
            Width = 14,
            Height = 30,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"],
            BorderThickness = new Thickness(1),
        };
        Canvas.SetTop(startThumb, 2);
        trimCanvas.Children.Add(startThumb);

        var endThumb = new Border
        {
            Width = 14,
            Height = 30,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"],
            BorderThickness = new Thickness(1),
        };
        Canvas.SetTop(endThumb, 2);
        trimCanvas.Children.Add(endThumb);

        var trimLabel = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Opacity = 0.6
        };

        void UpdateTrimDisplay()
        {
            double w = trimCanvas.ActualWidth;
            if (w <= 0) return;

            trimTrack.Width = w;
            Canvas.SetLeft(trimTrack, 0);

            double startX = totalSec > 0 ? (trimStart / totalSec) * w : 0;
            double endX = totalSec > 0 ? (trimEnd / totalSec) * w : w;

            Canvas.SetLeft(startThumb, startX - startThumb.Width / 2);
            Canvas.SetLeft(endThumb, endX - endThumb.Width / 2);
            Canvas.SetLeft(trimSelection, startX);
            trimSelection.Width = Math.Max(0, endX - startX);

            trimLabel.Text = $"{(int)trimStart:N0}s - {(int)trimEnd:N0}s  ({(int)(trimEnd - trimStart)}s)";
        }

        trimCanvas.SizeChanged += (_, _) => UpdateTrimDisplay();

        CancellationTokenSource? previewCts = null;
        async void SchedulePreviewUpdate()
        {
            previewCts?.Cancel();
            previewCts = new CancellationTokenSource();
            var token = previewCts.Token;
            try
            {
                await Task.Delay(250, token);
                if (!token.IsCancellationRequested)
                {
                    var src = await RenderPreviewAsync(
                        previewElement,
                        Math.Min(420, (int)widthBox.Value),
                        Math.Min(280, (int)heightBox.Value));
                    if (!token.IsCancellationRequested && src is not null)
                        previewImage.Source = src;
                }
            }
            catch { }
        }

        startThumb.PointerPressed += (_, e) =>
        {
            draggingStart = true;
            startThumb.CapturePointer(e.Pointer);
        };
        endThumb.PointerPressed += (_, e) =>
        {
            draggingEnd = true;
            endThumb.CapturePointer(e.Pointer);
        };

        var trimMoved = new PointerEventHandler((_, e) =>
        {
            if (!draggingStart && !draggingEnd) return;
            double w = trimCanvas.ActualWidth;
            if (w <= 0 || totalSec <= 0) return;

            double x = Math.Clamp(e.GetCurrentPoint(trimCanvas).Position.X, 0, w);
            double sec = (x / w) * totalSec;

            if (draggingStart)
            {
                if (sec >= trimEnd) sec = trimEnd - 1;
                if (sec < 0) sec = 0;
                trimStart = sec;
            }
            else if (draggingEnd)
            {
                if (sec <= trimStart) sec = trimStart + 1;
                if (sec > totalSec) sec = totalSec;
                trimEnd = sec;
            }

            UpdateTrimDisplay();
            SchedulePreviewUpdate();
        });

        var trimReleased = new PointerEventHandler((_, e) =>
        {
            if (draggingStart)
            {
                startThumb.ReleasePointerCapture(e.Pointer);
                draggingStart = false;
            }
            if (draggingEnd)
            {
                endThumb.ReleasePointerCapture(e.Pointer);
                draggingEnd = false;
            }
            UpdateTrimDisplay();
        });

        startThumb.PointerMoved += trimMoved;
        endThumb.PointerMoved += trimMoved;
        startThumb.PointerReleased += trimReleased;
        endThumb.PointerReleased += trimReleased;
        startThumb.PointerCaptureLost += trimReleased;
        endThumb.PointerCaptureLost += trimReleased;

        bool internalUpdate = false;

        double GetAspect()
        {
            if (aspect.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                return a;
            return 0;
        }

        void RecomputeHeight()
        {
            if (internalUpdate) return;
            double a = GetAspect();
            if (a > 0)
            {
                internalUpdate = true;
                heightBox.Value = Math.Round(widthBox.Value / a);
                internalUpdate = false;
            }
        }

        void OnDataUpdated() => SchedulePreviewUpdate();
        subscribeHistoryUpdated(OnDataUpdated);

        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            dispatcherQueue.TryEnqueue(SchedulePreviewUpdate);
        });

        aspect.SelectionChanged += (_, _) =>
        {
            double a = GetAspect();
            heightBox.IsEnabled = a <= 0;
            RecomputeHeight();
            SchedulePreviewUpdate();
        };
        widthBox.ValueChanged += (_, _) =>
        {
            RecomputeHeight();
            SchedulePreviewUpdate();
        };
        heightBox.ValueChanged += (_, _) => SchedulePreviewUpdate();

        TextBlock FieldLabel(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 4)
        };

        TextBlock SectionHeader(string text) => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.9,
            Margin = new Thickness(0, 4, 0, 6)
        };

        Border Divider() => new()
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 10, 0, 6)
        };

        var panel = new StackPanel { Spacing = 0, MinWidth = 500 };

        panel.Children.Add(SectionHeader(lang.Get("Export_PreviewLive")));
        panel.Children.Add(previewImage);

        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader(lang.Get("Export_TimeRange")));
        panel.Children.Add(new TextBlock
        {
            Text = lang.Format("Export_TimeRangeHint", totalSec, totalSec / 60.0),
            FontSize = 11,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(trimCanvas);
        panel.Children.Add(trimLabel);

        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader(lang.Get("Export_Dimensions")));
        panel.Children.Add(FieldLabel(lang.Get("Export_AspectRatio")));
        panel.Children.Add(aspect);

        var sizeGrid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sizeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var wLab = FieldLabel(lang.Get("Export_WidthPx"));
        Grid.SetColumn(wLab, 0);
        Grid.SetRow(wLab, 0);
        var hLab = FieldLabel(lang.Get("Export_HeightPx"));
        Grid.SetColumn(hLab, 1);
        Grid.SetRow(hLab, 0);
        Grid.SetColumn(widthBox, 0);
        Grid.SetRow(widthBox, 1);
        Grid.SetColumn(heightBox, 1);
        Grid.SetRow(heightBox, 1);
        sizeGrid.Children.Add(wLab);
        sizeGrid.Children.Add(hLab);
        sizeGrid.Children.Add(widthBox);
        sizeGrid.Children.Add(heightBox);
        panel.Children.Add(sizeGrid);

        panel.Children.Add(Divider());
        panel.Children.Add(SectionHeader(lang.Get("Export_FileFormat")));
        panel.Children.Add(format);

        var dialog = new ContentDialog
        {
            Title = lang.Get("Export_Title"),
            Content = panel,
            PrimaryButtonText = lang.Get("Ui_SaveEllipsis"),
            CloseButtonText = lang.Get("Ui_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        var result = await dialog.ShowAsync();
        unsubscribeHistoryUpdated(OnDataUpdated);

        if (result != ContentDialogResult.Primary) return null;

        string fmt = "png";
        if (format.SelectedItem is ComboBoxItem fi && fi.Tag is string ft) fmt = ft;

        return new ChartExportOptions(
            (int)widthBox.Value,
            (int)heightBox.Value,
            fmt,
            StartSec: trimStart > 0 ? (int)trimStart : null,
            EndSec: trimEnd < totalSec ? (int)trimEnd : null);
    }

    private static async Task<ImageSource?> RenderPreviewAsync(
        FrameworkElement element,
        int maxW,
        int maxH)
    {
        try
        {
            double srcW = element.ActualWidth;
            double srcH = element.ActualHeight;
            if (srcW <= 0 || srcH <= 0) return null;

            double scale = Math.Min(maxW / srcW, maxH / srcH);
            int w = Math.Max(1, (int)(srcW * scale));
            int h = Math.Max(1, (int)(srcH * scale));

            var rt = new RenderTargetBitmap();
            await rt.RenderAsync(element, w, h);

            var pixels = await rt.GetPixelsAsync();
            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                rt.PixelWidth,
                rt.PixelHeight,
                BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixels);

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveAsRaster(
        FrameworkElement renderElement,
        StorageFile file,
        ChartExportOptions opts,
        bool jpeg)
    {
        var rt = new RenderTargetBitmap();
        await rt.RenderAsync(renderElement, opts.Width, opts.Height);

        var pixels = await rt.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        DataReader.FromBuffer(pixels).ReadBytes(bytes);

        var encoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;

        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rt.PixelWidth,
            (uint)rt.PixelHeight,
            96,
            96,
            bytes);
        await encoder.FlushAsync();
    }

    private static async Task SaveAsSvg(Canvas canvas, StorageFile file, int width, int height)
    {
        double srcW = canvas.ActualWidth;
        double srcH = canvas.ActualHeight;
        if (srcW <= 0 || srcH <= 0)
            throw new InvalidOperationException("Chart canvas has no size.");

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                      $"width=\"{width}\" height=\"{height}\" " +
                      $"viewBox=\"0 0 {srcW.ToString("0.##", inv)} {srcH.ToString("0.##", inv)}\">");

        foreach (var child in canvas.Children)
        {
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible)
                continue;

            switch (child)
            {
                case Line l:
                    sb.AppendLine(SvgLine(l, inv));
                    break;
                case Polyline pl:
                    sb.AppendLine(SvgPolyline(pl, inv));
                    break;
                case Polygon pg:
                    sb.AppendLine(SvgPolygon(pg, inv));
                    break;
                case TextBlock tb:
                    sb.AppendLine(SvgText(tb, inv));
                    break;
            }
        }

        sb.AppendLine("</svg>");

        await FileIO.WriteTextAsync(file, sb.ToString());
    }

    private static string SvgLine(Line l, CultureInfo inv)
    {
        string stroke = BrushToHex(l.Stroke);
        string dashAttr = l.StrokeDashArray is { Count: > 0 } da
            ? $" stroke-dasharray=\"{string.Join(",", da.Select(d => d.ToString("0.##", inv)))}\""
            : "";
        return $"<line x1=\"{l.X1.ToString("0.##", inv)}\" y1=\"{l.Y1.ToString("0.##", inv)}\" " +
               $"x2=\"{l.X2.ToString("0.##", inv)}\" y2=\"{l.Y2.ToString("0.##", inv)}\" " +
               $"stroke=\"{stroke}\" stroke-width=\"{l.StrokeThickness.ToString("0.##", inv)}\"{dashAttr}/>";
    }

    private static string SvgPolyline(Polyline p, CultureInfo inv)
    {
        if (p.Points.Count == 0) return "";
        string points = string.Join(" ",
            p.Points.Select(pt => $"{pt.X.ToString("0.##", inv)},{pt.Y.ToString("0.##", inv)}"));
        string stroke = BrushToHex(p.Stroke);
        string fill = p.Fill is null ? "none" : BrushToHex(p.Fill);
        string dashAttr = p.StrokeDashArray is { Count: > 0 } da
            ? $" stroke-dasharray=\"{string.Join(",", da.Select(d => d.ToString("0.##", inv)))}\""
            : "";
        return $"<polyline points=\"{points}\" stroke=\"{stroke}\" fill=\"{fill}\" " +
               $"stroke-width=\"{p.StrokeThickness.ToString("0.##", inv)}\"{dashAttr} stroke-linejoin=\"round\"/>";
    }

    private static string SvgPolygon(Polygon p, CultureInfo inv)
    {
        if (p.Points.Count == 0) return "";
        string points = string.Join(" ",
            p.Points.Select(pt => $"{pt.X.ToString("0.##", inv)},{pt.Y.ToString("0.##", inv)}"));
        string fill = BrushToHex(p.Fill);
        string fo = p.Opacity.ToString("0.##", inv);
        return $"<polygon points=\"{points}\" fill=\"{fill}\" fill-opacity=\"{fo}\"/>";
    }

    private static string SvgText(TextBlock tb, CultureInfo inv)
    {
        double x = double.IsNaN(Canvas.GetLeft(tb)) ? 0 : Canvas.GetLeft(tb);
        double y = double.IsNaN(Canvas.GetTop(tb)) ? 0 : Canvas.GetTop(tb);
        y += tb.FontSize * 0.85;
        string color = BrushToHex(tb.Foreground);
        string fo = tb.Opacity.ToString("0.##", inv);
        string family = tb.FontFamily?.Source ?? "sans-serif";
        return $"<text x=\"{x.ToString("0.##", inv)}\" y=\"{y.ToString("0.##", inv)}\" " +
               $"font-family=\"{family}\" font-size=\"{tb.FontSize.ToString("0.##", inv)}\" " +
               $"fill=\"{color}\" fill-opacity=\"{fo}\">{XmlEscape(tb.Text)}</text>";
    }

    private static string BrushToHex(Brush? b)
    {
        if (b is SolidColorBrush scb)
            return $"#{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}";
        return "#808080";
    }

    private static string XmlEscape(string? s) =>
        (s ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
