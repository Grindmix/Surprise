using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Surprise.Core;
using Surprise.Executor;
using Surprise.Setup;
using Surprise.Windows;

namespace Surprise.Tests;

internal static class PortableChecks
{
    private static readonly ControllerIdentity Model = new("wgi:A", "Controller A", "WGI", 1, 2);

    public static void Add(string root, string media, Action<string, Action> test)
    {
        byte[] host = Host();
        string stub = Path.Combine(root, "stub-host.exe");
        File.WriteAllBytes(stub, host);
        var source = new SurpriseConfig
        {
            InstallationId = Guid.NewGuid(), MediaPath = media, Trigger = TriggerKind.ControllerModel, Controllers = [Model],
            Startup = StartupKind.TaskScheduler, PlaybackMode = PlaybackMode.Overlay, OverlayAnchor = OverlayAnchor.Custom,
            OverlayX = 80, OverlayWidth = 200, OverlayDurationSeconds = 3, OverlayClickThrough = false, Volume = 0.25
        };
        string package = Path.Combine(root, "Configured Surprise.exe");
        test("portable package preserves the host and roundtrips media", () =>
        {
            PortableBuilder.Build(package, stub, source, media);
            byte[] packed = File.ReadAllBytes(package);
            if (!packed.AsSpan(0, host.Length).SequenceEqual(host)) throw new Exception("The executor prefix was modified.");
            var names = PortablePackage.Entries(package);
            if (names.Any(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Contains("Setup", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Payload contains an executable.");
            string extracted = Path.Combine(root, "roundtrip");
            PortablePackage.Extract(package, extracted);
            string configPath = Path.Combine(extracted, "config.json");
            var loaded = JsonFiles.Read<SurpriseConfig>(configPath);
            loaded.Validate(configPath);
            if (loaded.Startup != StartupKind.Manual || loaded.InstallationId == source.InstallationId) throw new Exception("Portable config was not made ephemeral and manual.");
            if (loaded.Trigger != TriggerKind.ControllerModel || loaded.Controllers[0] != Model) throw new Exception("Controller model was not preserved.");
            if (loaded.PlaybackMode != PlaybackMode.Overlay || loaded.OverlayX != 80 || loaded.OverlayClickThrough) throw new Exception("Overlay settings were not preserved.");
            if (!File.ReadAllBytes(loaded.ResolveMedia(configPath)).SequenceEqual(File.ReadAllBytes(media))) throw new Exception("Media bytes changed.");
            if (!File.Exists(Path.Combine(extracted, "THIRD-PARTY.txt")) || !File.Exists(Path.Combine(extracted, "third-party", "GPL-2.0.txt")) || !File.Exists(Path.Combine(extracted, "third-party", "GPL-3.0.txt")))
                throw new Exception("License notices were not embedded.");
        });
        test("portable build refuses to overwrite", () =>
        {
            byte[] before = File.ReadAllBytes(package);
            try { PortableBuilder.Build(package, stub, source, media); }
            catch (IOException) { if (!File.ReadAllBytes(package).SequenceEqual(before)) throw new Exception("A refused overwrite changed the file."); return; }
            throw new Exception("Expected IOException.");
        });
        test("portable build never embeds Setup", () =>
        {
            string named = Path.Combine(root, "Surprise.Setup.exe");
            File.WriteAllBytes(named, host);
            string blocked = Path.Combine(root, "should-not-exist.exe");
            try { PortableBuilder.Build(blocked, named, source, media); }
            catch (InvalidDataException) { }
            string? real = FindSetup();
            if (real is not null)
            {
                try { PortableBuilder.Build(blocked, real, source, media); }
                catch (InvalidDataException) { }
            }
            if (File.Exists(blocked)) throw new Exception("Setup was embedded.");
        });
        test("portable build rejects a non-executable and a missing folder", () =>
        {
            string text = Path.Combine(root, "notes.exe");
            File.WriteAllText(text, "not a pe");
            try { PortableBuilder.Build(Path.Combine(root, "bad-host.exe"), text, source, media); }
            catch (InvalidDataException) { goto MissingFolder; }
            throw new Exception("Expected a non-PE rejection.");
            MissingFolder:
            try { PortableBuilder.Build(Path.Combine(root, "missing-dir", "out.exe"), stub, source, media); }
            catch (DirectoryNotFoundException) { return; }
            throw new Exception("Expected a missing-folder rejection.");
        });
        test("plain and badly signed files are not portable payloads", () =>
        {
            string plain = Path.Combine(root, "plain.exe");
            File.WriteAllBytes(plain, host);
            if (PortablePackage.HasPayload(plain) || PortableRuntime.TryOpen(plain) is not null) throw new Exception("A plain executable looked packaged.");
            string badMagic = CopyPackage(package, root, "bad-magic.exe");
            Patch(badMagic, new FileInfo(badMagic).Length - 1, [0x58]);
            if (PortablePackage.HasPayload(badMagic)) throw new Exception("A damaged magic still matched.");
            string badVersion = CopyPackage(package, root, "bad-version.exe");
            Patch(badVersion, new FileInfo(badVersion).Length - 12, [99, 0, 0, 0]);
            ExpectInvalid(() => PortablePackage.HasPayload(badVersion));
            string badLength = CopyPackage(package, root, "bad-length.exe");
            Patch(badLength, new FileInfo(badLength).Length - PortablePackage.FooterLength, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            ExpectInvalid(() => PortablePackage.HasPayload(badLength));
            string badCrc = CopyPackage(package, root, "bad-crc.exe");
            Patch(badCrc, host.Length + 30, [0x5A]);
            ExpectInvalid(() => PortablePackage.HasPayload(badCrc));
            string notZip = Path.Combine(root, "not-zip.exe");
            using (var output = File.Create(notZip))
            {
                output.Write(host);
                PortablePackage.Append(output, new MemoryStream(new byte[64]));
            }
            ExpectInvalid(() => PortablePackage.HasPayload(notZip));
        });
        test("payload paths cannot escape or carry Setup", () =>
        {
            string outside = Path.Combine(root, "escaped.txt");
            string slip = Path.Combine(root, "slip.exe");
            WritePackage(slip, host, zip =>
            {
                using (var entry = zip.CreateEntry("../escaped.txt").Open()) entry.Write(new byte[] { 1, 2, 3 });
                WriteEntry(zip, "config.json", "{}"u8);
                WriteEntry(zip, "media/surprise.gif", "GIF"u8);
            });
            ExpectInvalid(() => PortablePackage.Extract(slip, Path.Combine(root, "slip-out")));
            if (File.Exists(outside)) throw new Exception("A payload path escaped the package.");
            string nested = Path.Combine(root, "nested-slip.exe");
            WritePackage(nested, host, zip => WriteEntry(zip, "media/../../escaped.gif", "GIF"u8));
            ExpectInvalid(() => PortablePackage.Extract(nested, Path.Combine(root, "nested-out")));
            if (File.Exists(Path.Combine(root, "escaped.gif"))) throw new Exception("A nested payload path escaped the package.");
            string setupEntry = Path.Combine(root, "setup-entry.exe");
            WritePackage(setupEntry, host, zip =>
            {
                WriteEntry(zip, "Surprise.Setup.exe", host);
                WriteEntry(zip, "config.json", "{}"u8);
                WriteEntry(zip, "media/surprise.gif", "GIF"u8);
            });
            ExpectInvalid(() => PortablePackage.Extract(setupEntry, Path.Combine(root, "setup-out")));
            if (File.Exists(Path.Combine(root, "setup-out", "Surprise.Setup.exe"))) throw new Exception("Setup was extracted.");
        });
        test("runtime unpacks a manual payload and removes its temp directory", () =>
        {
            var before = Temps();
            string? owned = null;
            using (var session = PortableRuntime.TryOpen(package))
            {
                if (session is null) throw new Exception("Packaged file was not detected.");
                owned = session.Root;
                var loaded = JsonFiles.Read<SurpriseConfig>(session.ConfigPath);
                if (loaded.Startup != StartupKind.Manual) throw new Exception("Runtime accepted an armed startup mode.");
                loaded.Validate(session.ConfigPath);
            }
            if (owned is null || Directory.Exists(owned)) throw new Exception("Portable temp directory was not removed.");
            string armed = Path.Combine(root, "armed.exe");
            var hostile = new SurpriseConfig { MediaPath = "media/surprise.gif", Startup = StartupKind.TaskScheduler, InstallationId = Guid.NewGuid() };
            WritePackage(armed, host, zip =>
            {
                WriteEntry(zip, "config.json", JsonSerializer.SerializeToUtf8Bytes(hostile, JsonFiles.Options));
                WriteEntry(zip, "media/surprise.gif", "GIF"u8);
            });
            ExpectInvalid(() => PortableRuntime.TryOpen(armed));
            if (Temps().Except(before, StringComparer.OrdinalIgnoreCase).Any()) throw new Exception("A rejected payload left a temp directory.");
        });
        test("executor bundle cleanup does not delete a fallback binary", () =>
        {
            string prepared = ExecutorBundle.Prepare();
            if (!File.Exists(prepared)) throw new Exception("No executor was found.");
            bool owned = Path.GetFileName(prepared).StartsWith("Surprise-bundled-executor-", StringComparison.Ordinal);
            ExecutorBundle.Cleanup();
            if (File.Exists(prepared) == owned) throw new Exception(owned ? "Extracted executor was left behind." : "Fallback executor was deleted.");
        });
        test("portable watcher exit label is Выход", () =>
        {
            if (WatcherHost.PortableExitLabel != "Выход" || WatcherHost.InstalledExitLabel != "Exit until next sign-in")
                throw new Exception("Watcher exit label mismatch.");
        });
    }

    public static int PublishedLaunch(string executor)
    {
        try
        {
            WatchPublished(executor).GetAwaiter().GetResult();
            Console.WriteLine("PASS published portable runtime");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL published portable runtime: " + ex);
            return 1;
        }
    }

    private static async Task WatchPublished(string executor)
    {
        if (!File.Exists(executor)) throw new FileNotFoundException("Published executor was not found.", executor);
        var info = FileVersionInfo.GetVersionInfo(executor);
        if (info.FileDescription != "Surprise.Executor" || new FileInfo(executor).Length < 10_000_000)
            throw new InvalidOperationException("Pass the published self-contained Surprise.Executor.exe, not a development apphost.");
        string root = Path.Combine(Path.GetTempPath(), "Surprise-published-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? process = null;
        var before = Temps();
        try
        {
            string media = Path.Combine(root, "clip.gif");
            File.WriteAllBytes(media, "GIF"u8.ToArray());
            var config = new SurpriseConfig { MediaPath = media, Startup = StartupKind.TaskScheduler, Volume = 0 };
            string packaged = Path.Combine(root, "Configured Surprise.exe");
            PortableBuilder.Build(packaged, executor, config, media);
            using (var source = File.OpenRead(executor))
            using (var copy = File.OpenRead(packaged))
            {
                if (copy.Length <= source.Length) throw new InvalidOperationException("The package is not larger than the executor.");
                byte[] left = new byte[4096], right = new byte[4096];
                source.ReadExactly(left); copy.ReadExactly(right);
                if (!left.AsSpan().SequenceEqual(right)) throw new InvalidOperationException("The published executable prefix changed.");
            }
            string extracted = Path.Combine(root, "checked");
            PortablePackage.Extract(packaged, extracted);
            string configPath = Path.Combine(extracted, "config.json");
            var loaded = JsonFiles.Read<SurpriseConfig>(configPath);
            loaded.Validate(configPath);
            if (loaded.Startup != StartupKind.Manual) throw new InvalidOperationException("The packaged surprise arms startup.");
            if (!File.ReadAllBytes(loaded.ResolveMedia(configPath)).SequenceEqual(File.ReadAllBytes(media)))
                throw new InvalidOperationException("Packaged media did not roundtrip.");
            await RunVersion(packaged);
            var manifest = new InstallManifest { InstallationId = loaded.InstallationId, OwnerSid = TaskRegistration.CurrentSid, TaskName = TaskRegistration.Name(loaded.InstallationId), Root = root };
            process = Process.Start(new ProcessStartInfo(packaged) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root })
                ?? throw new InvalidOperationException("Could not start the packaged executable.");
            RuntimeReply? status = null;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited) throw new InvalidOperationException("Watcher exited before status (" + process.ExitCode + ").");
                status = await RuntimeClient.SendAsync(loaded.InstallationId, "status", 3000);
                if (status?.Ok == true) break;
                await Task.Delay(500);
            }
            if (status is null || !status.Ok || status.Status != "Armed")
                throw new InvalidOperationException("No-args launch did not arm the watcher. Last status: " + status?.Status);
            if (TaskRegistration.Enabled(manifest) is not null) throw new InvalidOperationException("The portable watcher registered a sign-in task.");
            var stopped = await RuntimeClient.SendAsync(loaded.InstallationId, "stop", 8000);
            if (stopped is null || !stopped.Ok) throw new InvalidOperationException("RuntimeClient could not stop the watcher.");
            using var exitTimeout = new System.Threading.CancellationTokenSource(20000);
            try { await process.WaitForExitAsync(exitTimeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Watcher did not exit after stop."); }
            if (process.ExitCode != 0) throw new InvalidOperationException("Watcher exit code was " + process.ExitCode + ".");
            var leftoverDeadline = DateTime.UtcNow.AddSeconds(5);
            string[] leftover = [];
            while (DateTime.UtcNow < leftoverDeadline)
            {
                leftover = Temps().Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
                if (leftover.Length == 0) return;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Portable temp was not removed: " + string.Join(", ", leftover));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                catch (Exception) { }
            }
            for (int attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) when (attempt < 9) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) when (attempt < 9) { await Task.Delay(200); }
            }
        }
    }

    private static async Task RunVersion(string exe)
    {
        string output = Path.Combine(Path.GetDirectoryName(exe)!, "version.json");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        start.ArgumentList.Add("--version");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the executor.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(90000);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Version check timed out. " + await stderr);
        }
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("Version check failed (" + process.ExitCode + "): " + await stderr + await stdout);
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        if (doc.RootElement.GetProperty("protocol").GetInt32() != Protocol.Version)
            throw new InvalidOperationException("Protocol mismatch.");
        string version = doc.RootElement.GetProperty("version").GetString() ?? "";
        if (!version.StartsWith("1.4", StringComparison.Ordinal)) throw new InvalidOperationException("Expected version 1.4, got " + version);
        if (doc.RootElement.GetProperty("executable").GetString() != "Surprise.Executor") throw new InvalidOperationException("Version output was not the executor.");
    }

    private static byte[] Host()
    {
        var host = new byte[128];
        host[0] = (byte)'M'; host[1] = (byte)'Z';
        return host;
    }

    private static string CopyPackage(string source, string root, string name)
    {
        string copy = Path.Combine(root, name);
        File.Copy(source, copy, true);
        return copy;
    }

    private static void Patch(string path, long offset, byte[] bytes)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = offset;
        stream.Write(bytes);
    }

    private static void WritePackage(string destination, byte[] host, Action<ZipArchive> populate)
    {
        string zipPath = destination + ".zip-src";
        using (var zipStream = File.Create(zipPath))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, false)) populate(zip);
        using (var output = File.Create(destination))
        {
            output.Write(host);
            using var payload = File.OpenRead(zipPath);
            PortablePackage.Append(output, payload);
        }
        File.Delete(zipPath);
    }

    private static void WriteEntry(ZipArchive zip, string name, ReadOnlySpan<byte> bytes)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(bytes);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException.");
    }

    private static HashSet<string> Temps() => Directory.GetDirectories(Path.GetTempPath(), "Surprise-portable-*").ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? FindSetup()
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, "Surprise.Setup.exe");
        if (File.Exists(sibling)) return sibling;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (string config in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(dir.FullName, "src", "Surprise.Setup", "bin", config, "net10.0-windows10.0.19041.0", "Surprise.Setup.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
