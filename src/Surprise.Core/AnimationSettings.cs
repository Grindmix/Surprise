using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Surprise.Core;

public enum MediaKind { Video, Image, PixelArt }
public enum RevealEffect { None, Random, Wave, Scatter, Rise }

public sealed class AnimationSettings
{
    public int Version { get; set; } = 1;
    public MediaKind MediaKind { get; set; } = MediaKind.Video;
    public List<string> Files { get; set; } = [];
    public double FramesPerSecond { get; set; } = 1;
    // Playback time starts after the first frame has finished appearing.
    public double DurationSeconds { get; set; } = 10;
    public RevealEffect Effect { get; set; } = RevealEffect.None;
    public double RevealSeconds { get; set; } = 2;
    public int CellSize { get; set; } // 0: infer the uniform square grid from PNG color boundaries.
    public int Width { get; set; } = 600;
    public int Height { get; set; } = 480;
    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public string Monitor { get; set; } = "";
    public bool FullscreenVideo { get; set; } = true;
    public double Volume { get; set; } = .5;
    public int CooldownSeconds { get; set; } = 30;
    public ControllerIdentity? Controller { get; set; }
    public bool CloseNoticeShown { get; set; }

    public AnimationSettings Copy() => new()
    {
        Version = Version, MediaKind = MediaKind, Files = Files is null ? [] : [.. Files], FramesPerSecond = FramesPerSecond,
        DurationSeconds = DurationSeconds, Effect = Effect, RevealSeconds = RevealSeconds, CellSize = CellSize,
        Width = Width, Height = Height, X = X, Y = Y, Monitor = Monitor, FullscreenVideo = FullscreenVideo,
        Volume = Volume, CooldownSeconds = CooldownSeconds, Controller = Controller, CloseNoticeShown = CloseNoticeShown
    };
    public string Resolve(string file, string settingsPath) => Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, file));
    public void Validate(string settingsPath)
    {
        if (Version != 1 || !Enum.IsDefined(MediaKind) || !Enum.IsDefined(Effect)) throw new InvalidDataException("Неизвестная версия или тип настроек.");
        if (Files is null || Files.Count == 0 || Files.Count > 240 || (MediaKind != MediaKind.PixelArt && Files.Count != 1)) throw new InvalidDataException("Выбери медиа: один файл или до 240 PNG-кадров.");
        foreach (string file in Files)
        {
            string path = Resolve(file, settingsPath);
            if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден. Укажи новый путь на странице «Файлы»: " + file, path);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool supported = MediaKind switch { MediaKind.PixelArt => extension == ".png", MediaKind.Image => new[] { ".png", ".gif", ".apng", ".webp", ".jpg", ".jpeg", ".bmp" }.Contains(extension), _ => new[] { ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".avi", ".wmv" }.Contains(extension) };
            if (!supported) throw new InvalidDataException("Формат файла не соответствует выбранному типу медиа: " + file);
        }
        if (!double.IsFinite(FramesPerSecond) || FramesPerSecond is < .1 or > 30 || !double.IsFinite(DurationSeconds) || DurationSeconds is < 1 or > 3600 || !double.IsFinite(RevealSeconds) || RevealSeconds is < .1 or > 30)
            throw new InvalidDataException("Скорость: 0,1–30 FPS; длительность показа: 1–3600 с; появление: 0,1–30 с.");
        if (CellSize is < 0 or > 32768 || Width is < 16 or > 16384 || Height is < 16 or > 16384 || X is < 0 or > 32768 || Y is < 0 or > 32768) throw new InvalidDataException("Проверь размер клетки, размер объекта и его координаты.");
        if (CooldownSeconds is < 0 or > 86400 || !double.IsFinite(Volume) || Volume is < 0 or > 1) throw new InvalidDataException("Проверь громкость и паузу между срабатываниями.");
        if (Controller is not null && !Controller.HasModelIdentity) throw new InvalidDataException("У контроллера нет идентификатора модели. Выбери «Любой контроллер».");
    }
    public SurpriseConfig PlayerConfig(string settingsPath) => new()
    {
        MediaPath = Resolve(Files[0], settingsPath), Startup = StartupKind.Manual,
        PlaybackMode = MediaKind == MediaKind.Video && FullscreenVideo ? PlaybackMode.Standard : PlaybackMode.Overlay,
        Fullscreen = FullscreenVideo, Monitor = Monitor, Volume = Volume,
        OverlayAnchor = OverlayAnchor.Custom, OverlayX = X, OverlayY = Y, OverlayWidth = Width, OverlayHeight = Height,
        OverlayDurationSeconds = (int)Math.Ceiling(DurationSeconds), GifDurationSeconds = (int)Math.Ceiling(DurationSeconds),
        CooldownSeconds = CooldownSeconds, Trigger = Controller is null ? TriggerKind.AnyController : TriggerKind.ControllerModel,
        Controllers = Controller is null ? [] : [Controller], OneShot = false
    };
}

public static class AnimationClock
{
    public static int FrameAt(double playbackSeconds, double fps, int count) => count <= 1 ? 0 : (int)(Math.Floor(Math.Max(0, playbackSeconds) * fps) % count);
    public static double Delay(RevealEffect effect, int x, int y, int width, int height, uint seed)
    {
        if (effect == RevealEffect.Wave) return (x + y) / (double)Math.Max(1, width + height - 2) * .7;
        uint hash = seed ^ (uint)(x * 374761393) ^ (uint)(y * 668265263); hash = (hash ^ (hash >> 13)) * 1274126177; hash ^= hash >> 16;
        return hash / (double)uint.MaxValue * .7;
    }
    public static double Progress(double elapsed, double delay) => Math.Clamp((elapsed - delay) / Math.Max(.001, 1 - delay), 0, 1);
}
