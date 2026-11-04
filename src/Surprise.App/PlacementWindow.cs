using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.App;

internal sealed class PlacementWindow : Window
{
    public OverlayRectangle Result { get; private set; }
    public PlacementWindow(AnimationSettings settings, ImageSource? source)
    {
        Resources = Ui.Resources(); FontFamily = new FontFamily("Segoe UI"); FontSize = 14; UseLayoutRounding = true;
        var display = PixelOverlay.Display(settings.Monitor); var initial = PixelOverlay.Placement(settings, display.Bounds.Width, display.Bounds.Height);
        DiagnosticLog.Info("placement.open", new { display.DeviceName, display.Bounds, Initial = initial });
        Title = "Surprise — размер и положение"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)); Topmost = true; ShowInTaskbar = false;
        var canvas = new Canvas(); Content = canvas;
        var frame = new Border { BorderBrush = Brushes.DeepSkyBlue, BorderThickness = new Thickness(2), Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), Cursor = Cursors.SizeAll };
        var inside = new Grid(); frame.Child = inside;
        if (source is not null) { var image = new Image { Source = source, Stretch = Stretch.Uniform, IsHitTestVisible = false }; RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor); inside.Children.Add(image); }
        inside.Children.Add(new TextBlock { Text = "Перетащи для перемещения", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(190, 15, 23, 42)), Padding = new Thickness(7), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
        var resize = new Thumb { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, ToolTip = "Потяни за угол. Shift — сохранить пропорции." };
        inside.Children.Add(resize); canvas.Children.Add(frame);
        double aspect = initial.Width / (double)initial.Height;
        resize.DragStarted += (_, _) => aspect = frame.Width / frame.Height;
        resize.DragDelta += (_, e) =>
        {
            bool locked = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var size = PlacementGeometry.Resize(frame.Width, frame.Height, e.HorizontalChange, e.VerticalChange, canvas.ActualWidth - Canvas.GetLeft(frame), canvas.ActualHeight - Canvas.GetTop(frame), locked, aspect);
            frame.Width = size.Width; frame.Height = size.Height;
        };
        resize.DragCompleted += (_, _) => DiagnosticLog.Info("placement.resized", new { frame.Width, frame.Height, Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 });
        Point? drag = null;
        frame.MouseLeftButtonDown += (_, e) => { if (resize.IsMouseOver) return; drag = e.GetPosition(canvas); frame.CaptureMouse(); e.Handled = true; };
        frame.MouseMove += (_, e) => { if (drag is not Point old || e.LeftButton != MouseButtonState.Pressed) return; var p = e.GetPosition(canvas); Canvas.SetLeft(frame, Math.Clamp(Canvas.GetLeft(frame) + p.X - old.X, 0, Math.Max(0, canvas.ActualWidth - frame.Width))); Canvas.SetTop(frame, Math.Clamp(Canvas.GetTop(frame) + p.Y - old.Y, 0, Math.Max(0, canvas.ActualHeight - frame.Height))); drag = p; };
        frame.MouseLeftButtonUp += (_, _) => { drag = null; frame.ReleaseMouseCapture(); };
        var controls = new StackPanel();
        controls.Children.Add(new TextBlock { Text = "Расположи сюрприз на экране", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.MidnightBlue });
        controls.Children.Add(new TextBlock { Text = "Перетаскивай объект мышью. Потяни за угол для изменения размера.\nУдерживай Shift, чтобы сохранить пропорции.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 10) });
        var buttons = new WrapPanel(); controls.Children.Add(buttons);
        var bar = new Border { Child = controls, Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(20) }; Canvas.SetLeft(bar, 20); Canvas.SetTop(bar, 20); canvas.Children.Add(bar);
        var center = new Button { Content = "Отцентрировать", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 0) };
        var apply = new Button { Content = "Использовать положение", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 0), Background = Ui.Brush("#2563EB"), Foreground = Brushes.White };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(14, 10, 14, 10) }; buttons.Children.Add(center); buttons.Children.Add(apply); buttons.Children.Add(cancel);
        center.Click += (_, _) => { var p = PlacementGeometry.Center(canvas.ActualWidth, canvas.ActualHeight, frame.Width, frame.Height); Canvas.SetLeft(frame, p.X); Canvas.SetTop(frame, p.Y); DiagnosticLog.Info("placement.centered", new { p.X, p.Y, frame.Width, frame.Height }); };
        apply.Click += (_, _) => { var dpi = VisualTreeHelper.GetDpi(this); Result = new((int)Math.Round(Canvas.GetLeft(frame) * dpi.DpiScaleX), (int)Math.Round(Canvas.GetTop(frame) * dpi.DpiScaleY), Math.Max(16, (int)Math.Round(frame.Width * dpi.DpiScaleX)), Math.Max(16, (int)Math.Round(frame.Height * dpi.DpiScaleY))); DiagnosticLog.Info("placement.applied", Result); DialogResult = true; };
        cancel.Click += (_, _) => DialogResult = false; KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        Loaded += (_, _) => { var b = display.Bounds; PixelOverlay.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), b.Left, b.Top, b.Width, b.Height, 0); var dpi = VisualTreeHelper.GetDpi(this); frame.Width = initial.Width / dpi.DpiScaleX; frame.Height = initial.Height / dpi.DpiScaleY; Canvas.SetLeft(frame, initial.X / dpi.DpiScaleX); Canvas.SetTop(frame, initial.Y / dpi.DpiScaleY); bar.Width = Math.Min(640, Math.Max(200, canvas.ActualWidth - 40)); };
        Closed += (_, _) => DiagnosticLog.Info("placement.closed", new { Applied = DialogResult == true });
    }
}
