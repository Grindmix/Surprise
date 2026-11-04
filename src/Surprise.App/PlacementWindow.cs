using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Surprise.Core;

namespace Surprise.App;

internal sealed class PlacementWindow : Window
{
    public OverlayRectangle Result { get; private set; }
    public PlacementWindow(AnimationSettings settings, ImageSource? source)
    {
        var display = PixelOverlay.Display(settings.Monitor); var initial = PixelOverlay.Placement(settings, display.Bounds.Width, display.Bounds.Height);
        Title = "Surprise — размер и положение"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)); Topmost = true; ShowInTaskbar = false;
        var canvas = new Canvas(); Content = canvas;
        var frame = new Border { BorderBrush = Brushes.DeepSkyBlue, BorderThickness = new Thickness(2), Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), Cursor = Cursors.SizeAll };
        var inside = new Grid(); frame.Child = inside;
        if (source is not null) { var image = new Image { Source = source, Stretch = Stretch.Uniform, IsHitTestVisible = false }; RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor); inside.Children.Add(image); }
        inside.Children.Add(new TextBlock { Text = "Перетащи объект · угол — размер", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
        var resize = new Thumb { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE };
        inside.Children.Add(resize); canvas.Children.Add(frame);
        resize.DragDelta += (_, e) => { frame.Width = Math.Clamp(frame.Width + e.HorizontalChange, 16, Math.Max(16, canvas.ActualWidth - Canvas.GetLeft(frame))); frame.Height = Math.Clamp(frame.Height + e.VerticalChange, 16, Math.Max(16, canvas.ActualHeight - Canvas.GetTop(frame))); };
        Point? drag = null;
        frame.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Thumb) return; drag = e.GetPosition(canvas); frame.CaptureMouse(); e.Handled = true; };
        frame.MouseMove += (_, e) => { if (drag is not Point old || e.LeftButton != MouseButtonState.Pressed) return; var p = e.GetPosition(canvas); Canvas.SetLeft(frame, Math.Clamp(Canvas.GetLeft(frame) + p.X - old.X, 0, Math.Max(0, canvas.ActualWidth - frame.Width))); Canvas.SetTop(frame, Math.Clamp(Canvas.GetTop(frame) + p.Y - old.Y, 0, Math.Max(0, canvas.ActualHeight - frame.Height))); drag = p; };
        frame.MouseLeftButtonUp += (_, _) => { drag = null; frame.ReleaseMouseCapture(); };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White }; Canvas.SetLeft(controls, 20); Canvas.SetTop(controls, 20); canvas.Children.Add(controls);
        var apply = new Button { Content = "Использовать это положение", Padding = new Thickness(16), Margin = new Thickness(8) };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16), Margin = new Thickness(8) }; controls.Children.Add(apply); controls.Children.Add(cancel);
        apply.Click += (_, _) => { var dpi = VisualTreeHelper.GetDpi(this); Result = new((int)Math.Round(Canvas.GetLeft(frame) * dpi.DpiScaleX), (int)Math.Round(Canvas.GetTop(frame) * dpi.DpiScaleY), Math.Max(16, (int)Math.Round(frame.Width * dpi.DpiScaleX)), Math.Max(16, (int)Math.Round(frame.Height * dpi.DpiScaleY))); DialogResult = true; };
        cancel.Click += (_, _) => DialogResult = false; KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        Loaded += (_, _) => { var b = display.Bounds; PixelOverlay.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), b.Left, b.Top, b.Width, b.Height, 0); var dpi = VisualTreeHelper.GetDpi(this); frame.Width = initial.Width / dpi.DpiScaleX; frame.Height = initial.Height / dpi.DpiScaleY; Canvas.SetLeft(frame, initial.X / dpi.DpiScaleX); Canvas.SetTop(frame, initial.Y / dpi.DpiScaleY); };
    }
}
