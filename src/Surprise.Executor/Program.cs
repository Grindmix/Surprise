using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.Executor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? configPath = null;
        PortableRuntime.Session? portable = null;
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            if (args.Length == 0)
            {
                portable = PortableRuntime.TryOpen();
                if (portable is null)
                    throw new ArgumentException("Use --watch --config <file>, --play --config <file>, --learn --pipe <name>, --probe --output <file>, or --version --output <file>.");
                configPath = portable.ConfigPath;
                var portableConfig = JsonFiles.Read<SurpriseConfig>(configPath);
                portableConfig.Validate(configPath);
                var portableApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                portableApp.DispatcherUnhandledException += (_, e) =>
                {
                    new LocalLog(Path.GetDirectoryName(configPath)!).Write(e.Exception.ToString());
                    e.Handled = true; portableApp.Shutdown(1);
                };
                using var watcher = new WatcherHost(portableApp, portableConfig, configPath, portableMode: true);
                if (!watcher.Start()) return 0;
                return portableApp.Run();
            }
            var options = Parse(args);
            if (options.ContainsKey("--version"))
            {
                JsonFiles.Write(Required(options, "--output"), new { protocol = Protocol.Version, version = typeof(Program).Assembly.GetName().Version?.ToString(3), executable = "Surprise.Executor", backend = "mpv" }); return 0;
            }
            if (options.ContainsKey("--probe"))
            {
                using var detector = new ControllerDetector();
                JsonFiles.Write(Required(options, "--output"), detector.Snapshot()); return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                if (configPath is not null) new LocalLog(Path.GetDirectoryName(configPath)!).Write(e.Exception.ToString());
                e.Handled = true; app.Shutdown(1);
            };
            if (options.ContainsKey("--learn"))
            {
                using var learner = new LearningHost(app, Required(options, "--pipe"), options.GetValueOrDefault("--output"));
                learner.Start(); return app.Run();
            }
            configPath = Path.GetFullPath(Required(options, "--config"));
            var config = JsonFiles.Read<SurpriseConfig>(configPath); config.Validate(configPath);
            if (options.ContainsKey("--watch"))
            {
                using var watcher = new WatcherHost(app, config, configPath);
                if (!watcher.Start()) return 0;
                return app.Run();
            }
            if (options.ContainsKey("--play"))
            {
                if (!SessionGuard.IsActiveUnlocked()) throw new InvalidOperationException("Playback requires an active, unlocked Windows session.");
                var player = new PlaybackWindow(config, configPath, options.GetValueOrDefault("--output"));
                player.Closed += (_, _) => app.Shutdown(player.OpenedSuccessfully ? 0 : 1);
                player.Show(); return app.Run();
            }
            throw new ArgumentException("Use --watch --config <file>, --play --config <file>, --learn --pipe <name>, --probe --output <file>, or --version --output <file>.");
        }
        catch (Exception ex)
        {
            if (configPath is not null) new LocalLog(Path.GetDirectoryName(configPath)!).Write(ex.ToString());
            int outputIndex = Array.IndexOf(args, "--output");
            bool diagnostic = args.Contains("--play") && outputIndex >= 0 && outputIndex + 1 < args.Length;
            if (diagnostic) JsonFiles.Write(args[outputIndex + 1], new { Backend = "mpv", Opened = false, Closed = true, Error = ex.Message });
            // Diagnostic commands must be non-interactive, including on failure.
            if (!diagnostic && !args.Contains("--probe") && !args.Contains("--version") && !args.Contains("--watch") && !args.Contains("--learn"))
                MessageBox.Show(ex.Message, "Surprise Executor", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { portable?.Dispose(); MpvBundle.Cleanup(); }
    }
    private static Dictionary<string, string?> Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string> { "--watch", "--play", "--learn", "--probe", "--version" };
        var valued = new HashSet<string> { "--config", "--pipe", "--output" };
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (!flags.Contains(key) && !valued.Contains(key)) throw new ArgumentException("Unknown argument: " + key);
            if (values.ContainsKey(key)) throw new ArgumentException("Duplicate argument: " + key);
            values[key] = flags.Contains(key) ? null : ++i < args.Length ? args[i] : throw new ArgumentException("Missing value: " + key);
        }
        if (values.Keys.Count(flags.Contains) != 1) throw new ArgumentException("Choose exactly one executor mode.");
        return values;
    }
    private static string Required(Dictionary<string, string?> options, string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing " + key);
}
