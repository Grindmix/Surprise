using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Surprise.Core;

namespace Surprise.Setup;

internal static class PortableBuilder
{
    public static void Build(string destination, string executor, SurpriseConfig config, string media)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Destination is required.", nameof(destination));
        if (string.IsNullOrWhiteSpace(executor)) throw new ArgumentException("Executor is required.", nameof(executor));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(media)) throw new ArgumentException("Media is required.", nameof(media));
        string destinationFull = Path.GetFullPath(destination);
        string executorFull = Path.GetFullPath(executor);
        if (File.Exists(destinationFull)) throw new IOException("Choose a new filename; existing files are not overwritten.");
        if (Directory.Exists(destinationFull)) throw new IOException("The destination is a folder.");
        if (destinationFull.Equals(executorFull, StringComparison.OrdinalIgnoreCase)) throw new IOException("The portable surprise cannot overwrite the executor.");
        string? parent = Path.GetDirectoryName(destinationFull);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose a destination inside an existing folder.");
        if (!File.Exists(executorFull)) throw new FileNotFoundException("Executor was not found.", executorFull);
        if (Path.GetFileName(executorFull).Equals("Surprise.Setup.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Setup cannot be embedded in a portable surprise.");
        var version = FileVersionInfo.GetVersionInfo(executorFull);
        if (string.Equals(version.FileDescription, "Surprise.Setup", StringComparison.Ordinal))
            throw new InvalidDataException("Setup cannot be embedded in a portable surprise.");
        using (var probe = File.OpenRead(executorFull))
        {
            Span<byte> mz = stackalloc byte[2];
            if (probe.Read(mz) != 2 || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
                throw new InvalidDataException("The executor file is not a Windows executable.");
        }
        if (!File.Exists(media)) throw new FileNotFoundException("The media file is missing.", media);
        string extension = Path.GetExtension(media);
        if (!SurpriseConfig.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a supported video, GIF, PNG, APNG, or WebP file.");

        string staging = Path.Combine(Path.GetTempPath(), "Surprise-pack-" + Guid.NewGuid().ToString("N"));
        string partial = destinationFull + "." + Guid.NewGuid().ToString("N") + ".partial";
        Directory.CreateDirectory(staging);
        try
        {
            string relative = "media/surprise" + extension.ToLowerInvariant();
            Directory.CreateDirectory(Path.Combine(staging, "media"));
            File.Copy(media, Path.Combine(staging, "media", "surprise" + extension.ToLowerInvariant()));
            var portable = JsonSerializer.Deserialize<SurpriseConfig>(JsonSerializer.Serialize(config, JsonFiles.Options), JsonFiles.Options)!;
            portable.InstallationId = Guid.NewGuid();
            portable.MediaPath = relative;
            portable.Startup = StartupKind.Manual;
            string configPath = Path.Combine(staging, "config.json");
            JsonFiles.Write(configPath, portable);
            portable.Validate(configPath);
            WriteNotices(staging);
            string zipPath = Path.Combine(staging, "payload.zip");
            using (var zipStream = File.Create(zipPath))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                Add(zip, staging, "config.json");
                Add(zip, staging, relative);
                Add(zip, staging, "THIRD-PARTY.txt");
                Add(zip, staging, "third-party/GPL-2.0.txt");
                Add(zip, staging, "third-party/GPL-3.0.txt");
            }
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var input = File.OpenRead(executorFull)) input.CopyTo(output);
                using var payload = File.OpenRead(zipPath);
                PortablePackage.Append(output, payload);
            }
            File.Move(partial, destinationFull);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void Add(ZipArchive zip, string staging, string relative)
    {
        string source = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
        ZipFileExtensions.CreateEntryFromFile(zip, source, relative, CompressionLevel.Optimal);
    }

    private static void WriteNotices(string staging)
    {
        Directory.CreateDirectory(Path.Combine(staging, "third-party"));
        foreach (string notice in new[] { "THIRD-PARTY.txt", "GPL-2.0.txt", "GPL-3.0.txt" })
        {
            using var source = typeof(PortableBuilder).Assembly.GetManifestResourceStream("Surprise." + notice) ?? throw new IOException("Missing player license notice.");
            string relative = notice.StartsWith("GPL-", StringComparison.Ordinal) ? "third-party/" + notice : notice;
            using var target = File.Create(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            source.CopyTo(target);
        }
    }
}
