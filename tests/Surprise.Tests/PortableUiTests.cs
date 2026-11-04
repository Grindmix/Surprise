using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Surprise.Core;
using Surprise.Setup;

namespace Surprise.Tests;

internal static class PortableUiTests
{
    internal static void Run()
    {
        var empty = new PortableWindow(null, true);
        try { if (empty.CanCreate) throw new Exception("Creating an executor must require media."); }
        finally { empty.Close(); }
        foreach (bool overlay in new[] { false, true })
        {
            var window = new PortableWindow(null, true);
            try
            {
                window.RenderDemo(overlay);
                var config = window.CurrentConfig;
                if (!window.CanCreate || config.Trigger != TriggerKind.ControllerModel || config.Startup != StartupKind.Manual ||
                    config.Controllers.Count != 1 || !config.Controllers[0].HasModelIdentity ||
                    config.PlaybackMode != (overlay ? PlaybackMode.Overlay : PlaybackMode.Standard))
                    throw new Exception("The one-page creator did not preserve portable model/playback choices.");
                if (overlay)
                {
                    Find<TextBox>(window, "Ширина объекта, пикселей").Text = "invalid";
                    Find<ComboBox>(window, "Внешний вид").SelectedIndex = 0;
                    if (window.CurrentConfig.OverlayWidth != 320) throw new Exception("A hidden overlay field blocks fullscreen creation.");
                }
            }
            finally { window.Close(); }
        }
    }
    private static T Find<T>(DependencyObject root, string name) where T : DependencyObject
    {
        if (root is T control && AutomationProperties.GetName(control) == name) return control;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            try { return Find<T>(child, name); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Accessible control missing: " + name);
    }
}
