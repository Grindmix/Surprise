using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surprise.Core;
using Surprise.App;
using Surprise.Windows;

internal static class Program
{
    private static int count;
    private static void Check(bool b, string name) { if (!b) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args.Length > 0 ? args[0] : "."); string output = Path.Combine(root, "artifacts", "animation-qa"); Directory.CreateDirectory(output);
            var lockedSize = PlacementGeometry.Resize(600, 480, 200, 0, 1200, 900, true, 1.25);
            Check(lockedSize.Width == 800 && lockedSize.Height == 640, "Shift resize preserves aspect ratio for horizontal drag");
            var verticalSize = PlacementGeometry.Resize(600, 480, 0, 100, 1200, 900, true, 1.25);
            Check(verticalSize.Width == 725 && verticalSize.Height == 580, "Shift resize follows vertical drag without distortion");
            var boundedSize = PlacementGeometry.Resize(600, 480, 500, 500, 700, 500, true, 1.25);
            Check(boundedSize.Width == 625 && boundedSize.Height == 500, "Shift resize respects screen bounds and aspect ratio");
            var freeSize = PlacementGeometry.Resize(600, 480, 200, 0, 1200, 900, false, 1.25);
            Check(freeSize.Width == 800 && freeSize.Height == 480, "resize without Shift remains independent on both axes");
            var center = PlacementGeometry.Center(1920, 1080, 600, 480);
            Check(center == (660d, 300d), "center positions the object in the selected display");
            string logDirectory = Path.Combine(output, "logging-test"); Directory.CreateDirectory(logDirectory);
            DiagnosticLog.Initialize(logDirectory); DiagnosticLog.Info("test.structured", new { Path = "кадр.png", Frame = 2 });
            DiagnosticLog.Error("test.exception", new InvalidOperationException("outer", new IOException("inner")));
            string logText = File.ReadAllText(DiagnosticLog.FilePath!);
            Check(logText.Contains("test.structured") && logText.Contains("InvalidOperationException") && logText.Contains("IOException") && logText.Contains("outer"), "diagnostic log records structured context and nested exceptions");
            File.WriteAllText(DiagnosticLog.FilePath!, new string('x', 4 * 1024 * 1024 + 1)); DiagnosticLog.Info("test.after-rotation");
            Check(File.Exists(DiagnosticLog.FilePath + ".1") && File.ReadAllText(DiagnosticLog.FilePath!).Contains("test.after-rotation"), "diagnostic log rotates at 4 MiB");
            string badDirectory = Path.Combine(output, "not-a-directory"); File.WriteAllText(badDirectory, "test"); DiagnosticLog.Initialize(badDirectory);
            Check(DiagnosticLog.Failure is not null, "logging failure is reported without crashing");
            DiagnosticLog.Initialize(logDirectory);
            var clock = Stopwatch.StartNew();
            if (args.Length > 1 && int.TryParse(args[1], out int sampleSize))
            {
                foreach (string name in new[] { "1", "2" }) { var sample = PixelPng.Read(Path.Combine(root, "local", name + ".png"), sampleSize); Save(FrameLoader.Bitmap(sample), Path.Combine(output, "sample-" + name + ".png")); }
                return 0;
            }
            var first = PixelPng.Read(Path.Combine(root, "local", "1.png"));
            var second = PixelPng.Read(Path.Combine(root, "local", "2.png"));
            Console.WriteLine($"Local PNGs decoded in {clock.Elapsed.TotalSeconds:0.00}s: grid {first.Width}x{first.Height}, cell {first.CellSize}; {second.Width}x{second.Height}, cell {second.CellSize}");
            Check(first.SourceWidth == 30000 && first.SourceHeight == 24000, "actual 720-megapixel source decoded without full bitmap");
            Check(first.CellSize == 10 && first.Colors.Length == 7_200_000, "exact 10px grid preserves all original alpha and color boundaries");
            Check(first.SourceWidth == second.SourceWidth && first.SourceHeight == second.SourceHeight, "frame canvas sizes agree");
            Check(!first.Colors.SequenceEqual(second.Colors), "two local frames contain different artwork");
            Check(first.Colors.Any(c => c == 0) && first.Colors.Any(c => c >> 24 != 0), "local alpha includes transparent and visible cells");
            Save(FrameLoader.Bitmap(first), Path.Combine(output, "frame-1.png")); Save(FrameLoader.Bitmap(second), Path.Combine(output, "frame-2.png"));
            Check(AnimationClock.FrameAt(0, 1, 2) == 0 && AnimationClock.FrameAt(.99, 1, 2) == 0 && AnimationClock.FrameAt(1, 1, 2) == 1 && AnimationClock.FrameAt(2, 1, 2) == 0, "1 FPS sequence loops at exact boundaries");
            Check(AnimationClock.FrameAt(4, 30, 1) == 0, "single frame remains stable");
            double maxRenderMs = 0;
            foreach (RevealEffect effect in Enum.GetValues<RevealEffect>())
            {
                for (int y = 0; y < 20; y++) for (int x = 0; x < 20; x++)
                {
                    double delay = AnimationClock.Delay(effect, x, y, 20, 20, 42);
                    if (AnimationClock.Progress(0, delay) != 0 || AnimationClock.Progress(1, delay) != 1) throw new Exception("Reveal boundary " + effect);
                }
                Check(true, effect + " starts hidden and completes every cell");
            }
            // Exercise actual drawing, including intermediate flight and fully assembled frames.
            foreach (RevealEffect effect in Enum.GetValues<RevealEffect>())
            {
                var settings = new AnimationSettings { MediaKind = MediaKind.PixelArt, Effect = effect, RevealSeconds = 2 };
                var surface = new PixelSurface(settings, [first, second]) { Width = 900, Height = 800, Target = new Rect(150, 100, 600, 480) };
                surface.Measure(new Size(900, 800)); surface.Arrange(new Rect(0, 0, 900, 800));
                foreach (double time in new[] { .6, 1.4, 2.1, 3.1 })
                {
                    surface.Elapsed = time; surface.InvalidateVisual(); surface.UpdateLayout();
                    var renderTime = Stopwatch.StartNew();
                    var bitmap = new RenderTargetBitmap(900, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                    maxRenderMs = Math.Max(maxRenderMs, renderTime.Elapsed.TotalMilliseconds);
                    Save(bitmap, Path.Combine(output, $"{effect}-{time.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}.png"));
                }
            }
            Check(true, "all five reveal modes render with both actual local frames");
            Console.WriteLine($"Slowest offscreen effect/frame render: {maxRenderMs:0.0} ms");
            // PNG codec cases independent of local fixtures: semitransparent and transparent RGBA.
            byte[] bgra = [0, 0, 255, 128, 255, 0, 0, 0, 0, 255, 0, 255, 0, 0, 255, 255];
            var small = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, bgra, 8); string smallPath = Path.Combine(output, "alpha-test.png"); Save(small, smallPath);
            var decoded = PixelPng.Read(smallPath, 1);
            Check(decoded.Colors[0] == 0x80ff0000 && decoded.Colors[1] == 0 && decoded.Colors[2] == 0xff00ff00, "RGBA alpha preserved and transparent RGB normalized");
            byte[] damaged = File.ReadAllBytes(smallPath); damaged[^5] ^= 1; string bad = Path.Combine(output, "damaged.png"); File.WriteAllBytes(bad, damaged);
            try { PixelPng.Read(bad); throw new Exception("CRC accepted corrupted PNG"); } catch (InvalidDataException) { Check(true, "damaged PNG is rejected"); }
            var config = new AnimationSettings { MediaKind = MediaKind.PixelArt, Files = [Path.Combine(root, "local", "1.png"), Path.Combine(root, "local", "2.png")] };
            config.Validate(Path.Combine(output, "settings.json"));
            Check(config.PlayerConfig(Path.Combine(output, "settings.json")).OneShot == false, "new app never enables one-shot");
            config.Files.Add(Path.Combine(root, "missing.png"));
            try { config.Validate(Path.Combine(output, "settings.json")); throw new Exception("missing file accepted"); } catch (FileNotFoundException) { Check(true, "missing media produces actionable validation error"); }
            if (args.Contains("--live"))
            {
                Exception? failure = null; var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Startup += async (_, _) =>
                {
                    try
                    {
                        foreach (RevealEffect effect in new[] { RevealEffect.None, RevealEffect.Rise })
                        {
                            var live = new PixelOverlay(new AnimationSettings { Effect = effect, RevealSeconds = .5, DurationSeconds = 2.2, FramesPerSecond = 2 }, [first, second]);
                            var done = new TaskCompletionSource(); var elapsed = new Stopwatch();
                            live.Ready += () => elapsed.Start(); live.Closed += (_, _) => done.TrySetResult(); live.Show();
                            await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
                            double expected = effect == RevealEffect.None ? 2.2 : 2.7;
                            Check(elapsed.Elapsed.TotalSeconds >= expected - .05 && elapsed.Elapsed.TotalSeconds < expected + 1, $"live {effect} overlay closes after {expected}s including reveal; actual {elapsed.Elapsed.TotalSeconds:0.00}s");
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { app.Shutdown(); }
                };
                app.Run(); if (failure is not null) throw failure;
            }
            Console.WriteLine($"{count} passed. Peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    private static void Save(BitmapSource source, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var stream = File.Create(path); encoder.Save(stream); }
}
