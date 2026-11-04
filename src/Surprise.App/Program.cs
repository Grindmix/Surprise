using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Surprise.Windows;
using Surprise.Executor;

namespace Surprise.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            DiagnosticLog.Initialize(AppContext.BaseDirectory);
            DiagnosticLog.Info("app.start", new { Executable = Environment.ProcessPath, BaseDirectory = AppContext.BaseDirectory, Arguments = args, Version = typeof(Program).Assembly.GetName().Version?.ToString(), OS = RuntimeInformation.OSDescription, Framework = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), WorkingDirectory = Environment.CurrentDirectory });
            AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagnosticLog.Write("FATAL", "app.unhandled", new { Exception = e.ExceptionObject.ToString(), e.IsTerminating });
            TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Error("app.unobserved-task", e.Exception);
            if (args.Length == 2 && args[0] == "--render-ui") return UiVerification.Render(args[1]);
            if (args.Length == 2 && args[0] == "--check-backend")
            {
                string backend = MpvBundle.Prepare();
                Surprise.Core.JsonFiles.Write(args[1], new { Ok = File.Exists(backend), Version = typeof(Program).Assembly.GetName().Version?.ToString(), Backend = "mpv + PNG scanlines" });
                return 0;
            }
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            DiagnosticLog.Info("display.environment", new { RenderTier = RenderCapability.Tier >> 16, Screens = System.Windows.Forms.Screen.AllScreens.Select(s => new { s.DeviceName, s.Bounds, s.WorkingArea, s.Primary }).ToArray() });
            string settings = Path.Combine(AppContext.BaseDirectory, "settings.json");
            int index = Array.IndexOf(args, "--settings");
            if (index >= 0) settings = Path.GetFullPath(args[index + 1]);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var host = new AppHost(application, settings);
            if (!host.Acquire()) { host.ActivateExisting(); return 0; }
            application.DispatcherUnhandledException += (_, e) => { DiagnosticLog.Error("app.dispatcher-exception", e.Exception); host.Report(e.Exception.Message); e.Handled = true; };
            application.Startup += (_, _) => host.Start(args.Contains("--background"));
            return application.Run();
        }
        catch (Exception ex) { DiagnosticLog.Error("app.startup-failed", ex); MessageBox.Show(ex.Message, "Surprise", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        finally { MpvBundle.Cleanup(); DiagnosticLog.Info("app.exit"); }
    }
}
