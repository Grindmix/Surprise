using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Surprise.Core;

namespace Surprise.Executor;

internal sealed class MpvSession : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> requests = new();
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private Process? process;
    private ChildProcessJob? job;
    private string? logFile;
    private int requestId;
    private bool disposed;
    public event Action<string, JsonElement>? Event;
    public event Action<string>? Failed;
    public int ProcessId { get; private set; }

    public async Task StartAsync(SurpriseConfig config)
    {
        string executable = await Task.Run(MpvBundle.Prepare, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        logFile = MpvBundle.NewLog();
        string name = "Surprise.Mpv." + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        string[] options = ["--no-config", "--load-scripts=no", "--osc=no", "--terminal=no", "--idle=yes", "--pause=yes", "--force-window=yes", "--window-minimized=yes",
            "--input-default-bindings=no", "--input-media-keys=no", "--input-cursor=no", "--drag-and-drop=no", "--osd-level=0", "--sub=no", "--audio-file-auto=no", "--sub-auto=no",
            "--vo=gpu", "--gpu-api=d3d11", "--d3d11-flip=no", "--window-corners=donotround", "--auto-window-resize=no", "--taskbar-progress=no", "--keep-open=no", "--title=Surprise — Escape to close"];
        foreach (string option in options) start.ArgumentList.Add(option);
        start.ArgumentList.Add("--input-conf=" + Path.Combine(Path.GetDirectoryName(executable)!, "input.conf"));
        start.ArgumentList.Add("--input-ipc-server=" + name);
        start.ArgumentList.Add("--log-file=" + logFile);
        start.ArgumentList.Add("--volume=" + (config.Volume * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--panscan=" + (config.Fill && config.PlaybackMode != PlaybackMode.Overlay ? "1" : "0"));
        start.ArgumentList.Add("--border=" + (config.PlaybackMode == PlaybackMode.Overlay || config.Fullscreen ? "no" : "yes"));
        start.ArgumentList.Add("--ontop=" + (config.PlaybackMode == PlaybackMode.Overlay || config.Fullscreen ? "yes" : "no"));
        start.ArgumentList.Add("--background=" + (config.PlaybackMode == PlaybackMode.Overlay ? "none" : "color"));
        start.ArgumentList.Add("--background-color=" + (config.PlaybackMode == PlaybackMode.Overlay ? "0/0/0/0" : "0/0/0/1"));
        start.ArgumentList.Add("--input-cursor-passthrough=" + (config.PlaybackMode == PlaybackMode.Overlay && config.OverlayClickThrough ? "yes" : "no"));
        if (SurpriseConfig.IsImage(config.MediaPath) || config.PlaybackMode == PlaybackMode.Overlay) start.ArgumentList.Add("--loop-file=inf");
        process = Process.Start(start) ?? throw new IOException("Could not start bundled mpv.");
        ProcessId = process.Id;
        try { job = new ChildProcessJob(process); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        _ = MonitorExitAsync();
        pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(10000, lifetime.Token);
        // mpv's default Windows pipe ACL grants write access only to its owner/admins.
        Surprise.Windows.UserPipes.VerifyServerOwner(pipe);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        _ = ReadAsync();
    }
    public async Task<JsonElement> CommandAsync(params object[] command)
    {
        int id = Interlocked.Increment(ref requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id] = completion;
        try
        {
            string json = JsonSerializer.Serialize(new { command, request_id = id });
            await writes.WaitAsync(lifetime.Token);
            try { await writer!.WriteLineAsync(json.AsMemory(), lifetime.Token); }
            finally { writes.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
        }
        finally { requests.TryRemove(id, out _); }
    }
    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(pipe!, Encoding.UTF8, false, 4096, true);
            while (!lifetime.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(lifetime.Token);
                if (line is null) return;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("request_id", out var id) && requests.TryGetValue(id.GetInt32(), out var response))
                {
                    string? error = root.GetProperty("error").GetString();
                    if (error != "success") response.TrySetException(new IOException("mpv: " + error));
                    else response.TrySetResult(root.TryGetProperty("data", out var data) ? data.Clone() : default);
                }
                else if (root.TryGetProperty("event", out var type)) Event?.Invoke(type.GetString()!, root.Clone());
            }
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception ex) { Failed?.Invoke(ex.Message); }
    }
    private async Task MonitorExitAsync()
    {
        try { await process!.WaitForExitAsync(lifetime.Token); if (!disposed) Failed?.Invoke("mpv exited unexpectedly. " + ReadLogTail()); }
        catch (OperationCanceledException) { }
        catch (Exception) when (disposed) { }
    }
    public string ReadLogTail()
    {
        try { return logFile is not null && File.Exists(logFile) ? string.Join("\n", System.Linq.Enumerable.TakeLast(File.ReadLines(logFile), 14)) : ""; }
        catch (IOException) { return ""; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; lifetime.Cancel();
        pipe?.Dispose(); job?.Dispose();
        if (process is not null) { try { if (!process.HasExited) process.Kill(); process.WaitForExit(3000); } catch (InvalidOperationException) { } process.Dispose(); }
        foreach (var request in requests.Values) request.TrySetCanceled();
        if (logFile is not null) MpvBundle.ReleaseLog(logFile);
        // In-flight readers own their references to these synchronization objects.
    }
}
