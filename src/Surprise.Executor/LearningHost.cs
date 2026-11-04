using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.Executor;

internal sealed class LearningHost(Application app, string pipeName, string? diagnostics = null) : IDisposable
{
    private readonly ControllerDetector detector = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<ControllerSnapshot> snapshots = Channel.CreateBounded<ControllerSnapshot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public void Start()
    {
        timer.Tick += (_, _) => snapshots.Writer.TryWrite(detector.Snapshot());
        timer.Start(); snapshots.Writer.TryWrite(detector.Snapshot());
        _ = WriteSnapshotsAsync();
    }
    private async Task WriteSnapshotsAsync()
    {
        try
        {
            using var pipe = UserPipes.Client(pipeName, PipeDirection.Out);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(connectTimeout.Token);
            UserPipes.VerifyServerOwner(pipe);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var snapshot in snapshots.Reader.ReadAllAsync(lifetime.Token))
                await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot, JsonFiles.Options).Replace("\r", "").Replace("\n", "").AsMemory(), lifetime.Token);
        }
        catch (Exception ex) { if (diagnostics is not null) JsonFiles.Write(diagnostics, new { error = ex.ToString() }); }
        finally { await app.Dispatcher.InvokeAsync(() => app.Shutdown()); }
    }
    public void Dispose() { timer.Stop(); lifetime.Cancel(); detector.Dispose(); lifetime.Dispose(); }
}
