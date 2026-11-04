using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Surprise.Core;
using Forms = System.Windows.Forms;

namespace Surprise.Executor;

// Invisible WPF lifecycle host. mpv owns the actual playback/transparent GPU window.
internal sealed class PlaybackWindow : Window
{
    private readonly MpvSession mpv = new();
    private readonly SurpriseConfig config;
    private readonly string path;
    private readonly string? diagnostics;
    private readonly Surprise.Windows.LocalLog log;
    private readonly DispatcherTimer deadline = new();
    private readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool closed, configuring, configured, failing, finished;
    public bool OpenedSuccessfully { get; private set; }
    public event Action? MediaReady;
    public string? Error { get; private set; }
    public IntPtr PlaybackHandle { get; private set; }
    private NativeRect lastBounds;

    public PlaybackWindow(SurpriseConfig config, string configPath, string? diagnostics = null)
    {
        this.config = config; this.diagnostics = diagnostics; path = config.ResolveMedia(configPath);
        log = new(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        Title = "Surprise runtime"; Width = Height = 1; Left = Top = -32000;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Opacity = 0; ShowInTaskbar = false; ShowActivated = false;
        mpv.Event += (name, data) => _ = Dispatcher.InvokeAsync(() => OnMpvEvent(name, data));
        mpv.Failed += error => _ = Dispatcher.InvokeAsync(() => Fail(error));
        Loaded += async (_, _) =>
        {
            WriteDiagnostics();
            StartDeadline(20);
            try { await mpv.StartAsync(config); if (!closed) await mpv.CommandAsync("loadfile", path, "replace"); }
            catch (Exception ex) { Fail("Could not start playback.\n" + ex.Message); }
        };
        deadline.Tick += (_, _) => { if (OpenedSuccessfully) { finished = true; Close(); } else Fail("Media did not open within 20 seconds.\n" + mpv.ReadLogTail()); };
        sessionTimer.Tick += (_, _) => { if (!Surprise.Windows.SessionGuard.IsActiveUnlocked()) Close(); };
        sessionTimer.Start();
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        Closed += (_, _) =>
        {
            closed = true; deadline.Stop(); sessionTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= DisplaysChanged; WriteDiagnostics(); mpv.Dispose();
        };
    }

    private async void OnMpvEvent(string name, JsonElement data)
    {
        if (closed || failing) return;
        try
        {
            if (name == "file-loaded" && !configuring)
            {
                configuring = true;
                var video = await mpv.CommandAsync("get_property", "video-params");
                if (!video.TryGetProperty("w", out var width) || width.GetInt32() <= 0)
                    throw new InvalidDataException("Choose a file containing a video or image track.");
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    if (closed) return;
                    try { PlaybackHandle = new IntPtr((await mpv.CommandAsync("get_property", "window-id")).GetInt64()); } catch (IOException) { }
                    if (PlaybackHandle != IntPtr.Zero) break;
                    await Task.Delay(50);
                }
                if (PlaybackHandle == IntPtr.Zero) throw new IOException("mpv did not create a playback window.");
                if (config.PlaybackMode == PlaybackMode.Overlay)
                {
                    long style = GetWindowLongPtr(PlaybackHandle, -20).ToInt64();
                    style = (style | 0x08000000 | 0x80) & ~0x40000L; // NOACTIVATE, TOOLWINDOW; no APPWINDOW
                    SetWindowLongPtr(PlaybackHandle, -20, new IntPtr(style));
                }
                ShowWindow(PlaybackHandle, config.PlaybackMode == PlaybackMode.Overlay ? 4 : 9);
                PositionOnMonitor();
                if (config.PlaybackMode != PlaybackMode.Overlay) SetForegroundWindow(PlaybackHandle);
                configured = true;
                await mpv.CommandAsync("set_property", "pause", false);
            }
            else if (name == "playback-restart" && configured && !OpenedSuccessfully)
            {
                OpenedSuccessfully = true; deadline.Stop();
                if (config.PlaybackMode == PlaybackMode.Overlay) StartDeadline(config.OverlayDurationSeconds);
                else if (SurpriseConfig.IsImage(path)) StartDeadline(config.GifDurationSeconds);
                WriteDiagnostics(); MediaReady?.Invoke();
            }
            else if (name == "end-file")
            {
                string? reason = data.TryGetProperty("reason", out var value) ? value.GetString() : null;
                if (reason == "error") Fail("mpv could not play this file. It may be damaged or unsupported.\n" + mpv.ReadLogTail());
                else if (reason == "eof" && config.PlaybackMode == PlaybackMode.Standard && !SurpriseConfig.IsImage(path)) { finished = true; Close(); }
                else if (reason == "quit") Close();
            }
            else if (name == "shutdown") Close();
        }
        catch (Exception ex) { if (!closed) Fail("Playback failed.\n" + ex.Message + "\n" + mpv.ReadLogTail()); }
    }
    private void StartDeadline(int seconds) { deadline.Interval = TimeSpan.FromSeconds(seconds); deadline.Start(); }
    private void Fail(string error)
    {
        if (closed || failing) return;
        failing = true; deadline.Stop(); Error = error; OpenedSuccessfully = false; log.Write(error);
        if (diagnostics is null) MessageBox.Show(error, "Surprise playback", MessageBoxButton.OK, MessageBoxImage.Error);
        Close();
    }
    private void WriteDiagnostics()
    {
        if (diagnostics is null) return;
        if (PlaybackHandle != IntPtr.Zero && GetWindowRect(PlaybackHandle, out var current)) lastBounds = current;
        NativeRect bounds = lastBounds;
        JsonFiles.Write(diagnostics, new { Backend = "mpv", Opened = OpenedSuccessfully, Closed = closed, Completed = finished, Error,
            ControlWindow = new WindowInteropHelper(this).Handle.ToInt64(),
            Window = PlaybackHandle.ToInt64(), MpvProcessId = mpv.ProcessId, Mode = config.PlaybackMode.ToString(),
            X = bounds.Left, Y = bounds.Top, Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top });
    }
    private void PositionOnMonitor()
    {
        if (PlaybackHandle == IntPtr.Zero) return;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == config.Monitor) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        var bounds = screen.Bounds;
        int x, y, width, height;
        bool topmost = config.PlaybackMode == PlaybackMode.Overlay || config.Fullscreen;
        if (config.PlaybackMode == PlaybackMode.Overlay)
        {
            var overlay = OverlayPlacement.Resolve(config, bounds.Width, bounds.Height);
            x = bounds.Left + overlay.X; y = bounds.Top + overlay.Y; width = overlay.Width; height = overlay.Height;
        }
        else if (config.Fullscreen) { x = bounds.Left; y = bounds.Top; width = bounds.Width; height = bounds.Height; }
        else
        {
            var work = screen.WorkingArea; width = Math.Min(900, work.Width); height = Math.Min(600, work.Height);
            x = work.Left + (work.Width - width) / 2; y = work.Top + (work.Height - height) / 2;
        }
        if (!SetWindowPos(PlaybackHandle, new IntPtr(topmost ? -1 : -2), x, y, width, height, 0x0010 | 0x0020))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private void DisplaysChanged(object? sender, EventArgs e) => _ = Dispatcher.InvokeAsync(() => { if (!closed) PositionOnMonitor(); });
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
