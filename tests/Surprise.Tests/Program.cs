using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using Surprise.Core;
using Surprise.Windows;
using Surprise.Setup;

namespace Surprise.Tests;

internal static class Program
{
    private static int passed, failed;
    private static readonly ControllerIdentity A = new("wgi:A", "Controller A", "WGI", 1, 2);
    private static readonly ControllerIdentity B = new("wgi:B", "Controller B", "WGI", 1, 2);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--portable-runtime") return PortableChecks.PublishedLaunch(args[1]);
        Test("one-page portable creator requires media and preserves portable choices", PortableUiTests.Run);
        Test("startup presence never fires", () => { var state = New(); Check(!state.Observe([A], Now, true, false, false)); });
        Test("new connection fires", () => { var state = New(); state.Baseline([]); Check(state.Observe([A], Now, true, false, false)); });
        Test("same connection does not repeat", () => { var state = New(); state.Baseline([]); Check(state.Observe([A], Now, true, false, false)); Check(!state.Observe([A], Now.AddMinutes(1), true, false, false)); });
        Test("disconnect reconnect rearms", () => { var state = New(); state.Baseline([A]); Check(!state.Observe([], Now, true, false, false)); Check(state.Observe([A], Now.AddSeconds(1), true, false, false)); });
        Test("selected device ignores identical model", () => { var state = new TriggerState(new() { Trigger = TriggerKind.SelectedController, Controllers = [A] }); state.Baseline([]); Check(!state.Observe([B], Now, true, false, false)); Check(state.Observe([A, B], Now.AddSeconds(1), true, false, false)); });
        Test("model matches on another PC despite changed ID and name", () => { var state = new TriggerState(new() { Trigger = TriggerKind.ControllerModel, Controllers = [A] }); state.Baseline([]); Check(state.Observe([B with { Name = "Different driver name" }], Now, true, false, false)); });
        Test("model ignores other vendor product and fallback", () => { var state = new TriggerState(new() { Trigger = TriggerKind.ControllerModel, Controllers = [A] }); state.Baseline([]); Check(!state.Observe([B with { VendorId = 3 }, B with { Id = "wgi:C", ProductId = 4 }, B with { Id = "xinput:0", Source = "XInput" }], Now, true, false, false)); });
        Test("model supports separate USB and Bluetooth IDs", () => { var bluetooth = A with { Id = "wgi:bt", ProductId = 5 }; var state = new TriggerState(new() { Trigger = TriggerKind.ControllerModel, Controllers = [A, bluetooth] }); state.Baseline([]); Check(state.Observe([bluetooth with { Id = "wgi:other-pc" }], Now, true, false, false)); });
        Test("model startup presence never fires", () => { var state = new TriggerState(new() { Trigger = TriggerKind.ControllerModel, Controllers = [A] }); Check(!state.Observe([B], Now, true, false, false)); });
        Test("unknown model IDs cannot match", () => Check(!new ControllerIdentity("a", "Unknown", "WGI").MatchesModel(new("b", "Unknown", "WGI"))));
        Test("locked arrivals are dropped", () => { var state = New(); state.Baseline([]); Check(!state.Observe([A], Now, false, false, false)); Check(!state.Observe([A], Now.AddMinutes(1), true, false, false)); });
        Test("playback arrivals are dropped", () => { var state = New(); state.Baseline([]); Check(!state.Observe([A], Now, true, true, false)); Check(!state.Observe([A], Now.AddMinutes(1), true, false, false)); });
        Test("paused arrivals are dropped", () => { var state = New(); state.Baseline([]); Check(!state.Observe([A], Now, true, false, true)); Check(!state.Observe([A], Now.AddMinutes(1), true, false, false)); });
        Test("cooldown drops arrivals without queuing", () => { var state = New(); state.Baseline([]); state.PlaybackStarted(Now); Check(!state.Observe([A], Now.AddSeconds(2), true, false, false)); Check(!state.Observe([A], Now.AddMinutes(1), true, false, false)); });
        Test("one shot persists consumed until rearmed", () => { var state = new TriggerState(new() { OneShot = true, CooldownSeconds = 0 }); state.Baseline([]); Check(state.Observe([A], Now, true, false, false)); Check(!state.Consumed); state.PlaybackStarted(Now); Check(state.Consumed); state.Baseline([]); Check(!state.Observe([A], Now.AddMinutes(1), true, false, false)); state.Consumed = false; state.Baseline([]); Check(state.Observe([A], Now.AddMinutes(2), true, false, false)); });
        Test("resume baseline does not fire", () => { var state = New(); state.Baseline([]); state.Baseline([A]); Check(!state.Observe([A], Now, true, false, false)); });
        Test("failed playback does not consume one shot", () => { var state = new TriggerState(new() { OneShot = true }); state.Baseline([]); Check(state.Observe([A], Now, true, false, false)); Check(!state.Consumed); });
        Test("XInput promotion to WGI does not duplicate any trigger", () => { var state = New(); state.Baseline([new("xinput-fallback:0", "Fallback", "XInput")]); Check(!state.Observe([A], Now, true, false, false)); });
        Test("connected controller can be recorded immediately", () => { var state = new ControllerLearningState(); state.Observe([A]); Check(state.CanRecord(A)); state.Record(A); Check(state.Profiles.Count == 1 && state.Verified.Count == 0); });
        Test("initial presence never verifies a recorded controller", () => { var state = new ControllerLearningState(); state.Observe([A]); state.Record(A); state.Observe([A]); state.Observe([A]); Check(state.Verified.Count == 0); });
        Test("one stable reconnect verifies the profile", () => { var state = new ControllerLearningState(); state.Observe([A]); state.Record(A); state.Observe([]); Check(state.IsMissing(A.Id)); state.Observe([A]); Check(state.Verified.Count == 0); state.Observe([A]); Check(state.Verified.Contains(A.Id)); });
        Test("another same-model device cannot verify the profile", () => { var state = new ControllerLearningState(); state.Observe([A]); state.Record(A); state.Observe([]); state.Observe([B]); state.Observe([B]); Check(state.Verified.Count == 0 && state.IsMissing(A.Id)); });
        Test("duplicate and fallback recordings are rejected", () => { var state = new ControllerLearningState(); var fallback = new ControllerIdentity("xinput-fallback:0", "Fallback", "XInput"); state.Observe([A, fallback]); state.Record(A); Check(!state.CanRecord(A) && !state.CanRecord(fallback)); Throws<InvalidOperationException>(() => state.Record(fallback)); });
        Test("restarting detection cannot fabricate verification", () => { var state = new ControllerLearningState(); state.Observe([A]); state.Record(A); state.Observe([]); state.ResetObservation(); state.Observe([A]); state.Observe([A]); Check(state.Verified.Count == 0); });
        Test("old configs default to standard playback", () => Check(JsonSerializer.Deserialize<SurpriseConfig>("{\"SchemaVersion\":1}", JsonFiles.Options)!.PlaybackMode == PlaybackMode.Standard));
        Test("overlay corner and centre positions", () => { var c = new SurpriseConfig { OverlayWidth = 320, OverlayHeight = 200 }; Check(OverlayPlacement.Resolve(c, 1920, 1080) == new OverlayRectangle(1576, 856, 320, 200)); c.OverlayAnchor = OverlayAnchor.Center; Check(OverlayPlacement.Resolve(c, 1920, 1080) == new OverlayRectangle(800, 440, 320, 200)); });
        Test("overlay clamps after display resolution changes", () => { var c = new SurpriseConfig { OverlayAnchor = OverlayAnchor.Custom, OverlayX = 3000, OverlayY = 3000, OverlayWidth = 1000, OverlayHeight = 900 }; Check(OverlayPlacement.Resolve(c, 640, 480) == new OverlayRectangle(0, 0, 640, 480)); });

