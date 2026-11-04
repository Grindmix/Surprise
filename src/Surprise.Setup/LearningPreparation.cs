using System;
using System.IO;
using System.Linq;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.Setup;

// WGI IDs can depend on the unpackaged application's path. Learn at the final runtime
// location, not from the distribution directory. This creates no startup registration.
internal sealed class LearningPreparation : IDisposable
{
    private readonly string root, marker;
    private readonly Guid token = Guid.NewGuid();
    public string Executor { get; }
    private sealed record Ownership(string OwnerSid, string Root, Guid Token);
    public LearningPreparation(string root, string source)
    {
        this.root = Path.GetFullPath(root);
        Executor = SafePaths.ResolveUnder(root, "Surprise.Executor.exe");
        marker = SafePaths.ResolveUnder(root, "learning-preparation.json");
        if (File.Exists(marker))
        {
            var stale = JsonFiles.Read<Ownership>(marker);
            if (stale.OwnerSid != TaskRegistration.CurrentSid || !stale.Root.Equals(this.root, StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFileSystemEntries(root).Any(p => p != marker && p != Executor))
                throw new IOException("Unrecognized learning preparation. Choose an empty folder.");
            if (File.Exists(Executor)) File.Delete(Executor);
            File.Delete(marker);
        }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Learning needs an empty destination folder, or open the existing installation first.");
        Directory.CreateDirectory(root);
        JsonFiles.Write(marker, new Ownership(TaskRegistration.CurrentSid, this.root, token));
        try
        {
            // Development apphosts need their dependencies; use the source in development,
            // while published EXEs are copied to the final application identity/location.
            if (File.Exists(Path.ChangeExtension(source, ".runtimeconfig.json")))
                throw new IOException("Selected-controller learning requires the published single-file executor. Run build/publish.ps1 first. Any controller works in development.");
            File.Copy(source, Executor);
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (!File.Exists(marker)) return;
            var owner = JsonFiles.Read<Ownership>(marker);
            if (owner.Token != token || owner.OwnerSid != TaskRegistration.CurrentSid) return;
            if (File.Exists(Executor)) File.Delete(Executor);
            File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A crash/lock leaves the ownership marker for the next run. */ }
    }
}
