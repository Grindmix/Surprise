using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Surprise.Core;
using Surprise.Executor;
using Surprise.Windows;

namespace Surprise.App;

internal sealed class AppHost : IDisposable
{
    private readonly Application app;
    public string SettingsPath { get; }
    private readonly string instance;
    private Mutex? mutex;
    private bool ownsMutex, disposed, starting;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? loading;
    private readonly ControllerDetector detector = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TriggerState? trigger;
    private string candidate = "";
    private string loggedControllers = "\0", lastWarning = "";
    private bool pendingDecision;
    private bool? lastSessionActive;
    private DateTimeOffset stableSince = DateTimeOffset.Now, suppressUntil = DateTimeOffset.Now.AddSeconds(3);
    public AnimationSettings? Settings { get; private set; }
    public MainWindow? Window { get; private set; }
    private Window? player;
    public bool Paused { get; private set; }
    private bool editing;
    public bool Editing { get => editing; set { if (editing != value) DiagnosticLog.Info("detection.editing", new { Active = value }); editing = value; } }
    public bool Playing => player is not null || starting;
    public string? Error { get; private set; }
    public event Action? Changed;
    public AppHost(Application app, string settingsPath)
    {
        this.app = app; SettingsPath = settingsPath;
        instance = "Surprise.App." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsPath.ToUpperInvariant())))[..24];
        DiagnosticLog.Info("host.created", new { SettingsPath, Instance = instance });
    }
    public bool Acquire()
    {
        mutex = new Mutex(false, "Local\\" + instance);
        try { ownsMutex = mutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
        DiagnosticLog.Info("instance.acquire", new { Acquired = ownsMutex }); return ownsMutex;
    }
    public void ActivateExisting()
    {
        using var pipe = new NamedPipeClientStream(".", instance, PipeDirection.Out);
        pipe.Connect(5000); using var writer = new StreamWriter(pipe); writer.WriteLine("show"); DiagnosticLog.Info("instance.activation-sent");
    }
    public void Start(bool background)
    {
        DiagnosticLog.Info("host.start", new { Background = background, SettingsExist = File.Exists(SettingsPath), SettingsPath });
        if (File.Exists(SettingsPath))
        {
            try { Settings = JsonFiles.Read<AnimationSettings>(SettingsPath); Settings.Validate(SettingsPath); DiagnosticLog.Info("settings.loaded", Settings); SetTrigger(); }
            catch (Exception ex) { DiagnosticLog.Error("settings.load-failed", ex, new { SettingsPath }); Error = ex.Message; }
        }
        timer.Tick += (_, _) => Tick(); timer.Start(); _ = ListenAsync();
        if (!background || Settings is null || Error is not null) Show();
    }
    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(instance, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var reader = new StreamReader(pipe);
                string? command = await reader.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), lifetime.Token);
                DiagnosticLog.Info("instance.command", new { Command = command });
                if (command == "show") await app.Dispatcher.InvokeAsync(Show);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { if (!lifetime.IsCancellationRequested) DiagnosticLog.Error("instance.pipe-failed", ex); }
        }
    }
    public void Show()
    {
        DiagnosticLog.Info("ui.show", new { ExistingWindow = Window is not null });
        if (Window is null) Window = new MainWindow(this);
        Window.Show(); if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
    }
    public ControllerSnapshot Controllers()
    {
        var snapshot = detector.Snapshot();
        string signature = string.Join('|', snapshot.Controllers.Select(c => c.Id));
        if (signature != loggedControllers) { loggedControllers = signature; DiagnosticLog.Info("controllers.snapshot", new { snapshot.Controllers }); }
        if ((snapshot.Warning ?? "") != lastWarning) { lastWarning = snapshot.Warning ?? ""; DiagnosticLog.Warn("controllers.warning", new { snapshot.Warning }); }
        return snapshot;
    }
    public string Status => Error is not null ? "Нужно проверить настройки" : Settings is null ? "Сначала настрой сюрприз" : Editing ? "Настройка — срабатывания приостановлены" : Playing ? "Сюрприз воспроизводится" : Paused ? "Ожидание приостановлено" : "Ожидает подключения контроллера";
    public void Report(string message) { DiagnosticLog.Write("ERROR", "host.error", new { Message = message }); Error = message; Changed?.Invoke(); if (Window is null || !Window.IsVisible) Show(); }
    public void ClearError() { Error = null; Changed?.Invoke(); }
    public void Pause() { Paused = !Paused; DiagnosticLog.Info("detection.pause", new { Paused }); Stop(); SetTrigger(); Changed?.Invoke(); }
    private void SetTrigger()
    {
        if (Settings is null) return;
        trigger = new(Settings.PlayerConfig(SettingsPath)); trigger.Baseline(Controllers().Controllers);
        suppressUntil = DateTimeOffset.Now.AddSeconds(2);
        DiagnosticLog.Info("detection.baseline", new { Settings.Controller, Settings.CooldownSeconds, SuppressUntil = suppressUntil });
    }
    public void Save(AnimationSettings draft)
    {
        DiagnosticLog.Info("settings.save-request", new { SettingsPath, Settings = draft });
        draft.Validate(SettingsPath);
        Stop();
        var saved = draft.Copy();
        saved.Files = saved.Files.Select(p =>
        {
            string full = saved.Resolve(p, SettingsPath), root = Path.GetDirectoryName(SettingsPath)!;
            string relative = Path.GetRelativePath(root, full);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative) ? relative : full;
        }).ToList();
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".bak", true);
        JsonFiles.Write(SettingsPath, saved); Settings = saved; Error = null; Editing = false; SetTrigger(); Changed?.Invoke();
        DiagnosticLog.Info("settings.saved", new { SettingsPath, Backup = SettingsPath + ".bak", Settings = saved });
    }
    private void Tick()
    {
        if (disposed) return;
        try
        {
            var snapshot = Controllers(); var now = DateTimeOffset.Now;
            string signature = string.Join('|', snapshot.Controllers.Select(c => c.Id));
            bool active = SessionGuard.IsActiveUnlocked();
            if (lastSessionActive != active) { lastSessionActive = active; DiagnosticLog.Info("session.state", new { ActiveUnlocked = active }); }
            if (!active) Stop();
            if (trigger is null) return;
            if (signature != candidate) { candidate = signature; stableSince = now; pendingDecision = true; DiagnosticLog.Info("detection.connection-change", new { snapshot.Controllers, Paused, Editing, Playing, SessionActive = active }); return; }
            if (now - stableSince < TimeSpan.FromMilliseconds(750)) return;
            if (now < suppressUntil) { trigger.Baseline(snapshot.Controllers); if (pendingDecision) DiagnosticLog.Info("detection.suppressed", new { Reason = "Startup/resume baseline" }); pendingDecision = false; return; }
            bool fire = trigger.Observe(snapshot.Controllers, now, active, Playing, Paused || Editing || Error is not null);
            if (pendingDecision) { DiagnosticLog.Info("detection.decision", new { Fire = fire, Paused, Editing, Playing, SessionActive = active, Error, Settings!.CooldownSeconds, SelectedController = Settings.Controller }); pendingDecision = false; }
            if (fire) { DiagnosticLog.Info("detection.trigger"); _ = PlayAsync(Settings!, false); }
        }
        catch (Exception ex) { DiagnosticLog.Error("detection.tick-failed", ex); Report(ex.Message); }
    }
    public async Task PlayAsync(AnimationSettings config, bool preview = true)
    {
        if (Playing) { DiagnosticLog.Warn("playback.request-ignored", new { Reason = "Already playing or loading" }); return; }
        config = config.Copy();
        try
        {
            DiagnosticLog.Info("playback.request", new { Preview = preview, Settings = config });
            config.Validate(SettingsPath); if (!SessionGuard.IsActiveUnlocked()) throw new InvalidOperationException("Для показа нужен разблокированный рабочий стол.");
            starting = true; loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); Changed?.Invoke();
            if (config.MediaKind == MediaKind.PixelArt)
            {
                var frames = await FrameLoader.LoadAsync(config, SettingsPath, loading.Token);
                loading.Token.ThrowIfCancellationRequested();
                var overlay = new PixelOverlay(config, frames); player = overlay;
                overlay.Ready += () => { DiagnosticLog.Info("playback.ready", new { Backend = "PNG", Preview = preview }); if (!preview) trigger?.PlaybackStarted(DateTimeOffset.Now); };
            }
            else
            {
                var playback = new PlaybackWindow(config.PlayerConfig(SettingsPath), SettingsPath);
                playback.MediaReady += () => { DiagnosticLog.Info("playback.ready", new { Backend = "mpv", Preview = preview }); if (!preview) trigger?.PlaybackStarted(DateTimeOffset.Now); };
                player = playback;
            }
            player.Closed += (_, _) => { DiagnosticLog.Info("playback.closed", new { Window = player?.Title, Preview = preview }); if (player is PlaybackWindow p && p.Error is not null) Report(p.Error); player = null; Changed?.Invoke(); };
            player.Show();
        }
        catch (OperationCanceledException) { DiagnosticLog.Info("playback.cancelled"); }
        catch (Exception ex) { DiagnosticLog.Error("playback.failed", ex, new { Preview = preview, Settings = config }); Report(ex.Message); }
        finally { starting = false; loading?.Dispose(); loading = null; Changed?.Invoke(); }
    }
    public void Stop() { if (Playing) DiagnosticLog.Info("playback.stop-request", new { Loading = starting, Window = player?.Title }); loading?.Cancel(); player?.Close(); }
    public void Exit() { DiagnosticLog.Info("host.exit-request"); Stop(); Window?.AllowExit(); app.Shutdown(); }
    public void Dispose()
    {
        disposed = true; timer.Stop(); lifetime.Cancel(); loading?.Cancel(); player?.Close(); detector.Dispose();
        if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose(); lifetime.Dispose();
        DiagnosticLog.Info("host.disposed");
    }
}

internal static class Autostart
{
    private const string Name = "Surprise";
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.Ordinal); } }
    private static string Command => "\"" + Environment.ProcessPath + "\" --background";
    public static void Set(bool enabled)
    {
        DiagnosticLog.Info("autostart.change", new { Enabled = enabled, Executable = Environment.ProcessPath, Command });
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(Name, Command, RegistryValueKind.String); else key.DeleteValue(Name, false);
    }
}
