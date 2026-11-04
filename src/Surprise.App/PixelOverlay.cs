using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Surprise.Core;
using Forms = System.Windows.Forms;

namespace Surprise.App;

internal static class FrameLoader
{
    private static readonly Dictionary<string, PixelFrame> cache = new();
    public static Task<List<PixelFrame>> LoadAsync(AnimationSettings config, string settingsPath, CancellationToken token) => Task.Run(() =>
    {
        var frames = new List<PixelFrame>(); long pixels = 0;
        foreach (string value in config.Files)
        {
            token.ThrowIfCancellationRequested(); string path = config.Resolve(value, settingsPath); var info = new FileInfo(path);
            string key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            PixelFrame? frame; lock (cache) cache.TryGetValue(key, out frame);
            frame ??= PixelPng.Read(path, cancellation: token);
            pixels += frame.Colors.Length;
            if (pixels > 32_000_000) throw new InvalidDataException("Слишком много данных во всех кадрах. Уменьши исходные PNG при экспорте или сократи число кадров.");
            if (frames.Count > 0 && (frame.SourceWidth != frames[0].SourceWidth || frame.SourceHeight != frames[0].SourceHeight)) throw new InvalidDataException("У всех PNG-кадров должны совпадать размеры холста.");
            lock (cache) { if (cache.Values.Sum(f => (long)f.Colors.Length) + frame.Colors.Length > 32_000_000) cache.Clear(); cache[key] = frame; }
            frames.Add(frame);
        }
        return frames;
    }, token);
    public static BitmapSource Bitmap(PixelFrame frame)
    {
        var bytes = new byte[frame.Colors.Length * 4];
        for (int i = 0; i < frame.Colors.Length; i++) { uint c = frame.Colors[i]; bytes[i * 4] = (byte)c; bytes[i * 4 + 1] = (byte)(c >> 8); bytes[i * 4 + 2] = (byte)(c >> 16); bytes[i * 4 + 3] = (byte)(c >> 24); }
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, bytes, frame.Width * 4); bitmap.Freeze(); return bitmap;
    }
}

