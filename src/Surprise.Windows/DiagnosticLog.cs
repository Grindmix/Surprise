using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Surprise.Windows;

// Shared by the unified UI, PNG renderer and linked mpv backend. Never crashes the app.
public static class DiagnosticLog
{
    private static readonly object gate = new();
    private const long Limit = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string? FilePath { get; private set; }
    public static string? Failure { get; private set; }
    public static bool Enabled => FilePath is not null;
    public static void Initialize(string directory)
    {
        lock (gate) { FilePath = Path.Combine(Path.GetFullPath(directory), "Surprise.log"); Failure = null; }
        Info("log.initialized", new { FilePath, MaxBytes = Limit, Backups = 3 });
    }
    public static void Info(string name, object? data = null) => Write("INFO", name, data);
    public static void Warn(string name, object? data = null) => Write("WARN", name, data);
    public static void Error(string name, Exception error, object? data = null) => Write("ERROR", name, new { Details = data, Exception = error.ToString() });
    public static void Write(string level, string name, object? data = null)
    {
        lock (gate)
        {
            if (FilePath is null) return;
            try
            {
                string line = DateTimeOffset.Now.ToString("O") + $" [{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] {level} {name} " + JsonSerializer.Serialize(data, JsonOptions) + Environment.NewLine;
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + bytes.Length > Limit)
                {
                    for (int i = 3; i >= 1; i--)
                    {
                        string source = i == 1 ? FilePath : FilePath + "." + (i - 1);
                        if (File.Exists(source)) File.Move(source, FilePath + "." + i, true);
                    }
                }
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes); Failure = null;
            }
            catch (Exception ex) { Failure = ex.Message; Trace.WriteLine("Surprise logging: " + ex); }
        }
    }
}
