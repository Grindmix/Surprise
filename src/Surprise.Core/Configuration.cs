using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surprise.Core;

public enum TriggerKind { AnyController, SelectedController, ControllerModel }
public enum StartupKind { TaskScheduler, Manual }
public enum PlaybackMode { Standard, Overlay }
public enum OverlayAnchor { Custom, TopLeft, TopRight, Center, BottomLeft, BottomRight }
public sealed record ControllerIdentity(string Id, string Name, string Source, ushort VendorId = 0, ushort ProductId = 0)
{
    public bool HasModelIdentity => Source == "WGI" && VendorId != 0 && ProductId != 0;
    public bool MatchesModel(ControllerIdentity other) => HasModelIdentity && other.HasModelIdentity && VendorId == other.VendorId && ProductId == other.ProductId;
    public string MatchDescription => Source == "XInput"
        ? "XInput slot only; cannot identify a physical controller. Use Any controller."
        : "Application/PC/USB-port identity. Record USB and Bluetooth separately if needed.";
    public override string ToString() => $"{Name} [{Source}] — {Id}";
}

public sealed class SurpriseConfig
{
    public int SchemaVersion { get; set; } = 1;
    public Guid InstallationId { get; set; } = Guid.NewGuid();
    public TriggerKind Trigger { get; set; } = TriggerKind.AnyController;
    public List<ControllerIdentity> Controllers { get; set; } = [];
    public string MediaPath { get; set; } = "";
    public bool Fullscreen { get; set; } = true;
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Standard;
    public OverlayAnchor OverlayAnchor { get; set; } = OverlayAnchor.BottomRight;
    public int OverlayX { get; set; } = 100;
    public int OverlayY { get; set; } = 100;
    public int OverlayWidth { get; set; } = 320;
    public int OverlayHeight { get; set; } = 320;
    public int OverlayDurationSeconds { get; set; } = 10;
    public bool OverlayClickThrough { get; set; } = true;
    public bool Fill { get; set; }
    public string Monitor { get; set; } = "";
    public double Volume { get; set; } = 0.5;
    public int GifDurationSeconds { get; set; } = 10;
    public bool OneShot { get; set; }
    public int CooldownSeconds { get; set; } = 30;
    public StartupKind Startup { get; set; } = StartupKind.TaskScheduler;

    public void Validate(string configPath)
    {
        if (SchemaVersion != 1 || InstallationId == Guid.Empty) throw new InvalidDataException("Unsupported or invalid configuration.");
        if (!Enum.IsDefined(Trigger) || !Enum.IsDefined(Startup) || !Enum.IsDefined(PlaybackMode) || !Enum.IsDefined(OverlayAnchor)) throw new InvalidDataException("Invalid trigger/startup/playback mode.");
        if (OverlayWidth is < 16 or > 16384 || OverlayHeight is < 16 or > 16384 || OverlayX is < 0 or > 32768 || OverlayY is < 0 or > 32768 || OverlayDurationSeconds is < 1 or > 3600)
            throw new InvalidDataException("Overlay size must be 16–16384 pixels, position 0–32768 pixels, and duration 1–3600 seconds.");
        if (Volume < 0 || Volume > 1 || !double.IsFinite(Volume)) throw new InvalidDataException("Volume must be between 0 and 1.");
        if (GifDurationSeconds is < 1 or > 3600 || CooldownSeconds is < 0 or > 86400) throw new InvalidDataException("Invalid playback duration/cooldown.");
        if (Controllers is null || (Trigger != TriggerKind.AnyController && Controllers.Count == 0)) throw new InvalidDataException("Record at least one controller.");
        if (Trigger == TriggerKind.ControllerModel && Controllers.Any(c => !c.HasModelIdentity))
            throw new InvalidDataException("Model matching requires a Windows Gaming Input controller with nonzero vendor and product IDs.");
        if (Trigger == TriggerKind.SelectedController && Controllers.Any(c => c.Source != "WGI" || string.IsNullOrWhiteSpace(c.Id)))
            throw new InvalidDataException("Selected-controller matching requires a Windows Gaming Input identity. XInput slots are not physical identities.");
        if (string.IsNullOrWhiteSpace(MediaPath)) throw new InvalidDataException("Choose a media file.");
        string media = ResolveMedia(configPath);
        if (!File.Exists(media)) throw new FileNotFoundException("The configured media file is missing.", media);
        if (!SupportedExtensions.Contains(Path.GetExtension(media), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a supported video, GIF, PNG, APNG, or WebP file.");
    }

    public string ResolveMedia(string configPath) => Path.IsPathRooted(MediaPath) ? Path.GetFullPath(MediaPath) : SafePaths.ResolveUnder(Path.GetDirectoryName(Path.GetFullPath(configPath))!, MediaPath);
    public static readonly string[] SupportedExtensions = [".gif", ".mp4", ".wmv", ".avi", ".mov", ".mkv", ".m4v", ".webm", ".png", ".apng", ".webp"];
    public static bool IsImage(string path) => new[] { ".gif", ".png", ".apng", ".webp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public readonly record struct OverlayRectangle(int X, int Y, int Width, int Height);
public static class OverlayPlacement
{
    // Saved coordinates are physical pixels relative to the selected display.
    // Clamp to the current display after unplugging a screen or changing resolution.
    public static OverlayRectangle Resolve(SurpriseConfig config, int screenWidth, int screenHeight)
    {
        int width = Math.Clamp(config.OverlayWidth, 1, Math.Max(1, screenWidth));
        int height = Math.Clamp(config.OverlayHeight, 1, Math.Max(1, screenHeight));
        int maxX = Math.Max(0, screenWidth - width), maxY = Math.Max(0, screenHeight - height);
        int marginX = Math.Min(24, maxX), marginY = Math.Min(24, maxY);
        var (x, y) = config.OverlayAnchor switch
        {
            OverlayAnchor.TopLeft => (marginX, marginY),
            OverlayAnchor.TopRight => (maxX - marginX, marginY),
            OverlayAnchor.Center => (maxX / 2, maxY / 2),
            OverlayAnchor.BottomLeft => (marginX, maxY - marginY),
            OverlayAnchor.BottomRight => (maxX - marginX, maxY - marginY),
            _ => (Math.Clamp(config.OverlayX, 0, maxX), Math.Clamp(config.OverlayY, 0, maxY))
        };
        return new(x, y, width, height);
    }
}

public sealed class InstallManifest
{
    public int SchemaVersion { get; set; } = 1;
    public Guid InstallationId { get; set; }
    public string OwnerSid { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string Root { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public bool Paused { get; set; }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Empty JSON file.");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class SafePaths
{
    public static string ResolveUnder(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Expected a relative path.");
        string basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes the installation directory.");
        // Never follow junctions/symlinks while reading or removing managed assets.
        string? parent = full;
        while (parent is not null && parent.Length >= basePath.Length)
        {
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installation paths cannot contain symbolic links or junctions.");
            parent = Path.GetDirectoryName(parent);
        }
        return full;
    }
}
