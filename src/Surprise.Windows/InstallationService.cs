using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Surprise.Core;

namespace Surprise.Windows;

public static class InstallationService
{
    public const string ManifestFile = "install-manifest.json";
    public static string ConfigPath(string root) => SafePaths.ResolveUnder(root, "config.json");
    public static InstallManifest LoadManifest(string root)
    {
        var manifest = JsonFiles.Read<InstallManifest>(SafePaths.ResolveUnder(root, ManifestFile));
        if (manifest.SchemaVersion != 1 || manifest.InstallationId == Guid.Empty || manifest.OwnerSid != TaskRegistration.CurrentSid ||
            manifest.TaskName != TaskRegistration.Name(manifest.InstallationId) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.Root)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This folder is not an installation owned by the current account. Import portable config instead.");
        if (manifest.Files is null || manifest.Files.Any(f => !IsOwnedName(f))) throw new InvalidDataException("Invalid installation file inventory.");
        foreach (string file in manifest.Files) _ = SafePaths.ResolveUnder(root, file);
        return manifest;
    }
    private static bool IsOwnedName(string file) => file is "Surprise.Executor.exe" or "config.json" or "install-manifest.json" or "runtime-state.json" or "logs/executor.log" or "logs/executor.previous.log" ||
        SurpriseConfig.SupportedExtensions.Any(ext => file == "media/surprise" + ext);

    public static async Task VerifyExecutorAsync(string executor)
    {
        if (!File.Exists(executor)) throw new FileNotFoundException("Place Surprise.Executor.exe beside setup or select it manually.", executor);
        var info = FileVersionInfo.GetVersionInfo(executor);
        if (info.ProductName != "Surprise" || info.FileDescription != "Surprise.Executor") throw new InvalidDataException("Select the published Surprise.Executor.exe from the same release.");
        string check = Path.Combine(Path.GetTempPath(), "Surprise-version-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var process = Start(executor, "--version", "--output", check);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (process.ExitCode != 0 || !File.Exists(check)) throw new InvalidDataException("Executor version check failed.");
            using var doc = JsonDocument.Parse(File.ReadAllText(check));
            if (doc.RootElement.GetProperty("protocol").GetInt32() != Protocol.Version) throw new InvalidDataException("Setup and executor protocol versions differ.");
        }
        finally { if (File.Exists(check)) File.Delete(check); }
    }

