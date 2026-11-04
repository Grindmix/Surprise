using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Surprise.Core;

namespace Surprise.Windows;

public static class RuntimeClient
{
    public static async Task<RuntimeReply?> SendAsync(Guid id, string command, int timeoutMs = 2500, bool reportPermissionError = false)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var pipe = UserPipes.Client(Protocol.RuntimePipe(id), PipeDirection.InOut);
            await pipe.ConnectAsync(timeout.Token);
            UserPipes.VerifyServerOwner(pipe);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            string? reply = await reader.ReadLineAsync(timeout.Token);
            return reply is null ? null : JsonSerializer.Deserialize<RuntimeReply>(reply, JsonFiles.Options);
        }
        catch (UnauthorizedAccessException) when (!reportPermissionError) { return null; }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { return null; }
    }
}
