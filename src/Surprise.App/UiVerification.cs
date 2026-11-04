using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.App;

// Offscreen layout rendering: no Show(), desktop capture, input injection or Computer Use.
internal static class UiVerification
{
    public static int Render(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new AppHost(app, Path.Combine(directory, "design-settings.json"));
        var results = new List<object>();
        var settings = new AnimationSettings { MediaKind = MediaKind.PixelArt, Files = [@"C:\Media\Пиксельная анимация — первый кадр.png", @"C:\Media\Пиксельная анимация — второй кадр.png"], Effect = RevealEffect.Rise };
        foreach (var (width, dpi) in new[] { (880, 96), (1080, 96), (880, 144) })
        {
            foreach (var (name, page, existing, dashboard) in new[] { ("media", 0, false, false), ("files", 1, true, false), ("playback", 2, true, false), ("position", 3, true, false), ("controller", 4, true, false), ("review", 5, false, false), ("dashboard", 0, true, true) })
            {
                var window = new MainWindow(host, design: true);
                window.RenderDesignPage(settings, page, existing, dashboard);
                var element = (FrameworkElement)window.Content; window.Content = null;
                var root = new Border { Child = element, Resources = window.Resources, Background = window.Background };
                System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily); System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
                int height = width == 880 ? 660 : 760;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96, dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(root);
                string path = Path.Combine(directory, $"{name}-{width}{(dpi == 96 ? "" : "-150pct")}.png");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
                var clipped = Descendants(root).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible && t.ActualHeight > 0 && t.DesiredSize.Height - t.Margin.Top - t.Margin.Bottom > t.ActualHeight + 1 && t.TextTrimming == TextTrimming.None).Select(t => t.Text).ToArray();
                var tiles = Descendants(root).OfType<Button>().Where(b => b.GetType().Name == "SquareChoice").Select(b => new { b.ActualWidth, b.ActualHeight }).ToArray();
                if (tiles.Any(t => Math.Abs(t.ActualWidth - t.ActualHeight) > 1)) throw new InvalidOperationException("Choice tile lost its square shape: " + name);
                results.Add(new { Page = name, Width = width, Height = height, Dpi = dpi, File = path, ClippedText = clipped, Tiles = tiles });
                if (clipped.Length > 0) DiagnosticLog.Warn("ui.render-clipped-text", new { Page = name, Width = width, Text = clipped });
                window.AllowExit(); window.Close();
            }
        }
        JsonFiles.Write(Path.Combine(directory, "layout-report.json"), results);
        DiagnosticLog.Info("ui.offscreen-verification", new { directory, Renders = results.Count }); return 0;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
