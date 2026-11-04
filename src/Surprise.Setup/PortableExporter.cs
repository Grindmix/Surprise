using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Surprise.Core;

namespace Surprise.Setup;

internal static class PortableExporter
{
    public static void Export(string destination, string setup, string executor, SurpriseConfig config, string media)
    {
        if (File.Exists(destination)) throw new IOException("Choose a new ZIP filename; existing files are not overwritten.");
        string staging = Path.Combine(Path.GetTempPath(), "Surprise-export-" + Guid.NewGuid().ToString("N"));
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        Directory.CreateDirectory(staging);
        try
        {
            File.Copy(setup, Path.Combine(staging, "Surprise.Setup.exe"));
            File.Copy(executor, Path.Combine(staging, "Surprise.Executor.exe"));
            Directory.CreateDirectory(Path.Combine(staging, "third-party"));
            foreach (string notice in new[] { "THIRD-PARTY.txt", "GPL-2.0.txt", "GPL-3.0.txt" })
            {
                using var source = typeof(PortableExporter).Assembly.GetManifestResourceStream("Surprise." + notice) ?? throw new IOException("Missing player license notice.");
                using var target = File.Create(Path.Combine(staging, notice.StartsWith("GPL-") ? "third-party/" + notice : notice));
                source.CopyTo(target);
            }
            Directory.CreateDirectory(Path.Combine(staging, "media"));
            string relative = "media/surprise" + Path.GetExtension(media).ToLowerInvariant();
            File.Copy(media, Path.Combine(staging, relative));
            var portable = JsonSerializer.Deserialize<SurpriseConfig>(JsonSerializer.Serialize(config, JsonFiles.Options), JsonFiles.Options)!;
            portable.InstallationId = Guid.NewGuid(); portable.MediaPath = relative;
            JsonFiles.Write(Path.Combine(staging, "portable-config.json"), portable);
            File.WriteAllText(Path.Combine(staging, "START-HERE.txt"), "SURPRISE вЂ” QUICK START\r\n\r\n1. Keep both EXEs and the media folder together.\r\n2. Open Surprise.Setup.exe normally. Settings import automatically.\r\n3. Click Get started. Saved controller models are ready. For an individual controller, save and verify it again on this PC, or choose Any controller.\r\n4. Click Preview surprise, then Review surprise.\r\n5. Click Arm surprise. Wait a few seconds, then disconnect/reconnect your controller.\r\n\r\nYou can close Setup. The tray app keeps waiting and starts at sign-in by default.\r\nTo pause or remove, open Setup again.\r\n\r\nDirect playback without arming: Surprise.Executor.exe --play --config portable-config.json\r\n");
            ZipFile.CreateFromDirectory(staging, partial, CompressionLevel.Optimal, false);
            File.Move(partial, destination);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); Directory.Delete(staging, true); }
    }
}
