using System;
using System.IO;
using System.Linq;
using System.Windows;
using Surprise.Executor;

namespace Surprise.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--check-backend")
            {
                string backend = MpvBundle.Prepare();
                Surprise.Core.JsonFiles.Write(args[1], new { Ok = File.Exists(backend), Version = typeof(Program).Assembly.GetName().Version?.ToString(), Backend = "mpv + PNG scanlines" });
                return 0;
            }
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            string settings = Path.Combine(AppContext.BaseDirectory, "settings.json");
            int index = Array.IndexOf(args, "--settings");
            if (index >= 0) settings = Path.GetFullPath(args[index + 1]);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var host = new AppHost(application, settings);
            if (!host.Acquire()) { host.ActivateExisting(); return 0; }
            application.DispatcherUnhandledException += (_, e) => { host.Report(e.Exception.Message); e.Handled = true; };
            application.Startup += (_, _) => host.Start(args.Contains("--background"));
            return application.Run();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Surprise", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        finally { MpvBundle.Cleanup(); }
    }
}
