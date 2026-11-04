using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Surprise.Windows;

namespace Surprise.App;

internal static class Ui
{
    public static ResourceDictionary Resources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="FontSize" Value="13"/><Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#22324A"/>
            <Setter Property="BorderBrush" Value="#DCE3EE"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Cursor" Value="Hand"/><Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="12" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.82"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#2563EB"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox"><Setter Property="Padding" Value="12,9"/><Setter Property="MinHeight" Value="40"/><Setter Property="BorderBrush" Value="#D8E1EE"/><Setter Property="Background" Value="#F8FAFD"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
          <Style TargetType="ComboBox"><Setter Property="MinHeight" Value="42"/><Setter Property="Padding" Value="10"/><Setter Property="BorderBrush" Value="#D8E1EE"/><Setter Property="Background" Value="White"/></Style>
          <Style TargetType="CheckBox"><Setter Property="Margin" Value="0,12,0,12"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
          <Style TargetType="ListBox"><Setter Property="Background" Value="#F8FAFD"/><Setter Property="BorderBrush" Value="#E0E7F1"/><Setter Property="Padding" Value="6"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/></Style>
        </ResourceDictionary>
        """);
    public static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    public static Button Button(string caption, Action action, bool primary = false)
    {
        var button = new Button { Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, Padding = new Thickness(18, 11, 18, 11), Margin = new Thickness(0, 0, 10, 8), MinHeight = 44, Background = primary ? Brush("#2563EB") : Brushes.White, Foreground = primary ? Brushes.White : Brush("#22324A") };
        button.Click += (_, _) => { DiagnosticLog.Info("ui.action", new { Caption = caption }); action(); }; return button;
    }
    public static Button Tile(string caption, string icon, bool selected, Action action)
    {
        var button = new SquareChoice { Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 0), Background = Brush(selected ? "#EAF1FF" : "#FFFFFF"), BorderBrush = Brush(selected ? "#2563EB" : "#DCE3EE"), BorderThickness = new Thickness(selected ? 2 : 1) };
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Stretch;
        button.SetResourceReference(FrameworkElement.StyleProperty, typeof(Button));
        var content = new Grid();
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(Icon(icon, selected ? "#2563EB" : "#677D9C"));
        center.Children.Add(new TextBlock { Text = caption, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush(selected ? "#1D4ED8" : "#22324A"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 12, 3, 0) });
        content.Children.Add(center);
        if (selected) content.Children.Add(new TextBlock { Text = "✓", Foreground = Brush("#2563EB"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
        button.Content = content; button.Click += (_, _) => action(); System.Windows.Automation.AutomationProperties.SetName(button, caption); return button;
    }
    public static UIElement Icon(string name, string color = "#2563EB")
    {
        string geometry = name switch
        {
            "video" => "M4,5 L20,5 Q22,5 22,7 L22,19 Q22,21 20,21 L4,21 Q2,21 2,19 L2,7 Q2,5 4,5 M10,9 L16,13 L10,17 Z",
            "image" => "M4,3 L22,3 L22,23 L2,23 L2,3 Z M3,19 L9,12 L14,17 L18,13 L22,18 M8,8 A1.5,1.5 0 1 0 8,8.1",
            "pixels" => "M2,3 L8,3 L8,9 L2,9 Z M11,3 L17,3 L17,9 L11,9 Z M2,12 L8,12 L8,18 L2,18 Z M11,12 L17,12 L17,18 L11,18 Z M20,12 L26,12 L26,18 L20,18 Z M11,21 L17,21 L17,27 L11,27 Z",
            "none" => "M4,4 L22,22 M22,4 L4,22",
            "random" => "M2,2 L7,2 L7,7 L2,7 Z M18,3 L23,3 L23,8 L18,8 Z M10,11 L15,11 L15,16 L10,16 Z M2,20 L7,20 L7,25 L2,25 Z M20,19 L25,19 L25,24 L20,24 Z",
            "wave" => "M2,7 Q7,1 12,7 Q17,13 24,7 M2,14 Q7,8 12,14 Q17,20 24,14 M2,21 Q7,15 12,21 Q17,27 24,21",
            "scatter" => "M2,2 L9,9 M2,2 L2,7 M2,2 L7,2 M25,2 L18,9 M25,2 L20,2 M25,2 L25,7 M2,25 L9,18 M2,25 L2,20 M2,25 L7,25 M25,25 L18,18 M25,25 L20,25 M25,25 L25,20 M11,11 L16,11 L16,16 L11,16 Z",
            "rise" => "M5,24 L5,8 M1,12 L5,8 L9,12 M14,19 L14,2 M10,6 L14,2 L18,6 M23,24 L23,8 M19,12 L23,8 L27,12",
            _ => "M3,3 L23,3 L23,23 L3,23 Z"
        };
        return new Viewbox { Width = 34, Height = 34, Child = new Canvas { Width = 28, Height = 28, Children = { new Path { Data = Geometry.Parse(geometry), Stroke = Brush(color), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round } } } };
    }
    public static Border Description(string title, string description)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Brush("#244C91"), Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = description, Foreground = Brush("#52647C"), TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        return new Border { Child = text, Padding = new Thickness(16), Background = Brush("#F0F5FF"), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 16, 0, 20) };
    }
    private sealed class SquareChoice : Button
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double side = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 150;
            base.MeasureOverride(new Size(side, side)); return new Size(side, side);
        }
    }
}
