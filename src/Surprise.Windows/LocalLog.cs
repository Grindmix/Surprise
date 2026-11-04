using System;
using System.IO;
using Surprise.Core;

namespace Surprise.Windows;

public sealed class LocalLog(string root)
{
    private readonly object gate = new();
    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                string path = SafePaths.ResolveUnder(root, "logs/executor.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, SafePaths.ResolveUnder(root, "logs/executor.previous.log"), true);
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch (Exception) { /* Logging failure must not terminate playback/control. */ }
        }
    }
}
