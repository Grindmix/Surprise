using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.Setup;

internal sealed class LearningSession : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly NamedPipeServerStream pipe;
    private readonly Process process;
    public event Action<ControllerSnapshot>? Snapshot;
    public event Action<string>? Failed;
    public LearningSession(string executor)
    {
        string name = "Surprise.Learn." + Guid.NewGuid().ToString("N");
        pipe = UserPipes.Server(name, PipeDirection.In);
        try { process = InstallationService.Start(executor, "--learn", "--pipe", name); }
        catch { pipe.Dispose(); throw; }
    }
    public async Task RunAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(15000);
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            while (!lifetime.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(lifetime.Token);
                if (line is null) throw new IOException("Controller learning stopped unexpectedly.");
                var snapshot = JsonSerializer.Deserialize<ControllerSnapshot>(line, JsonFiles.Options);
                if (snapshot is not null) Snapshot?.Invoke(snapshot);
            }
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception ex) { Failed?.Invoke(ex.Message); }
    }
    public void Dispose()
    {
        lifetime.Cancel(); pipe.Dispose();
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch (InvalidOperationException) { }
        process.Dispose(); lifetime.Dispose();
    }
}
