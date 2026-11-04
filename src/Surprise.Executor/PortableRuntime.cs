using System;
using System.IO;
using System.Runtime.CompilerServices;
using Surprise.Core;

[assembly: InternalsVisibleTo("Surprise.Tests")]

namespace Surprise.Executor;

internal static class PortableRuntime
{
    internal sealed class Session : IDisposable
    {
        public string Root { get; }
        public string ConfigPath { get; }
        private bool disposed;
        public Session(string root, string configPath) { Root = root; ConfigPath = configPath; }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DeleteOwned(Root);
        }
    }

    public static Session? TryOpen(string? executablePath = null)
    {
        string path = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate this executable.");
        if (!File.Exists(path)) throw new FileNotFoundException("Executable was not found.", path);
        if (!PortablePackage.HasPayload(path)) return null;
        string root = Path.Combine(Path.GetTempPath(), "Surprise-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PortablePackage.Extract(path, root);
            string configPath = SafePaths.ResolveUnder(root, "config.json");
            var config = JsonFiles.Read<SurpriseConfig>(configPath);
            if (config.Startup != StartupKind.Manual) throw new InvalidDataException("A portable surprise does not register startup.");
            config.Validate(configPath);
            return new Session(root, configPath);
        }
        catch
        {
            DeleteOwned(root);
            throw;
        }
    }

    private static void DeleteOwned(string root)
    {
        string full = Path.GetFullPath(root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        if (!Path.GetFileName(full).StartsWith("Surprise-portable-", StringComparison.Ordinal)) return;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(full)) Directory.Delete(full, true); return; }
            catch (IOException) when (attempt < 9) { System.Threading.Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 9) { System.Threading.Thread.Sleep(50); }
        }
    }
}
