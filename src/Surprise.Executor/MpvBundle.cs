using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace Surprise.Executor;

internal static class MpvBundle
{
    private static readonly object gate = new();
    private static string? root;
    private static readonly HashSet<string> owned = [];
    public static string Prepare()
    {
        lock (gate)
        {
            if (root is not null) return Path.Combine(root, "mpv.exe");
            var assembly = Assembly.GetExecutingAssembly();
            using var binary = assembly.GetManifestResourceStream("Surprise.mpv.exe") ?? throw new IOException("This executor has no bundled mpv. Run build/publish.ps1 to obtain a complete build.");
            using var hashStream = assembly.GetManifestResourceStream("Surprise.mpv.sha256") ?? throw new IOException("Bundled mpv checksum is missing.");
            using var reader = new StreamReader(hashStream);
            string expected = reader.ReadToEnd().Trim();
            string folder = Path.Combine(Path.GetTempPath(), "Surprise-mpv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string executable = Path.Combine(folder, "mpv.exe");
            try
            {
                using (var file = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None)) binary.CopyTo(file);
                using (var file = File.OpenRead(executable))
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Bundled mpv checksum did not match.");
                string input = Path.Combine(folder, "input.conf");
                File.WriteAllText(input, "ESC quit\n");
                owned.Add(executable); owned.Add(input); root = folder; return executable;
            }
            catch { if (File.Exists(executable)) File.Delete(executable); if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); throw; }
        }
    }
    public static string NewLog()
    {
        lock (gate) { _ = Prepare(); string file = Path.Combine(root!, "playback-" + Guid.NewGuid().ToString("N") + ".log"); owned.Add(file); return file; }
    }
    public static void ReleaseLog(string path)
    {
        lock (gate) { if (!owned.Contains(path)) return; try { if (File.Exists(path)) File.Delete(path); owned.Remove(path); } catch (IOException) { } }
    }
    public static void Cleanup()
    {
        lock (gate)
        {
            // Windows may briefly retain the executable image after process exit.
            foreach (string file in owned)
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    try { if (File.Exists(file)) File.Delete(file); break; }
                    catch (IOException) { if (attempt < 29) Thread.Sleep(100); }
                    catch (UnauthorizedAccessException) { if (attempt < 29) Thread.Sleep(100); }
                }
            if (root is not null) try { Directory.Delete(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            root = null; owned.Clear();
        }
    }
}