internal sealed class PixelSurface : FrameworkElement
{
    private readonly AnimationSettings settings;
    private readonly List<PixelFrame> frames;
    private readonly BitmapSource[] bitmaps;
    private WriteableBitmap? revealBitmap;
    private uint[] canvas = [], texture = [];
    private int rasterWidth, rasterHeight, textureWidth, textureHeight, originX, originY, tileSize;
    private Rect rasterRegion;
    private readonly uint seed = (uint)Random.Shared.Next();
    public double Elapsed { get; set; }
    public Rect Target { get; set; }
    public double RevealDuration => settings.Effect == RevealEffect.None ? 0 : settings.RevealSeconds;
    public PixelSurface(AnimationSettings settings, List<PixelFrame> frames)
    {
        this.settings = settings; this.frames = frames; bitmaps = frames.Select(FrameLoader.Bitmap).ToArray();
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (Target.IsEmpty) return;
        var first = frames[0]; double scale = Math.Min(Target.Width / first.SourceWidth, Target.Height / first.SourceHeight);
        double w = first.SourceWidth * scale, h = first.SourceHeight * scale;
        var region = new Rect(Target.X + (Target.Width - w) / 2, Target.Y + (Target.Height - h) / 2, w, h);
        if (Elapsed >= RevealDuration)
        {
            int index = AnimationClock.FrameAt(Elapsed - RevealDuration, settings.FramesPerSecond, frames.Count);
            dc.DrawImage(bitmaps[index], region); return;
        }
        PrepareRaster(region);
        Array.Clear(canvas); double elapsed = Elapsed / RevealDuration;
        for (int y = 0; y < textureHeight; y += tileSize) for (int x = 0; x < textureWidth; x += tileSize)
        {
            double delay = AnimationClock.Delay(settings.Effect, x, y, textureWidth, textureHeight, seed);
            double p = AnimationClock.Progress(elapsed, delay); if (p <= 0) continue;
            double ease = 1 - Math.Pow(1 - p, 3), dx = 0, dy = 0;
            if (settings.Effect == RevealEffect.Scatter)
            {
                double angle = AnimationClock.Delay(RevealEffect.Random, x + 251, y + 113, textureWidth, textureHeight, seed) / .7 * Math.PI * 2;
                dx = Math.Cos(angle) * textureWidth * .7 * (1 - ease); dy = Math.Sin(angle) * textureHeight * .7 * (1 - ease);
            }
            else if (settings.Effect == RevealEffect.Rise)
            {
                dy = (textureHeight * 1.45 - y) * (1 - ease);
                dx = (delay / .7 - .5) * textureWidth * .18 * (1 - ease);
            }
            int opacity = (int)Math.Round(255 * (settings.Effect is RevealEffect.Scatter or RevealEffect.Rise ? p : Math.Min(1, p * 5)));
            Blit(x, y, Math.Min(tileSize, textureWidth - x), Math.Min(tileSize, textureHeight - y), originX + x + (int)Math.Round(dx), originY + y + (int)Math.Round(dy), opacity);
        }
        revealBitmap!.WritePixels(new Int32Rect(0, 0, rasterWidth, rasterHeight), canvas, rasterWidth * 4, 0);
        dc.DrawImage(revealBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }
    private void PrepareRaster(Rect region)
    {
        double ratio = Math.Min(1, Math.Min(2048 / Math.Max(1, ActualWidth), 2048 / Math.Max(1, ActualHeight)));
        int rw = Math.Max(1, (int)Math.Ceiling(ActualWidth * ratio)), rh = Math.Max(1, (int)Math.Ceiling(ActualHeight * ratio));
        if (revealBitmap is not null && rasterWidth == rw && rasterHeight == rh && rasterRegion == region) return;
        rasterWidth = rw; rasterHeight = rh; rasterRegion = region; canvas = new uint[rw * rh];
        revealBitmap = new WriteableBitmap(rw, rh, 96, 96, PixelFormats.Pbgra32, null);
        textureWidth = Math.Max(1, (int)Math.Round(region.Width * ratio)); textureHeight = Math.Max(1, (int)Math.Round(region.Height * ratio));
        originX = (int)Math.Round(region.X * ratio); originY = (int)Math.Round(region.Y * ratio); texture = new uint[textureWidth * textureHeight];
        var frame = frames[0];
        for (int y = 0; y < textureHeight; y++) for (int x = 0; x < textureWidth; x++)
        {
            uint c = frame.Colors[Math.Min(frame.Height - 1, (int)((y + .5) * frame.Height / textureHeight)) * frame.Width + Math.Min(frame.Width - 1, (int)((x + .5) * frame.Width / textureWidth))];
            uint a = c >> 24; texture[y * textureWidth + x] = (a << 24) | ((((c >> 16) & 255) * a + 127) / 255 << 16) | ((((c >> 8) & 255) * a + 127) / 255 << 8) | (((c & 255) * a + 127) / 255);
        }
        tileSize = settings.CellSize > 0 ? Math.Max(1, (int)Math.Round(settings.CellSize * textureWidth / (double)frame.SourceWidth)) : Math.Max(1, (int)Math.Round(5 * ratio));
    }
    private void Blit(int sx, int sy, int width, int height, int dx, int dy, int opacity)
    {
        for (int y = 0; y < height; y++)
        {
            int targetY = dy + y; if (targetY < 0 || targetY >= rasterHeight) continue;
            for (int x = 0; x < width; x++)
            {
                int targetX = dx + x; if (targetX < 0 || targetX >= rasterWidth) continue;
                uint color = texture[(sy + y) * textureWidth + sx + x]; if (color >> 24 == 0) continue;
                int a = (int)(color >> 24) * opacity / 255, r = (int)((color >> 16) & 255) * opacity / 255, g = (int)((color >> 8) & 255) * opacity / 255, b = (int)(color & 255) * opacity / 255;
                int at = targetY * rasterWidth + targetX; uint old = canvas[at]; int inverse = 255 - a;
                a += (int)(old >> 24) * inverse / 255; r += (int)((old >> 16) & 255) * inverse / 255; g += (int)((old >> 8) & 255) * inverse / 255; b += (int)(old & 255) * inverse / 255;
                canvas[at] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }
    }
}

internal sealed class PixelOverlay : Window
{
    private readonly PixelSurface surface;
    private readonly AnimationSettings settings;
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int lastFrame = -1;
    public event Action? Ready;
    public PixelOverlay(AnimationSettings settings, List<PixelFrame> frames)
    {
        this.settings = settings; surface = new(settings, frames); Content = surface;
        Title = "Surprise — пиксельарт"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Loaded += (_, _) =>
        {
            var screen = Display(settings.Monitor); var bounds = screen.Bounds;
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x80 | 0x20));
            SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x10);
            var dpi = VisualTreeHelper.GetDpi(this); var target = Placement(settings, bounds.Width, bounds.Height);
            surface.Target = new Rect(target.X / dpi.DpiScaleX, target.Y / dpi.DpiScaleY, target.Width / dpi.DpiScaleX, target.Height / dpi.DpiScaleY);
            clock.Start(); timer.Start(); surface.InvalidateVisual(); Ready?.Invoke();
        };
        timer.Tick += (_, _) =>
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= surface.RevealDuration + settings.DurationSeconds) { Close(); return; }
            int frame = AnimationClock.FrameAt(t - surface.RevealDuration, settings.FramesPerSecond, frames.Count);
            if (t <= surface.RevealDuration + .05 || frame != lastFrame) { surface.Elapsed = t; surface.InvalidateVisual(); lastFrame = frame; }
        };
        Closed += (_, _) => timer.Stop(); KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    public static Forms.Screen Display(string name) => Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    public static OverlayRectangle Placement(AnimationSettings s, int width, int height) => OverlayPlacement.Resolve(new SurpriseConfig { OverlayWidth = s.Width, OverlayHeight = s.Height, OverlayX = s.X, OverlayY = s.Y, OverlayAnchor = OverlayAnchor.Custom }, width, height);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
