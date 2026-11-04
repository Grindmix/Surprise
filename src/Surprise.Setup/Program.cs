using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Non-interactive release check: proves Setup contains a runnable executor without fallbacks.
        if (args.Length == 2 && args[0] == "--check-bundle")
        {
            try
            {
                using var embedded = typeof(Program).Assembly.GetManifestResourceStream("Surprise.bundled.executor.exe") ?? throw new IOException("Setup has no embedded executor.");
                string path = ExecutorBundle.Prepare();
                InstallationService.VerifyExecutorAsync(path).GetAwaiter().GetResult();
                JsonFiles.Write(args[1], new { Ok = true, Protocol = Protocol.Version, Embedded = true });
                return 0;
            }
            catch (Exception ex) { JsonFiles.Write(args[1], new { Ok = false, Error = ex.Message }); return 1; }
            finally { ExecutorBundle.Cleanup(); }
        }
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            string? executor = null, render = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--executor" && i + 1 < args.Length) executor = args[++i];
                else if (args[i] == "--render-demo" && i + 1 < args.Length) render = args[++i];
                else throw new ArgumentException("Usage: Surprise.Setup.exe [--executor <Surprise.Executor.exe>] [--render-demo <PNG path>]");
            }
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "Surprise Setup", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
            var window = new PortableWindow(executor, render is not null);
            if (render is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(render))!);
                window.RenderDemo();
                RenderPage(window, render, 780, 850);
                window.Close();
                var overlayWindow = new PortableWindow(executor, true); overlayWindow.RenderDemo(true);
                RenderPage(overlayWindow, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(render))!, Path.GetFileNameWithoutExtension(render) + "-overlay.png"), 780, 1200);
                overlayWindow.Close(); return 0;
            }
            return app.Run(window);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Surprise Setup", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        finally { ExecutorBundle.Cleanup(); }
    }
    private static void RenderPage(Window window, string file, int width, int height)
    {
        var element = RenderRoot(window);
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }
    private static FrameworkElement RenderRoot(Window window)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        var root = new System.Windows.Controls.Border { Child = content, Resources = window.Resources, Background = window.Background };
        System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
        return root;
    }
}
