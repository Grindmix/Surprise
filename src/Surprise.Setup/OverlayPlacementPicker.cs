using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surprise.Core;
using Forms = System.Windows.Forms;

namespace Surprise.Setup;

// A temporary placement editor. Playback itself is handled by mpv.
internal sealed class OverlayPlacementPicker : Window
{
    private readonly Canvas canvas = new();
    private readonly Border frame = new() { BorderBrush = Brushes.Cyan, BorderThickness = new Thickness(2), Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), Cursor = Cursors.SizeAll };
    private readonly Forms.Screen screen;
    private readonly OverlayRectangle initial;
    private Point? drag;
    public OverlayRectangle Placement { get; private set; }

    public OverlayPlacementPicker(SurpriseConfig config, Forms.Screen screen)
    {
        this.screen = screen; initial = OverlayPlacement.Resolve(config, screen.Bounds.Width, screen.Bounds.Height);
        Title = "Place your overlay"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)); Topmost = true; ShowInTaskbar = false;
        Content = canvas; canvas.Children.Add(frame);
        var inside = new Grid(); frame.Child = inside;
        try
        {
            if (SurpriseConfig.IsImage(config.MediaPath) && File.Exists(config.MediaPath))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(Path.GetFullPath(config.MediaPath)); bitmap.EndInit(); bitmap.Freeze();
                inside.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, IsHitTestVisible = false });
            }
        }
        catch (Exception) { /* A frame is enough to place unsupported preview thumbnails. */ }
        inside.Children.Add(new TextBlock { Text = "Drag to move", Foreground = Brushes.White, Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4), IsHitTestVisible = false });
        var resize = new Thumb { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE };
        inside.Children.Add(resize);
        resize.DragDelta += (_, e) => { frame.Width = Math.Clamp(frame.Width + e.HorizontalChange, 16, Math.Max(16, canvas.ActualWidth - Canvas.GetLeft(frame))); frame.Height = Math.Clamp(frame.Height + e.VerticalChange, 16, Math.Max(16, canvas.ActualHeight - Canvas.GetTop(frame))); };
        frame.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Thumb) return; drag = e.GetPosition(canvas); frame.CaptureMouse(); e.Handled = true; };
        frame.MouseMove += (_, e) =>
        {
            if (drag is not Point previous || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(canvas);
            Canvas.SetLeft(frame, Math.Clamp(Canvas.GetLeft(frame) + point.X - previous.X, 0, Math.Max(0, canvas.ActualWidth - frame.Width)));
            Canvas.SetTop(frame, Math.Clamp(Canvas.GetTop(frame) + point.Y - previous.Y, 0, Math.Max(0, canvas.ActualHeight - frame.Height)));
            drag = point;
        };
        frame.MouseLeftButtonUp += (_, _) => { drag = null; frame.ReleaseMouseCapture(); };
        var controls = new StackPanel { Background = Brushes.White, Margin = new Thickness(20), Orientation = Orientation.Horizontal };
        controls.Children.Add(new TextBlock { Text = "Drag the frame; drag its bottom-right corner to resize.", Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center });
        var apply = new Button { Content = "Use this position", Margin = new Thickness(6), Padding = new Thickness(12) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(6), Padding = new Thickness(12) };
        controls.Children.Add(apply); controls.Children.Add(cancel); canvas.Children.Add(controls);
        apply.Click += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Placement = new((int)Math.Round(Canvas.GetLeft(frame) * dpi.DpiScaleX), (int)Math.Round(Canvas.GetTop(frame) * dpi.DpiScaleY), Math.Max(16, (int)Math.Round(frame.Width * dpi.DpiScaleX)), Math.Max(16, (int)Math.Round(frame.Height * dpi.DpiScaleY)));
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        Loaded += (_, _) =>
        {
            var b = screen.Bounds;
            SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), b.Left, b.Top, b.Width, b.Height, 0);
            var dpi = VisualTreeHelper.GetDpi(this);
            frame.Width = initial.Width / dpi.DpiScaleX; frame.Height = initial.Height / dpi.DpiScaleY;
            Canvas.SetLeft(frame, initial.X / dpi.DpiScaleX); Canvas.SetTop(frame, initial.Y / dpi.DpiScaleY);
        };
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
