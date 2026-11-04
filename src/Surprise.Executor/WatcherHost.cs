using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Surprise.Core;
using Surprise.Windows;
using Forms = System.Windows.Forms;

namespace Surprise.Executor;

internal sealed class WatcherHost : IDisposable
{
    internal const string PortableExitLabel = "Выход";
    internal const string InstalledExitLabel = "Exit until next sign-in";
    private readonly Application app;
    private readonly SurpriseConfig config;
    private readonly string configPath, root;
    private readonly bool portableMode;
    private readonly TriggerState trigger;
    private readonly ControllerDetector detector = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly LocalLog log;
    private Mutex? mutex;
    private bool ownsMutex, paused, stopping;
    private Forms.NotifyIcon? tray;
    private PlaybackWindow? player;
    private string candidate = "";
    private DateTimeOffset candidateSince, suppressUntil = DateTimeOffset.Now.AddSeconds(3);
    private string? lastWarning;
    private sealed record PersistedState(bool Consumed, bool Paused);

    public WatcherHost(Application app, SurpriseConfig config, string configPath, bool portableMode = false)
    {
        this.app = app; this.config = config; this.configPath = configPath; this.portableMode = portableMode;
        root = Path.GetDirectoryName(configPath)!; trigger = new(config); log = new(root);
    }
    public bool Start()
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        mutex = new Mutex(false, "Local\\Surprise.Executor." + sid + "." + config.InstallationId.ToString("N"));
        try { ownsMutex = mutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) return false;
        string stateFile = SafePaths.ResolveUnder(root, "runtime-state.json");
        if (File.Exists(stateFile))
        {
            var state = JsonFiles.Read<PersistedState>(stateFile); trigger.Consumed = state.Consumed; paused = state.Paused;
        }
        trigger.Baseline(detector.Snapshot().Controllers);
        SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
        timer.Tick += (_, _) => Tick(); timer.Start();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = true, Text = portableMode ? "Surprise portable listener" : "Surprise controller listener" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Preview media", null, (_, _) => app.Dispatcher.InvokeAsync(() => Play(false)));
        menu.Items.Add("Pause / resume detection", null, (_, _) => app.Dispatcher.InvokeAsync(TogglePause));
        menu.Items.Add("Rearm one-shot", null, (_, _) => app.Dispatcher.InvokeAsync(Rearm));
        menu.Items.Add(portableMode ? PortableExitLabel : InstalledExitLabel, null, (_, _) => app.Dispatcher.InvokeAsync(() => app.Shutdown()));
        tray.ContextMenuStrip = menu;
        _ = ServeAsync(); log.Write("Listener started; existing connections baselined."); UpdateTray(); return true;
    }
    private void Tick()
    {
        var snapshot = detector.Snapshot();
        if (snapshot.Warning != lastWarning) { lastWarning = snapshot.Warning; if (lastWarning is not null) log.Write(lastWarning); }
        string signature = string.Join("|", snapshot.Controllers.Select(c => c.Id));
        var now = DateTimeOffset.Now;
        if (now < suppressUntil) { trigger.Baseline(snapshot.Controllers); candidate = signature; candidateSince = now; return; }
        if (signature != candidate) { candidate = signature; candidateSince = now; return; }
        if (now - candidateSince < TimeSpan.FromMilliseconds(750)) return;
        if (trigger.Observe(snapshot.Controllers, now, SessionGuard.IsActiveUnlocked(), player is not null, paused)) Play(true);
    }
    private void Play(bool consume)
    {
        if (player is not null || !SessionGuard.IsActiveUnlocked()) return;
        try
        {
            player = new(config, configPath);
            player.MediaReady += () => { if (consume) { trigger.PlaybackStarted(DateTimeOffset.Now); SaveState(); UpdateTray(); log.Write("Surprise played."); } };
            player.Closed += (_, _) => { if (player?.Error is not null) log.Write(player.Error); player = null; };
            player.Show();
        }
        catch (Exception ex) { log.Write("Playback failed: " + ex); player?.Close(); player = null; }
    }
    private void SaveState() => JsonFiles.Write(SafePaths.ResolveUnder(root, "runtime-state.json"), new PersistedState(trigger.Consumed, paused));
    private string Status => paused ? "Paused" : config.OneShot && trigger.Consumed ? "One-shot consumed; rearm to trigger again" : "Armed";
    private void UpdateTray() { if (tray is not null) tray.Text = "Surprise: " + Status; }
    private void Rearm() { trigger.Consumed = false; trigger.Baseline(detector.Snapshot().Controllers); SaveState(); UpdateTray(); }
    private void TogglePause()
    {
        try
        {
            bool next = !paused;
            string manifestPath = SafePaths.ResolveUnder(root, InstallationService.ManifestFile);
            if (!portableMode && File.Exists(manifestPath))
            {
                var manifest = InstallationService.LoadManifest(root);
                TaskRegistration.Enable(manifest, !next);
                manifest.Paused = next; JsonFiles.Write(manifestPath, manifest);
            }
            paused = next; player?.Close(); trigger.Baseline(detector.Snapshot().Controllers); SaveState(); UpdateTray();
        }
        catch (Exception ex) { log.Write(ex.ToString()); tray?.ShowBalloonTip(3000, "Cannot change pause state", ex.Message, Forms.ToolTipIcon.Error); }
    }
    private void ResetAfterSessionChange()
    {
        if (stopping) return;
        player?.Close(); suppressUntil = DateTimeOffset.Now.AddSeconds(4); trigger.Baseline(detector.Snapshot().Controllers);
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => app.Dispatcher.InvokeAsync(ResetAfterSessionChange);
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => app.Dispatcher.InvokeAsync(ResetAfterSessionChange);
    private async Task ServeAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = UserPipes.Server(Protocol.RuntimePipe(config.InstallationId), PipeDirection.InOut);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); requestTimeout.CancelAfter(3000);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                string? command = await reader.ReadLineAsync(requestTimeout.Token);
                var reply = await app.Dispatcher.InvokeAsync(() => Handle(command));
                string json = JsonSerializer.Serialize(reply, JsonFiles.Options).Replace("\r", "").Replace("\n", "");
                await writer.WriteLineAsync(json.AsMemory(), requestTimeout.Token);
                if (command == "stop") { await app.Dispatcher.InvokeAsync(() => app.Shutdown()); return; }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            catch (Exception ex) { log.Write("Control server: " + ex.Message); await Task.Delay(500, lifetime.Token); }
        }
    }
    private RuntimeReply Handle(string? command)
    {
        switch (command)
        {
            case "status": return new(true, Status, trigger.Consumed);
            case "stop": timer.Stop(); player?.Close(); return new(true, "Stopping", trigger.Consumed);
            case "rearm": Rearm(); return new(true, Status, trigger.Consumed);
            case "resume": if (paused) TogglePause(); return new(!paused, Status, trigger.Consumed);
            case "pause": if (!paused) TogglePause(); return new(paused, Status, trigger.Consumed);
            default: return new(false, "Unknown command");
        }
    }
    public void Dispose()
    {
        stopping = true; timer.Stop(); lifetime.Cancel(); player?.Close();
        SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        detector.Dispose(); tray?.Dispose();
        if (ownsMutex) { mutex?.ReleaseMutex(); ownsMutex = false; }
        mutex?.Dispose(); lifetime.Dispose(); log.Write("Listener stopped.");
    }
}