        string root = Path.Combine(Path.GetTempPath(), "Surprise-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Test("relative paths remain under root", () => Check(SafePaths.ResolveUnder(root, "media/surprise.gif").StartsWith(root, StringComparison.OrdinalIgnoreCase)));
            Test("parent traversal rejected", () => Throws<InvalidDataException>(() => SafePaths.ResolveUnder(root, "../outside.txt")));
            Test("absolute path rejected", () => Throws<InvalidDataException>(() => SafePaths.ResolveUnder(root, Path.Combine(root, "absolute.txt"))));
            Test("prefix sibling escape rejected", () => Throws<InvalidDataException>(() => SafePaths.ResolveUnder(root, "../" + Path.GetFileName(root) + "-other/file.txt")));
            string media = Path.Combine(root, "file.gif"); File.WriteAllBytes(media, [71, 73, 70]);
            Test("config JSON roundtrip preserves enums/profiles", () => { var config = new SurpriseConfig { MediaPath = media, Trigger = TriggerKind.SelectedController, Controllers = [A] }; string file = Path.Combine(root, "config.json"); JsonFiles.Write(file, config); var loaded = JsonFiles.Read<SurpriseConfig>(file); loaded.Validate(file); Check(loaded.Controllers[0] == A && loaded.Trigger == config.Trigger); });
            Test("portable model JSON preserves IDs and matches other PC", () => { var config = new SurpriseConfig { MediaPath = media, Trigger = TriggerKind.ControllerModel, Controllers = [A] }; string file = Path.Combine(root, "model.json"); JsonFiles.Write(file, config); var loaded = JsonFiles.Read<SurpriseConfig>(file); loaded.Validate(file); Check(loaded.Trigger == TriggerKind.ControllerModel && loaded.Controllers[0].MatchesModel(B)); });
            Test("model rejects empty missing and fallback IDs", () => { foreach (var profiles in new List<ControllerIdentity>[] { [], [A with { VendorId = 0 }], [A with { ProductId = 0 }], [A with { Source = "XInput" }] }) { var config = new SurpriseConfig { MediaPath = media, Trigger = TriggerKind.ControllerModel, Controllers = profiles }; Throws<InvalidDataException>(() => config.Validate(Path.Combine(root, "config.json"))); } });
            Test("individual wizard still requires one verified reconnect", () => { for (int stage = 0; stage < 3; stage++) { var window = new MainWindow(null, true); try { window.RenderDemoLearning(stage); Check(window.SelectedTrigger == TriggerKind.SelectedController && window.CanContinue == (stage == 2)); } finally { window.Close(); } } });
            Test("model portable import can continue without controller relearning", () => { string file = Path.Combine(root, "model-import.json"); JsonFiles.Write(file, new SurpriseConfig { MediaPath = media, Trigger = TriggerKind.ControllerModel, Controllers = [A] }); var window = new MainWindow(null, true); try { window.LoadPortable(file); window.RenderDemoStep(1); Check(window.SelectedTrigger == TriggerKind.ControllerModel && window.CanContinue); } finally { window.Close(); } });
            Test("individual portable import still requires relearning", () => { string file = Path.Combine(root, "individual-import.json"); JsonFiles.Write(file, new SurpriseConfig { MediaPath = media, Trigger = TriggerKind.SelectedController, Controllers = [A] }); var window = new MainWindow(null, true); try { window.LoadPortable(file); window.RenderDemoStep(1); Check(window.SelectedTrigger == TriggerKind.SelectedController && !window.CanContinue); } finally { window.Close(); } });
            Test("XInput slots cannot be selected identities", () => { var config = new SurpriseConfig { MediaPath = media, Trigger = TriggerKind.SelectedController, Controllers = [new("xinput:0", "Slot", "XInput")] }; Throws<InvalidDataException>(() => config.Validate(Path.Combine(root, "config.json"))); });
            Test("nonfinite volume rejected", () => Throws<InvalidDataException>(() => new SurpriseConfig { MediaPath = media, Volume = double.NaN }.Validate(Path.Combine(root, "config.json"))));
            Test("invalid overlay duration rejected", () => Throws<InvalidDataException>(() => new SurpriseConfig { MediaPath = media, OverlayDurationSeconds = 0 }.Validate(Path.Combine(root, "config.json"))));
            Test("overlay settings roundtrip", () => { var c = new SurpriseConfig { MediaPath = media, PlaybackMode = PlaybackMode.Overlay, OverlayAnchor = OverlayAnchor.Custom, OverlayX = 80, OverlayWidth = 200, OverlayDurationSeconds = 3, OverlayClickThrough = false }; string f = Path.Combine(root, "overlay.json"); JsonFiles.Write(f, c); var d = JsonFiles.Read<SurpriseConfig>(f); d.Validate(f); Check(d.PlaybackMode == PlaybackMode.Overlay && d.OverlayX == 80 && d.OverlayWidth == 200 && !d.OverlayClickThrough); });
            PortableChecks.Add(root, media, Test);
            Test("unknown schema rejected", () => Throws<InvalidDataException>(() => new SurpriseConfig { MediaPath = media, SchemaVersion = 2 }.Validate(Path.Combine(root, "config.json"))));
            Test("manifest rejects ownership mismatch", () => { WriteManifest(root, TaskRegistration.CurrentSid + "-other", ["config.json"]); Throws<InvalidDataException>(() => InstallationService.LoadManifest(root)); });
            Test("manifest rejects arbitrary deletion inventory", () => { WriteManifest(root, TaskRegistration.CurrentSid, ["precious.txt"]); Throws<InvalidDataException>(() => InstallationService.LoadManifest(root)); });
            Test("manifest accepts only owned paths", () => { WriteManifest(root, TaskRegistration.CurrentSid, ["config.json", "media/surprise.gif", "logs/executor.log"]); Check(InstallationService.LoadManifest(root).Files.Count == 3); });
        }
        finally { Directory.Delete(root, true); }
        if (args.Length >= 3 && args[0] == "--integration")
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async (_, _) => { try { await IntegrationAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args.Skip(3).Where(a => a != "--overlay-only").Select(Path.GetFullPath).ToArray(), args.Contains("--overlay-only")); } catch (Exception ex) { failed++; Console.WriteLine("FAIL integration: " + ex); } finally { app.Shutdown(); } };
            app.Run();
        }
        Console.WriteLine($"{passed} passed; {failed} failed."); return failed == 0 ? 0 : 1;
    }
    private static void WriteManifest(string root, string sid, List<string> files)
    {
        Guid id = Guid.NewGuid(); JsonFiles.Write(Path.Combine(root, InstallationService.ManifestFile), new InstallManifest { Root = root, OwnerSid = sid, InstallationId = id, TaskName = TaskRegistration.Name(id), Files = files });
    }
    private static TriggerState New() => new(new());
    private static void Check(bool value, string message = "Assertion failed.") { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static async Task IntegrationAsync(string root, string executor, string[] videos, bool overlayOnly)
    {
        // Explicit opt-in only. The task (if created) is removed in finally.
        if (Directory.Exists(root)) throw new IOException("Integration root must not already exist.");
        Directory.CreateDirectory(root);
        string fixture = SafePaths.ResolveUnder(root, "fixture.gif");
        // Two 1px GIF frames with a 100ms delay and a two-color global palette.
        File.WriteAllBytes(fixture, [71,73,70,56,57,97,1,0,1,0,128,0,0,255,0,0,0,255,0,
            33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,
            33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,
            33,249,4,4,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59]);
        var config = new SurpriseConfig { MediaPath = fixture, GifDurationSeconds = 1, Fullscreen = false, Startup = StartupKind.Manual };
        string configFile = SafePaths.ResolveUnder(root, "fixture-config.json"); JsonFiles.Write(configFile, config);
        InstallManifest? installed = null;
        string installRoot = SafePaths.ResolveUnder(root, "installation");
        try
        {
            await TestAsync("wizard requires chosen-controller verification and excludes Any controller", () =>
            {
                for (int stage = 0; stage < 3; stage++)
                {
                    var window = new MainWindow(executor, true);
                    try { window.RenderDemoLearning(stage); Check(window.SelectedTrigger == TriggerKind.SelectedController); Check(window.CanContinue == (stage == 2)); }
                    finally { window.Close(); }
                }
                var mediaWindow = new MainWindow(executor, true);
                try { mediaWindow.RenderDemoStep(2); Check(!mediaWindow.CanContinue, "Unpreviewed media can advance."); }
                finally { mediaWindow.Close(); }
                return Task.CompletedTask;
            });
            await TestAsync("published executor protocol", async () => await InstallationService.VerifyExecutorAsync(executor));
            await TestAsync("placement picker roundtrips physical coordinates and size", async () =>
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                var c = new SurpriseConfig { MediaPath = fixture, OverlayAnchor = OverlayAnchor.Custom, OverlayX = 180, OverlayY = 200, OverlayWidth = 240, OverlayHeight = 180 };
                var picker = new OverlayPlacementPicker(c, screen);
                try
                {
                    picker.Show(); await Task.Delay(300);
                    var canvas = (System.Windows.Controls.Canvas)picker.Content;
                    var bar = canvas.Children.OfType<System.Windows.Controls.StackPanel>().Single();
                    // Setting DialogResult requires a modal window; close after inspecting the physical frame instead.
                    var frame = canvas.Children.OfType<System.Windows.Controls.Border>().Single();
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(picker);
                    Check(Math.Abs(System.Windows.Controls.Canvas.GetLeft(frame) * dpi.DpiScaleX - 180) < 1 && Math.Abs(System.Windows.Controls.Canvas.GetTop(frame) * dpi.DpiScaleY - 200) < 1);
                    Check(Math.Abs(frame.ActualWidth * dpi.DpiScaleX - 240) < 1 && Math.Abs(frame.ActualHeight * dpi.DpiScaleY - 180) < 1);
                    Check(bar.Children.OfType<System.Windows.Controls.Button>().Count() == 2);
                }
                finally { picker.Close(); }
            });
            await TestAsync("portable export roundtrip preserves assets and relative paths", () =>
            {
                string zip = SafePaths.ResolveUnder(root, "portable.zip");
                PortableExporter.Export(zip, Path.Combine(Path.GetDirectoryName(executor)!, "Surprise.Setup.exe"), executor, config, fixture);
                string extracted = SafePaths.ResolveUnder(root, "portable"); ZipFile.ExtractToDirectory(zip, extracted);
                var portable = JsonFiles.Read<SurpriseConfig>(Path.Combine(extracted, "portable-config.json")); portable.Validate(Path.Combine(extracted, "portable-config.json"));
                Check(!Path.IsPathRooted(portable.MediaPath)); Check(portable.InstallationId != config.InstallationId);
                Check(Directory.GetFiles(extracted, "*.exe").Length == 2);
                Check(File.ReadAllBytes(portable.ResolveMedia(Path.Combine(extracted, "portable-config.json"))).SequenceEqual(File.ReadAllBytes(fixture)));
                return Task.CompletedTask;
            });
            await TestAsync("learning preparation uses final path and cleans itself", () =>
            {
                string location = SafePaths.ResolveUnder(root, "learning-location");
                using (var prep = new LearningPreparation(location, executor)) Check(File.Exists(prep.Executor));
                Check(!Directory.Exists(location)); return Task.CompletedTask;
            });
            await TestAsync("learning IPC returns device snapshots", async () =>
            {
                string pipeName = "Surprise.Test.Learn." + Guid.NewGuid().ToString("N");
                using var pipe = UserPipes.Server(pipeName, PipeDirection.In);
                string diagnostics = SafePaths.ResolveUnder(root, "learning-diagnostics.json");
                using var process = InstallationService.Start(executor, "--learn", "--pipe", pipeName, "--output", diagnostics);
                try
                {
                    using var timeout = new CancellationTokenSource(15000); await pipe.WaitForConnectionAsync(timeout.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                    string? json = await reader.ReadLineAsync(timeout.Token); Check(JsonSerializer.Deserialize<ControllerSnapshot>(json!, JsonFiles.Options) is not null);
                }
                catch (Exception ex) { throw new IOException(File.Exists(diagnostics) ? File.ReadAllText(diagnostics) : ex.Message, ex); }
                finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
            });
            if (SessionGuard.IsActiveUnlocked())
            {
                await TestAsync("transparent GIF overlay preserves background, placement, focus and child cleanup", async () =>
                {
                    string image = Path.GetFullPath("artifacts/media-tests/transparent-object.gif");
                    // Self-contained fallback fixture: transparent GIF with a red centre.
                    if (!File.Exists(image))
                    {
                        image = SafePaths.ResolveUnder(root, "transparent.gif");
                        using var bitmap = new System.Drawing.Bitmap(128, 128);
                        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) { graphics.Clear(System.Drawing.Color.Transparent); graphics.FillEllipse(System.Drawing.Brushes.Crimson, 24, 24, 80, 80); }
                        bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Gif);
                    }
                    var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                    var background = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.Lime, Topmost = true, Width = 500, Height = 500 };
                    background.Show();
                    IntPtr backgroundHandle = new System.Windows.Interop.WindowInteropHelper(background).Handle;
                    SetWindowPos(backgroundHandle, new IntPtr(-1), screen.Bounds.Left + 150, screen.Bounds.Top + 150, 500, 500, 0);
                    SetForegroundWindow(backgroundHandle); await Task.Delay(300);
                    IntPtr foreground = GetForegroundWindow();
                    var c = new SurpriseConfig { MediaPath = image, PlaybackMode = PlaybackMode.Overlay, OverlayAnchor = OverlayAnchor.Custom, OverlayX = 200, OverlayY = 200, OverlayWidth = 320, OverlayHeight = 320, OverlayDurationSeconds = 4, OverlayClickThrough = true, Volume = 0 };
                    string file = SafePaths.ResolveUnder(root, "overlay-config.json"), result = SafePaths.ResolveUnder(root, "overlay-result.json"); JsonFiles.Write(file, c);
                    using var process = InstallationService.Start(executor, "--play", "--config", file, "--output", result);
                    try
                    {
                        var report = await PlaybackReport(result); IntPtr hwnd = new(report.GetProperty("Window").GetInt64());
                        await Task.Delay(500);
                        Check(GetWindowRect(hwnd, out var bounds) && bounds.Left == screen.Bounds.Left + 200 && bounds.Top == screen.Bounds.Top + 200 && bounds.Right - bounds.Left == 320 && bounds.Bottom - bounds.Top == 320, "Overlay placement mismatch.");
                        IntPtr afterForeground = GetForegroundWindow();
                        long style = GetWindowLongPtr(hwnd, -20).ToInt64(); Check((style & 0x08000020) == 0x08000020, "Overlay is not NOACTIVATE/click-through.");
                        using var capture = new System.Drawing.Bitmap(320, 320);
                        using (var graphics = System.Drawing.Graphics.FromImage(capture)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, capture.Size);
                        var corner = capture.GetPixel(15, 15); var centre = capture.GetPixel(160, 160);
                        Directory.CreateDirectory("artifacts/media-tests"); capture.Save("artifacts/media-tests/overlay-verified.png", System.Drawing.Imaging.ImageFormat.Png);
                        Check(corner.G > 200 && corner.R < 40 && corner.B < 40, $"Transparent pixel covered the desktop: {corner}.");
                        Check(centre.R > 150 && centre.G < 100, $"Object was not visible: {centre}.");
                        Check(afterForeground == foreground, $"Overlay stole keyboard focus: before {foreground}, after {afterForeground}, mpv {hwnd}, background {backgroundHandle}.");
                        int childId = report.GetProperty("MpvProcessId").GetInt32();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); Check(process.ExitCode == 0);
                        bool running; try { using var child = Process.GetProcessById(childId); running = !child.HasExited; } catch (ArgumentException) { running = false; }
                        Check(!running, "mpv child remained after playback.");
                    }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } background.Close(); }
                });
                if (overlayOnly) return;
                await TestAsync("preview stop closes cleanly and removes temporary player files", async () =>
                {
                    string file = SafePaths.ResolveUnder(root, "stop-config.json"), result = SafePaths.ResolveUnder(root, "stop-result.json");
                    JsonFiles.Write(file, new SurpriseConfig { MediaPath = fixture, PlaybackMode = PlaybackMode.Overlay, OverlayDurationSeconds = 30 });
                    using var process = InstallationService.Start(executor, "--play", "--config", file, "--output", result);
                    try
                    {
                        var report = await PlaybackReport(result); int childId = report.GetProperty("MpvProcessId").GetInt32();
                        using var child = Process.GetProcessById(childId); string folder = Path.GetDirectoryName(child.MainModule!.FileName)!;
                        Check(PostMessage(new IntPtr(report.GetProperty("ControlWindow").GetInt64()), 0x0010, IntPtr.Zero, IntPtr.Zero));
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(process.ExitCode == 0);
                        Check(!Directory.Exists(folder), "Temporary mpv folder remained after preview stop."); Check(child.HasExited);
                    }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                });
                await TestAsync("invalid media fails without reporting readiness", async () =>
                {
                    string damaged = SafePaths.ResolveUnder(root, "damaged.mp4"); File.WriteAllText(damaged, "not video");
                    string file = SafePaths.ResolveUnder(root, "damaged-config.json"), result = SafePaths.ResolveUnder(root, "damaged-result.json"); JsonFiles.Write(file, new SurpriseConfig { MediaPath = damaged });
                    using var process = InstallationService.Start(executor, "--play", "--config", file, "--output", result);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); Check(process.ExitCode != 0); using var report = JsonDocument.Parse(File.ReadAllText(result)); Check(!report.RootElement.GetProperty("Opened").GetBoolean() && report.RootElement.GetProperty("Error").GetString() is not null); }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                });
                await TestAsync("animated GIF opens and closes at duration", async () =>
                {
                    using var process = InstallationService.Start(executor, "--play", "--config", configFile);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Check(process.ExitCode == 0); }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                });
                await TestAsync("fullscreen covers the selected monitor", async () =>
                {
                    var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                    var fullConfig = new SurpriseConfig { MediaPath = fixture, GifDurationSeconds = 4, Fullscreen = true, Monitor = screen.DeviceName };
                    string fullFile = SafePaths.ResolveUnder(root, "fullscreen-config.json"); JsonFiles.Write(fullFile, fullConfig);
                    string result = SafePaths.ResolveUnder(root, "full-result.json");
                    using var process = InstallationService.Start(executor, "--play", "--config", fullFile, "--output", result);
                    try
                    {
                        var report = await PlaybackReport(result);
                        Check(GetWindowRect(new IntPtr(report.GetProperty("Window").GetInt64()), out var bounds), "No playback window bounds.");
                        Check(bounds.Left == screen.Bounds.Left && bounds.Top == screen.Bounds.Top && bounds.Right == screen.Bounds.Right && bounds.Bottom == screen.Bounds.Bottom, "Fullscreen does not match the selected monitor bounds.");
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Check(process.ExitCode == 0);
                    }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                });
            }
            else Console.WriteLine("SKIP GIF playback: no active unlocked interactive session.");
            foreach (string video in videos.Where(_ => SessionGuard.IsActiveUnlocked()))
                await TestAsync("mpv video playback opens and completes: " + Path.GetFileName(video), async () =>
                {
                    var clipConfig = new SurpriseConfig { MediaPath = video, Fullscreen = false, Volume = 0 };
                    string clipFile = SafePaths.ResolveUnder(root, "video-config.json"); JsonFiles.Write(clipFile, clipConfig);
                    string result = SafePaths.ResolveUnder(root, "video-result.json"); if (File.Exists(result)) File.Delete(result);
                    using var process = InstallationService.Start(executor, "--play", "--config", clipFile, "--output", result);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); Check(process.ExitCode == 0, File.Exists(result) ? File.ReadAllText(result) : "No diagnostics"); Check((await PlaybackReport(result)).GetProperty("Completed").GetBoolean()); }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                });
            if (videos.Length > 0 && SessionGuard.IsActiveUnlocked())
                await TestAsync("Escape closes mpv video and releases its resources", async () =>
                {
                    var clipConfig = new SurpriseConfig { MediaPath = videos[^1], Fullscreen = true, Volume = 0 };
                    string clipFile = SafePaths.ResolveUnder(root, "escape-config.json"); JsonFiles.Write(clipFile, clipConfig);
                    string result = SafePaths.ResolveUnder(root, "escape-result.json");
                    using var process = InstallationService.Start(executor, "--play", "--config", clipFile, "--output", result);
                    try
                    {
                        var report = await PlaybackReport(result); process.Refresh();
                        Check(!process.HasExited, "Use a clip longer than two seconds for Escape verification.");
                        IntPtr child = new(report.GetProperty("Window").GetInt64());
                        Check(child != IntPtr.Zero, "mpv surface not created.");
                        Check(PostMessage(child, 0x0100, new IntPtr(0x1B), IntPtr.Zero));
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(process.ExitCode == 0);
                    }
                    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                });
            await TestAsync("install/manual listener health, duplicate launch, pause, rearm, and resume", async () =>
            {
                installed = await InstallationService.InstallAsync(installRoot, config, fixture, executor);
                Check((await RuntimeClient.SendAsync(installed.InstallationId, "status"))?.Status == "Armed", "New listener is not armed.");
                using var duplicate = InstallationService.Start(Path.Combine(installRoot, "Surprise.Executor.exe"), "--watch", "--config", InstallationService.ConfigPath(installRoot));
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(duplicate.ExitCode == 0, "Duplicate watcher did not exit cleanly.");
                Check((await RuntimeClient.SendAsync(installed.InstallationId, "pause"))?.Status == "Paused", "Pause command did not pause detection.");
                await InstallationService.PauseAsync(installed);
                Check(await RuntimeClient.SendAsync(installed.InstallationId, "status", 300) is null, "Pause did not stop the listener.");
                await InstallationService.ResumeAsync(installed, true);
                Check((await RuntimeClient.SendAsync(installed.InstallationId, "status"))?.Status == "Armed", "Resume did not clear persisted pause state.");
            });
            if (installed is not null)
            {
                await TestAsync("user-level task registration ownership and removal", () =>
                {
                    TaskRegistration.Register(installed, Path.Combine(installRoot, "Surprise.Executor.exe"), InstallationService.ConfigPath(installRoot));
                    try
                    {
                        Check(TaskRegistration.Enabled(installed) == true);
                        var xml = XDocument.Parse(TaskRegistration.DefinitionXml(installed)!); XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                        var principal = xml.Root!.Element(ns + "Principals")!.Element(ns + "Principal")!;
                        Check(principal.Element(ns + "UserId")!.Value == TaskRegistration.CurrentSid);
                        Check(principal.Element(ns + "LogonType")!.Value == "InteractiveToken");
                        Check(principal.Element(ns + "RunLevel")?.Value is null or "LeastPrivilege");
                        TaskRegistration.Enable(installed, false); Check(TaskRegistration.Enabled(installed) == false);
                    }
                    finally { TaskRegistration.Remove(installed); }
                    Check(TaskRegistration.Enabled(installed) is null); return Task.CompletedTask;
                });
                await TestAsync("transactional update preserves unrelated files", async () =>
                {
                    File.WriteAllText(Path.Combine(installRoot, "keep.txt"), "unrelated");
                    config.Volume = 0.2;
                    installed = await InstallationService.InstallAsync(installRoot, config, fixture, executor);
                    Check(JsonFiles.Read<SurpriseConfig>(InstallationService.ConfigPath(installRoot)).Volume == 0.2);
                    Check(File.ReadAllText(Path.Combine(installRoot, "keep.txt")) == "unrelated");
                });
                await TestAsync("failed update rolls back and preserves unowned destination", async () =>
                {
                    string blocked = Path.Combine(installRoot, "media", "surprise.mp4"); File.WriteAllText(blocked, "unowned media");
                    string newMedia = SafePaths.ResolveUnder(root, "replacement.mp4"); File.WriteAllBytes(newMedia, [1, 2, 3]);
                    var update = new SurpriseConfig { InstallationId = config.InstallationId, MediaPath = newMedia, Volume = 0.8, Startup = StartupKind.Manual };
                    bool rejected = false;
                    try { await InstallationService.InstallAsync(installRoot, update, newMedia, executor); }
                    catch (IOException ex) { rejected = ex.Message.Contains("unowned file", StringComparison.Ordinal); }
                    Check(rejected, "Update did not reject an unowned destination.");
                    Check(File.ReadAllText(blocked) == "unowned media");
                    Check(JsonFiles.Read<SurpriseConfig>(InstallationService.ConfigPath(installRoot)).Volume == 0.2);
                    Check((await RuntimeClient.SendAsync(installed.InstallationId, "status"))?.Status == "Armed", "Rollback did not restore the old watcher.");
                });
                await TestAsync("removal preserves originals and unrelated files", async () =>
                {
                    Check((await InstallationService.RemoveAsync(installed)).Count == 0);
                    Check(File.Exists(fixture)); Check(File.Exists(Path.Combine(installRoot, "keep.txt")));
                    Check(!File.Exists(Path.Combine(installRoot, "Surprise.Executor.exe"))); Check(TaskRegistration.Enabled(installed) is null);
                    Check(await RuntimeClient.SendAsync(installed.InstallationId, "status", 300) is null);
                });
            }
        }
        finally
        {
            if (installed is not null)
            {
                try { TaskRegistration.Remove(installed); }
                catch (Exception ex) { Console.WriteLine("TASK CLEANUP WARNING: " + ex.Message); failed++; }
                try { await InstallationService.StopAsync(installed); }
                catch (Exception ex) { Console.WriteLine("PROCESS CLEANUP WARNING: " + ex.Message); failed++; }
            }
            // Only this explicitly chosen test root is cleaned, never an installation elsewhere.
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static async Task TestAsync(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static async Task<JsonElement> PlaybackReport(string path)
    {
        for (int i = 0; i < 200; i++)
        {
            if (File.Exists(path)) { using var report = JsonDocument.Parse(File.ReadAllText(path)); if (report.RootElement.GetProperty("Opened").GetBoolean()) return report.RootElement.Clone(); if (report.RootElement.GetProperty("Closed").GetBoolean()) throw new IOException(File.ReadAllText(path)); }
            await Task.Delay(100);
        }
        throw new TimeoutException("Playback did not report readiness.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