    public static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Could not start " + executable);
    }

    public static async Task<InstallManifest> InstallAsync(string root, SurpriseConfig config, string mediaSource, string executorSource)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string manifestPath = SafePaths.ResolveUnder(root, ManifestFile);
        InstallManifest? old = File.Exists(manifestPath) ? LoadManifest(root) : null;
        if (old is null && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Choose an empty folder or an existing Surprise installation. Unrelated files will not be overwritten.");
        if (old is not null && config.InstallationId != old.InstallationId) throw new InvalidDataException("Installation ID mismatch.");
        string mediaRelative = "media/surprise" + Path.GetExtension(mediaSource).ToLowerInvariant();
        config.MediaPath = Path.GetFullPath(mediaSource);
        config.Validate(Path.Combine(root, "config.json"));
        await VerifyExecutorAsync(executorSource);
        if (File.Exists(Path.ChangeExtension(executorSource, ".runtimeconfig.json")))
            throw new InvalidOperationException("Installation requires the published single-file executor. Run build/publish.ps1 first; a development apphost alone does not contain its dependencies.");

        string transaction = Path.Combine(Path.GetTempPath(), "Surprise-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var backups = new Dictionary<string, string>();
        var written = new List<string>();
        bool rootExisted = Directory.Exists(root);
        bool oldStopped = false, taskTouched = false;
        bool? oldTaskEnabled = old is null ? null : TaskRegistration.Enabled(old);
        var manifest = new InstallManifest
        {
            InstallationId = config.InstallationId, OwnerSid = TaskRegistration.CurrentSid, Root = root,
            TaskName = TaskRegistration.Name(config.InstallationId), Files = ["Surprise.Executor.exe", "config.json", ManifestFile, mediaRelative, "runtime-state.json", "logs/executor.log", "logs/executor.previous.log"]
        };
        try
        {
            // Stage first, including media if it is currently the installed copy.
            File.Copy(executorSource, Path.Combine(transaction, "executor.exe"));
            File.Copy(mediaSource, Path.Combine(transaction, "media"));
            if (old is not null)
            {
                oldStopped = true; await StopAsync(old);
                foreach (string name in old.Files)
                {
                    string path = SafePaths.ResolveUnder(root, name);
                    if (!File.Exists(path)) continue;
                    string backup = Path.Combine(transaction, "backup-" + backups.Count);
                    File.Copy(path, backup); backups[name] = backup;
                }
            }
            Directory.CreateDirectory(root);
            foreach (string name in new[] { "Surprise.Executor.exe", mediaRelative, "config.json", ManifestFile })
            {
                string dest = SafePaths.ResolveUnder(root, name);
                if (File.Exists(dest) && !backups.ContainsKey(name)) throw new IOException("Refusing to overwrite an unowned file: " + dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                written.Add(name);
                if (name == "Surprise.Executor.exe") File.Copy(Path.Combine(transaction, "executor.exe"), dest, true);
                else if (name == mediaRelative) File.Copy(Path.Combine(transaction, "media"), dest, true);
                else if (name == "config.json") { config.MediaPath = mediaRelative; JsonFiles.Write(dest, config); }
                else JsonFiles.Write(dest, manifest);
            }
            string runtimeState = SafePaths.ResolveUnder(root, "runtime-state.json");
            if (File.Exists(runtimeState)) File.Delete(runtimeState); // Saving explicitly rearms one-shot.
            if (config.Startup == StartupKind.TaskScheduler)
            {
                TaskRegistration.Register(manifest, SafePaths.ResolveUnder(root, "Surprise.Executor.exe"), ConfigPath(root)); taskTouched = true;
            }
            else if (old is not null) { TaskRegistration.Remove(old); taskTouched = true; }
            await StartWatcherAsync(manifest);
            // Commit is complete once the new watcher is healthy. Obsolete owned media
            // cleanup must not roll back a working update if an old file is still locked.
            if (old is not null)
                foreach (string obsolete in old.Files.Except(manifest.Files))
                {
                    string path = SafePaths.ResolveUnder(root, obsolete);
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { manifest.Files.Add(obsolete); JsonFiles.Write(manifestPath, manifest); }
                    catch (UnauthorizedAccessException) { manifest.Files.Add(obsolete); JsonFiles.Write(manifestPath, manifest); }
                }
            return manifest;
        }
        catch (Exception failure)
        {
            var recoveryErrors = new List<string>();
            try { await StopAsync(manifest); } catch (Exception ex) { recoveryErrors.Add(ex.Message); }
            if (taskTouched) try { TaskRegistration.Remove(manifest); } catch (Exception ex) { recoveryErrors.Add(ex.Message); }
            foreach (string name in written) try { string path = SafePaths.ResolveUnder(root, name); if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { recoveryErrors.Add(ex.Message); }
            foreach (var backup in backups) try { File.Copy(backup.Value, SafePaths.ResolveUnder(root, backup.Key), true); } catch (Exception ex) { recoveryErrors.Add(ex.Message); }
            if (old is not null && oldStopped)
            {
                try
                {
                    if (oldTaskEnabled.HasValue) { TaskRegistration.Register(old, SafePaths.ResolveUnder(root, "Surprise.Executor.exe"), ConfigPath(root)); TaskRegistration.Enable(old, oldTaskEnabled.Value); }
                    if (!old.Paused) await StartWatcherAsync(old);
                }
                catch (Exception ex) { recoveryErrors.Add(ex.Message); }
            }
            if (old is null)
            {
                foreach (string name in manifest.Files.Except(written))
                    try { string path = SafePaths.ResolveUnder(root, name); if (File.Exists(path)) File.Delete(path); }
                    catch (Exception ex) { recoveryErrors.Add(ex.Message); }
            }
            if (!rootExisted) RemoveEmptyDirectories(root);
            throw new IOException("Installation failed: " + failure.Message + (recoveryErrors.Count == 0 ? " Changes were rolled back." : " Recovery needs attention: " + string.Join("; ", recoveryErrors)), failure);
        }
        finally { Directory.Delete(transaction, true); }
    }

    public static async Task StartWatcherAsync(InstallManifest manifest)
    {
        var reply = await RuntimeClient.SendAsync(manifest.InstallationId, "status", 500);
        if (reply is not null) return;
        using var process = Start(SafePaths.ResolveUnder(manifest.Root, "Surprise.Executor.exe"), "--watch", "--config", ConfigPath(manifest.Root));
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (process.HasExited) throw new IOException("Controller listener exited. See logs/executor.log.");
            reply = await RuntimeClient.SendAsync(manifest.InstallationId, "status", 250, true);
            if (reply?.Ok == true) return;
            await Task.Delay(200);
        }
        throw new IOException("Controller listener did not become ready. See logs/executor.log.");
    }

    public static async Task StopAsync(InstallManifest manifest)
    {
        var reply = await RuntimeClient.SendAsync(manifest.InstallationId, "stop", 800);
        if (reply is not null)
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                if (await RuntimeClient.SendAsync(manifest.InstallationId, "status", 200) is null) break;
            }
        }
        // A crashed/hung listener may no longer answer IPC. Only terminate our exact image,
        // in this user's interactive session. Never kill processes by executable name alone.
        string exact = SafePaths.ResolveUnder(manifest.Root, "Surprise.Executor.exe");
        foreach (var process in Process.GetProcessesByName("Surprise.Executor"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != Process.GetCurrentProcess().SessionId || !string.Equals(process.MainModule?.FileName, exact, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    public static async Task PauseAsync(InstallManifest manifest)
    {
        TaskRegistration.Enable(manifest, false);
        await StopAsync(manifest);
        manifest.Paused = true; JsonFiles.Write(SafePaths.ResolveUnder(manifest.Root, ManifestFile), manifest);
    }
    public static async Task ResumeAsync(InstallManifest manifest, bool rearm)
    {
        var config = JsonFiles.Read<SurpriseConfig>(ConfigPath(manifest.Root)); config.Validate(ConfigPath(manifest.Root));
        if (config.Startup == StartupKind.TaskScheduler) TaskRegistration.Register(manifest, SafePaths.ResolveUnder(manifest.Root, "Surprise.Executor.exe"), ConfigPath(manifest.Root));
        await StartWatcherAsync(manifest);
        var resumed = await RuntimeClient.SendAsync(manifest.InstallationId, "resume");
        if (resumed?.Ok != true) throw new IOException("Listener could not resume detection.");
        if (rearm) await RuntimeClient.SendAsync(manifest.InstallationId, "rearm");
        manifest.Paused = false; JsonFiles.Write(SafePaths.ResolveUnder(manifest.Root, ManifestFile), manifest);
    }
    public static async Task<List<string>> RemoveAsync(InstallManifest manifest)
    {
        manifest = LoadManifest(manifest.Root);
        TaskRegistration.Remove(manifest);
        await StopAsync(manifest);
        var leftovers = new List<string>();
        // Keep the manifest until everything else has been removed, allowing cleanup retry.
        foreach (string name in manifest.Files.Where(f => f != ManifestFile))
        {
            string path = SafePaths.ResolveUnder(manifest.Root, name);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftovers.Add(path + ": " + ex.Message); }
        }
        if (leftovers.Count == 0) File.Delete(SafePaths.ResolveUnder(manifest.Root, ManifestFile));
        RemoveEmptyDirectories(manifest.Root);
        return leftovers;
    }
    private static void RemoveEmptyDirectories(string root)
    {
        foreach (string relative in new[] { "media", "logs" })
        {
            string path = SafePaths.ResolveUnder(root, relative);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }
}
