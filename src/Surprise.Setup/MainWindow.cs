using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using Microsoft.Win32;
using Surprise.Core;
using Surprise.Windows;
using Forms = System.Windows.Forms;

namespace Surprise.Setup;

internal sealed class MainWindow : Window
{
    private readonly StackPanel page = new() { Margin = new Thickness(32, 22, 32, 16) };
    private readonly TextBlock stepLabel = new(), status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button back = Button("Back"), next = Button("Continue"), install = Button("Arm surprise"), export = Button("Create portable ZIP…");
    private readonly TextBox executorBox = new(), mediaBox = new(), storageBox = new();
    private readonly RadioButton any = new() { Content = "Any controller (recommended)", IsChecked = true, GroupName = "Trigger" }, selected = new() { Content = "Only a controller I choose", GroupName = "Trigger" }, model = new() { Content = "Only a controller model (portable to another PC)", GroupName = "Trigger" };
    private readonly ListBox devices = new() { Height = 70, DisplayMemberPath = "Name" }, profiles = new() { Height = 48 };
    private readonly TextBlock detection = Text(""), learnStatus = Text(""), previewStatus = Text("");
    private readonly Button findControllers = Button("Find controllers"), recordController = Button("Use this controller");
    private StackPanel? learningPanel;
    private readonly CheckBox fullscreen = new() { Content = "Fullscreen", IsChecked = true }, fill = new() { Content = "Fill screen (crop edges)" }, oneShot = new() { Content = "Play once until manually rearmed" };
    private readonly ComboBox monitor = new() { MinWidth = 320 }, startup = new() { MinWidth = 320 };
    private readonly Slider volume = new() { Minimum = 0, Maximum = 100, Value = 50, TickFrequency = 10, Width = 350, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock volumeLabel = Text("50%");
    private readonly TextBox duration = new() { Text = "10", Width = 100 }, cooldown = new() { Text = "30", Width = 100 };
    private readonly ComboBox playbackMode = new() { MinWidth = 320 }, anchor = new() { MinWidth = 260 };
    private readonly TextBox overlayX = new() { Text = "100", Width = 90 }, overlayY = new() { Text = "100", Width = 90 }, overlayWidth = new() { Text = "320", Width = 90 }, overlayHeight = new() { Text = "320", Width = 90 }, overlayDuration = new() { Text = "10", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox clickThrough = new() { Content = "Let clicks pass through to the desktop", IsChecked = true };
    private StackPanel? standardOptions, overlayOptions;
    private readonly Button stopPreview = Button("Stop preview");
    private Process? previewProcess;
    private bool previewStopped;
    private string? previewResult;
    private readonly ControllerLearningState learning = new();
    private List<ControllerIdentity> recorded => learning.Profiles;
    private HashSet<string> verifiedProfiles => learning.Verified;
    private LearningSession? learner;
    private InstallManifest? manifest;
    private Guid installationId = Guid.NewGuid();
    private string? previewFingerprint;
    private int step;
    private bool busy;
    private LearningPreparation? preparation;
    private string? learningRoot;
    private readonly bool renderOnly;
    private readonly TextBlock heading = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly string[] titles = ["A surprise when a controller connects", "Which controller?", "What should play?", "Ready to arm", "Your surprise is ready"];

    public MainWindow(string? executor, bool renderOnly = false)
    {
        this.renderOnly = renderOnly;
        Title = "Surprise Setup"; Width = 940; Height = 800; MinWidth = 740; MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14; Background = new SolidColorBrush(Color.FromRgb(246, 248, 250));
        Resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="Button">
                <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#233D48"/>
                <Setter Property="BorderBrush" Value="#B9C9CE"/><Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                  <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.82"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Frame" Property="Opacity" Value="0.45"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TextBox"><Setter Property="Padding" Value="9"/><Setter Property="BorderBrush" Value="#B9C9CE"/></Style>
              <Style TargetType="RadioButton"><Setter Property="Margin" Value="0,0,0,14"/><Setter Property="FontSize" Value="16"/></Style>
              <Style TargetType="CheckBox"><Setter Property="Margin" Value="0,0,0,12"/></Style>
              <Style TargetType="Expander"><Setter Property="Margin" Value="0,8,0,16"/></Style>
            </ResourceDictionary>
            """);
        foreach (var primary in new[] { next, install }) { primary.Background = new SolidColorBrush(Color.FromRgb(13, 118, 110)); primary.Foreground = Brushes.White; primary.BorderBrush = primary.Background; }
        var layout = new Grid { Background = Background }; layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(32, 24, 32, 24) };
        header.Children.Add(Text("S U R P R I S E", 12, Brushes.LightCyan)); header.Children.Add(heading);
        stepLabel.Foreground = Brushes.LightGray; stepLabel.Margin = new Thickness(0, 8, 0, 0); header.Children.Add(stepLabel);
        var banner = new Border { Background = new SolidColorBrush(Color.FromRgb(26, 49, 61)), Child = header }; layout.Children.Add(banner);
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var footer = new Grid { Margin = new Thickness(32, 8, 32, 20) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Margin = new Thickness(0, 0, 15, 0); status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(status);
        var nav = new StackPanel { Orientation = Orientation.Horizontal }; stopPreview.Visibility = Visibility.Collapsed; nav.Children.Add(stopPreview); nav.Children.Add(back); nav.Children.Add(next); nav.Children.Add(install); Grid.SetColumn(nav, 1); footer.Children.Add(nav); Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = layout;
        stopPreview.Click += async (_, _) => await StopPreviewAsync();
        back.Click += (_, _) => { if (step > 0) { step--; Render(); } };
        next.Click += async (_, _) => await RunAsync(async () => { await ValidateStepAsync(); step++; Render(); });
        install.Click += async (_, _) => await RunAsync(InstallAsync);
        export.Click += async (_, _) => await RunAsync(ExportAsync);
        findControllers.Click += async (_, _) => await RunAsync(StartLearningAsync);
        recordController.Click += async (_, _) => await RunAsync(() => { RecordSelected(); return Task.CompletedTask; });
        any.Checked += (_, _) => { selected.IsChecked = false; model.IsChecked = false; MediaChanged(); UpdateTriggerControls(); };
        selected.Checked += (_, _) => { any.IsChecked = false; model.IsChecked = false; MediaChanged(); UpdateTriggerControls(); };
        model.Checked += (_, _) => { any.IsChecked = false; selected.IsChecked = false; MediaChanged(); UpdateTriggerControls(); };
        devices.SelectionChanged += (_, _) => UpdateTriggerControls();
        mediaBox.TextChanged += (_, _) => MediaChanged();
        fullscreen.Checked += (_, _) => MediaChanged(); fullscreen.Unchecked += (_, _) => MediaChanged();
        fill.Checked += (_, _) => MediaChanged(); fill.Unchecked += (_, _) => MediaChanged();
        volume.ValueChanged += (_, _) => { volumeLabel.Text = $"{volume.Value:0}%"; MediaChanged(); }; duration.TextChanged += (_, _) => MediaChanged();
        cooldown.TextChanged += (_, _) => MediaChanged(); monitor.SelectionChanged += (_, _) => MediaChanged();
        oneShot.Checked += (_, _) => MediaChanged(); oneShot.Unchecked += (_, _) => MediaChanged();
        startup.SelectionChanged += (_, _) => MediaChanged();
        playbackMode.Items.Add("Video / animation in a player"); playbackMode.Items.Add("Transparent desktop overlay"); playbackMode.SelectedIndex = 0;
        foreach (string name in new[] { "Custom position", "Top left", "Top right", "Centre", "Bottom left", "Bottom right" }) anchor.Items.Add(name);
        anchor.SelectedIndex = (int)OverlayAnchor.BottomRight;
        playbackMode.SelectionChanged += (_, _) => { UpdatePlaybackControls(); MediaChanged(); };
        anchor.SelectionChanged += (_, _) => MediaChanged();
        foreach (var box in new[] { overlayX, overlayY, overlayWidth, overlayHeight, overlayDuration }) box.TextChanged += (_, _) => MediaChanged();
        clickThrough.Checked += (_, _) => MediaChanged(); clickThrough.Unchecked += (_, _) => MediaChanged();
        executorBox.Text = executor ?? LocateExecutor();
        storageBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Surprise", "Default");
        int displayNumber = 0;
        foreach (var screen in Forms.Screen.AllScreens) monitor.Items.Add(new ComboBoxItem { Content = $"Display {++displayNumber} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " · Main display" : "")}", Tag = screen.DeviceName });
        monitor.SelectedIndex = 0;
        startup.Items.Add("Automatically when I sign in (recommended)"); startup.Items.Add("Manual — I start it myself"); startup.SelectedIndex = 0;
        if (!renderOnly)
        {
            string portable = Path.Combine(AppContext.BaseDirectory, "portable-config.json");
            if (File.Exists(portable)) TryLoadPortable(portable);
            else if (File.Exists(Path.Combine(storageBox.Text, InstallationService.ManifestFile))) TryLoadInstallation(storageBox.Text);
        }
        Closed += (_, _) => { learner?.Dispose(); learner = null; preparation?.Dispose(); };
        Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Wait for the current operation to finish before closing setup."; } };
        Render();
    }

    private static string LocateExecutor()
    {
        string sibling = Path.Combine(AppContext.BaseDirectory, "Surprise.Executor.exe");
        if (File.Exists(sibling)) return sibling;
        // Development output directories have a stable relative relationship.
        string dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Surprise.Executor/bin", AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug", "net10.0-windows10.0.19041.0", "Surprise.Executor.exe"));
        return File.Exists(dev) ? dev : sibling;
    }
    private void Render()
    {
        page.Children.Clear(); learningPanel = null; heading.Text = titles[step];
        stepLabel.Text = step == 4 ? "Setup complete" : $"Step {step + 1} of 4 · Start → Controller → Media → Arm";
        back.Visibility = step == 4 ? Visibility.Collapsed : Visibility.Visible;
        next.Visibility = step < 3 ? Visibility.Visible : Visibility.Collapsed; install.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        next.Content = step == 0 ? "Get started" : step == 2 ? "Review surprise" : "Continue";
        status.Text = "";
        switch (step)
        {
            case 0: Welcome(); break;
            case 1: TriggerPage(); break;
            case 2: MediaPage(); break;
            case 3: Review(); break;
            case 4: Complete(); break;
        }
        UpdateNavigation();
    }
    internal void RenderDemoStep(int index)
    {
        if (!renderOnly) throw new InvalidOperationException("Only available in the render diagnostic.");
        mediaBox.Text = @"C:\Videos\maow.mp4";
        step = index; Render();
    }
    internal void RenderDemoLearning(int stage)
    {
        if (!renderOnly) throw new InvalidOperationException("Only available in the render diagnostic.");
        var controller = new ControllerIdentity("wgi:demo", "Xbox controller", "WGI");
        learning.Observe([controller]); learning.Record(controller);
        if (stage >= 1) learning.Observe([]);
        if (stage >= 2) { learning.Observe([controller]); learning.Observe([controller]); }
        selected.IsChecked = true; step = 1; Render();
        UpdateDetection(new(learning.Connected.ToList(), null));
    }
    internal void RenderDemoOverlay()
    {
        if (!renderOnly) throw new InvalidOperationException("Only available in the render diagnostic.");
        mediaBox.Text = @"C:\Animations\transparent-cat.gif"; playbackMode.SelectedIndex = 1; step = 2; Render();
    }
    private void Welcome()
    {
        Add(Text("Choose a controller. Pick a video or GIF. Preview it, then arm.", 21));
        Add(Text("When a controller connects by USB, Bluetooth, or a wireless adapter, your media plays. A small app waits in the notification area by the clock."));
        Add(Card("Ready in a few clicks", "Setup saves a copy of your media and starts waiting for controller connections. With the defaults, it starts again when you sign in. You can pause or remove it here at any time."));
        if (manifest is not null)
        {
            Label("Your installed surprise");
            Add(Text("Continue to change the media or settings, or use the controls below."));
            var manage = new WrapPanel();
            AddAction(manage, "Pause", async () => { await InstallationService.PauseAsync(manifest); await RefreshStatusAsync(); });
            AddAction(manage, "Resume", async () => { await InstallationService.ResumeAsync(manifest, false); await RefreshStatusAsync(); });
            AddAction(manage, "Rearm one-shot", async () => { await InstallationService.ResumeAsync(manifest, true); await RefreshStatusAsync(); });
            AddAction(manage, "Remove surprise", RemoveAsync); Add(manage);
            status.Text = manifest.Paused ? "Your surprise is paused." : "Installed surprise loaded.";
        }
        var advanced = new StackPanel();
        PanelAdd(advanced, Text("Keep the default settings for the quickest setup. Choose an empty local folder if you change the location."));
        PanelAdd(advanced, Text("Installation folder", 14));
        PanelAdd(advanced, PathRow(storageBox, "Choose…", () => { var folder = PickFolder("Choose an empty folder or the loaded installation"); if (folder is not null) storageBox.Text = folder; }));
        PanelAdd(advanced, Text("Start the surprise listener", 14)); PanelAdd(advanced, startup);
        PanelAdd(advanced, Text("Executor (found automatically beside Setup)", 14));
        PanelAdd(advanced, PathRow(executorBox, "Browse…", PickExecutor));
        var actions = new WrapPanel();
        AddAction(actions, "Open installation…", async () => { string? folder = PickFolder("Select the installed Surprise folder"); if (folder is not null) { LoadInstallation(folder); Render(); await RefreshStatusAsync(); } });
        AddAction(actions, "Import portable config…", async () => { var dialog = new OpenFileDialog { Filter = "Surprise portable config|portable-config.json|JSON|*.json" }; if (dialog.ShowDialog(this) == true) { LoadPortable(dialog.FileName); Render(); } await Task.CompletedTask; });
        PanelAdd(advanced, actions); Add(new Expander { Header = "Advanced settings & other installations", Content = advanced });
        Add(Text("No administrator access needed. Run Setup normally."));
        if (manifest is null) status.Text = "Keep both EXEs in the extracted folder.";
    }
    private void TriggerPage()
    {
        Add(any); Add(model); Add(selected);
        Add(Text("Model matching uses manufacturer and model IDs, so settings can transfer to another PC. Any unit of that model can trigger. The individual-controller option is tied to this PC."));
        learningPanel = new StackPanel();
        PanelAdd(learningPanel, Text("1. Connect your controller and click Find controllers.\n2. Select it below and click Use this controller.\n3. For an individual controller, disconnect it, wait for confirmation, then reconnect once. Model matching needs no reconnect verification."));
        var row = new WrapPanel();
        Detach(findControllers); Detach(recordController); row.Children.Add(findControllers); row.Children.Add(recordController);
        PanelAdd(learningPanel, row); PanelAdd(learningPanel, detection); PanelAdd(learningPanel, devices);
        PanelAdd(learningPanel, learnStatus); PanelAdd(learningPanel, profiles);
        var reset = new WrapPanel();
        AddAction(reset, "Clear saved controllers", () => { learning.Clear(); MediaChanged(); UpdateTriggerControls(); return Task.CompletedTask; });
        var help = new StackPanel();
        PanelAdd(help, Text("For an individual controller, verify using the same connection method and USB port. Model matching uses VID/PID; USB and Bluetooth may report different IDs. To trigger through both USB and Bluetooth, add each connection separately.\n\nIf the connected list does not change when you unplug the controller, a remapping app may be keeping a virtual controller alive. Close that app, or choose Any controller. Some older controllers cannot be selected individually but work with Any controller.")); PanelAdd(help, reset);
        PanelAdd(learningPanel, new Expander { Header = "Connection help & reset saved controllers", Content = help });
        Add(learningPanel); UpdateTriggerControls();
    }
    private void MediaPage()
    {
        Label("Video, animation, or image");
        Add(PathRow(mediaBox, "Choose media…", () => { var dialog = new OpenFileDialog { Filter = "Media|*.gif;*.png;*.apng;*.webp;*.mp4;*.wmv;*.avi;*.mov;*.mkv;*.m4v;*.webm|All files|*.*" }; if (dialog.ShowDialog(this) == true) mediaBox.Text = dialog.FileName; }));
        Label("How it appears"); Add(playbackMode);
        Label("Screen"); Add(monitor);
        standardOptions = new StackPanel(); PanelAdd(standardOptions, fullscreen);
        overlayOptions = new StackPanel();
        PanelAdd(overlayOptions, Text("Your object appears above the desktop, without a window border, then disappears. Use a GIF or image with a transparent background; this does not remove a background already in the file."));
        PanelAdd(overlayOptions, Text("Position", 14)); PanelAdd(overlayOptions, anchor);
        var place = Button("Position on screen…"); place.Click += (_, _) => PickOverlayPosition(); PanelAdd(overlayOptions, place);
        var size = new WrapPanel(); size.Children.Add(Text("Width (px) ")); size.Children.Add(overlayWidth); size.Children.Add(Text("  Height (px) ")); size.Children.Add(overlayHeight); PanelAdd(overlayOptions, size);
        PanelAdd(overlayOptions, Text("Appear for (seconds)", 14)); PanelAdd(overlayOptions, overlayDuration); PanelAdd(overlayOptions, clickThrough);
        var coordinates = new StackPanel(); PanelAdd(coordinates, Text("For Custom position: pixels from the selected screen's top-left corner."));
        var xy = new WrapPanel(); xy.Children.Add(Text("X ")); xy.Children.Add(overlayX); xy.Children.Add(Text("  Y ")); xy.Children.Add(overlayY); PanelAdd(coordinates, xy);
        PanelAdd(overlayOptions, new Expander { Header = "Exact coordinates", Content = coordinates });
        Add(standardOptions); Add(overlayOptions); UpdatePlaybackControls();
        Label("Volume"); Add(volume); Add(volumeLabel);
        var playback = new StackPanel();
        PanelAdd(standardOptions, fill);
        PanelAdd(playback, Text("GIF play time (seconds)", 14)); PanelAdd(playback, duration);
        PanelAdd(playback, Text("Wait between surprises (seconds)", 14)); PanelAdd(playback, cooldown); PanelAdd(playback, oneShot);
        PanelAdd(playback, Text("In player mode, videos play once and images loop for the time above. Overlays loop until their appearance time ends. A missing screen falls back to the main screen."));
        Add(new Expander { Header = "More playback options", Content = playback });
        var preview = Button("Preview surprise"); preview.Click += async (_, _) => await RunAsync(PreviewAsync); Add(preview); Add(previewStatus);
        Add(Text("Bundled mpv handles playback; no codec pack needed. Use Stop preview to finish early, or Escape in the player. Then click Review surprise."));
        UpdatePreviewStatus();
    }
    private void UpdatePlaybackControls()
    {
        bool overlay = playbackMode.SelectedIndex == 1;
        if (standardOptions is not null) standardOptions.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        if (overlayOptions is not null) overlayOptions.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PickOverlayPosition()
    {
        try
        {
            var config = ReadConfig();
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == config.Monitor) ?? Forms.Screen.PrimaryScreen!;
            var picker = new OverlayPlacementPicker(config, screen) { Owner = this };
            if (picker.ShowDialog() != true) return;
            anchor.SelectedIndex = (int)OverlayAnchor.Custom;
            overlayX.Text = picker.Placement.X.ToString(); overlayY.Text = picker.Placement.Y.ToString();
            overlayWidth.Text = picker.Placement.Width.ToString(); overlayHeight.Text = picker.Placement.Height.ToString();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Position overlay", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Review()
    {
        var config = ReadConfig();
        Add(Card("When", config.Trigger == TriggerKind.AnyController ? "Any controller connects" : string.Join("\n", recorded.Select(c => config.Trigger == TriggerKind.ControllerModel ? $"Model: {c.Name} (VID {c.VendorId:X4} / PID {c.ProductId:X4})" : c.Name))));
        string appearance = config.PlaybackMode == PlaybackMode.Overlay ? $"Transparent overlay · {anchor.SelectedItem} · {config.OverlayWidth} × {config.OverlayHeight}px · {config.OverlayDurationSeconds}s" : config.Fullscreen ? "Fullscreen" : "Windowed";
        Add(Card("Play", $"{Path.GetFileName(mediaBox.Text)}\n{appearance}\nVolume {config.Volume:P0} · {(config.OneShot ? "Play once until rearmed" : $"Repeat on reconnect, with a {config.CooldownSeconds}s wait")}"));
        Add(Card("Startup", config.Startup == StartupKind.TaskScheduler ? "Starts now, and automatically when you sign in." : "Starts now. After restarting Windows, start the listener manually."));
        Add(Text("Click Arm surprise to save your media and start waiting for a controller connection."));
        var details = new StackPanel(); PanelAdd(details, Text("Installed copies: " + storageBox.Text));
        PanelAdd(details, Text("Your original media is kept. Only this user's startup task and Surprise's owned files are changed."));
        Add(new Expander { Header = "Where files are saved", Content = details });
        var portable = new StackPanel(); PanelAdd(portable, Text("Make a ready-to-configure ZIP for another PC. It includes both apps, your media, and settings. On that PC, extract it and run Setup.")); PanelAdd(portable, export);
        Add(new Expander { Header = "Take this surprise to another PC", Content = portable });
    }

    private void Complete()
    {
        Add(Card("Armed and waiting", "Wait a few seconds, then disconnect and reconnect your controller to try it. A controller that was already connected during setup will not trigger until reconnected."));
        Add(Text("You can close Setup. The Surprise icon by the clock keeps waiting for controller connections."));
        Add(Text("To pause or remove the surprise, open Setup again. To preview or temporarily pause detection, right-click the Surprise tray icon."));
        var actions = new WrapPanel(); AddAction(actions, "Change settings", () => { step = 0; Render(); return Task.CompletedTask; });
        var close = Button("Close setup"); close.Click += (_, _) => Close(); actions.Children.Add(close); Add(actions);
    }

    private async Task ValidateStepAsync()
    {
        if (step == 0) { await InstallationService.VerifyExecutorAsync(executorBox.Text); ValidateStorage(); }
        if (step == 1 && model.IsChecked == true && !ModelsReady)
            throw new InvalidOperationException("Save at least one controller model with valid vendor and product IDs. Clear incompatible saved controllers first.");
        if (step == 1 && selected.IsChecked == true)
        {
            if (recorded.Count == 0 || recorded.Any(c => !verifiedProfiles.Contains(c.Id))) throw new InvalidOperationException("Add your controller, then disconnect and reconnect it once. Continue becomes available when it is verified.");
        }
        if (step >= 2) ReadConfig().Validate(Path.Combine(Path.GetTempPath(), "surprise-preview.json"));
        if (step == 2 && !PreviewIsCurrent()) throw new InvalidOperationException("Click Preview surprise before continuing.");
    }
    private SurpriseConfig ReadConfig()
    {
        if (!int.TryParse(duration.Text, out int seconds) || !int.TryParse(cooldown.Text, out int wait)) throw new InvalidDataException("Duration and cooldown must be whole numbers.");
        return new SurpriseConfig
        {
            InstallationId = installationId, Trigger = SelectedTrigger,
            Controllers = recorded.ToList(), MediaPath = mediaBox.Text, Fullscreen = fullscreen.IsChecked == true, Fill = fill.IsChecked == true,
            Monitor = (monitor.SelectedItem as ComboBoxItem)?.Tag as string ?? "", Volume = volume.Value / 100, GifDurationSeconds = seconds,
            PlaybackMode = playbackMode.SelectedIndex == 1 ? PlaybackMode.Overlay : PlaybackMode.Standard,
            OverlayAnchor = (OverlayAnchor)anchor.SelectedIndex, OverlayX = WholeNumber(overlayX), OverlayY = WholeNumber(overlayY),
            OverlayWidth = WholeNumber(overlayWidth), OverlayHeight = WholeNumber(overlayHeight), OverlayDurationSeconds = WholeNumber(overlayDuration), OverlayClickThrough = clickThrough.IsChecked == true,
            CooldownSeconds = wait, OneShot = oneShot.IsChecked == true, Startup = startup.SelectedIndex == 0 ? StartupKind.TaskScheduler : StartupKind.Manual
        };
    }
    private static int WholeNumber(TextBox box) => int.TryParse(box.Text, out int value) ? value : throw new InvalidDataException("Overlay position, size, and time must be whole numbers.");
    private string Fingerprint()
    {
        var config = ReadConfig();
        var file = new FileInfo(mediaBox.Text);
        return JsonSerializer.Serialize(config, JsonFiles.Options) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }
    private async Task PreviewAsync()
    {
        var config = ReadConfig(); config.MediaPath = Path.GetFullPath(mediaBox.Text);
        string folder = Path.Combine(Path.GetTempPath(), "Surprise-preview-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "config.json");
        try
        {
            config.Validate(file);
            await InstallationService.VerifyExecutorAsync(executorBox.Text);
            string fingerprint = Fingerprint();
            string result = Path.Combine(folder, "result.json");
            JsonFiles.Write(file, config); status.Text = "Preview running. Use Stop preview to finish early.";
            using var process = InstallationService.Start(executorBox.Text, "--play", "--config", file, "--output", result);
            previewProcess = process; previewResult = result; previewStopped = false; stopPreview.IsEnabled = true; stopPreview.Visibility = Visibility.Visible;
            await process.WaitForExitAsync();
            using var report = File.Exists(result) ? JsonDocument.Parse(File.ReadAllText(result)) : null;
            bool opened = report is not null && report.RootElement.TryGetProperty("Opened", out var openedValue) && openedValue.GetBoolean();
            string? error = report is not null && report.RootElement.TryGetProperty("Error", out var errorValue) ? errorValue.GetString() : null;
            if (previewStopped && !opened) throw new OperationCanceledException("Preview stopped before the file opened. Preview again to check playback.");
            if (!opened || (process.ExitCode != 0 && !previewStopped)) throw new IOException(error ?? "Preview did not open. Check the file and try again.");
            previewFingerprint = fingerprint; status.Text = "Preview passed. You can continue."; UpdatePreviewStatus();
        }
        finally { previewProcess = null; previewResult = null; stopPreview.Visibility = Visibility.Collapsed; Directory.Delete(folder, true); }
    }
    private async Task StopPreviewAsync()
    {
        var process = previewProcess; string? result = previewResult;
        if (process is null || result is null) return;
        stopPreview.IsEnabled = false;
        for (int attempt = 0; attempt < 200 && previewProcess == process && !process.HasExited; attempt++)
        {
            try
            {
                if (File.Exists(result))
                {
                    using var report = JsonDocument.Parse(File.ReadAllText(result));
                    if (report.RootElement.TryGetProperty("ControlWindow", out var handle) && handle.GetInt64() != 0 && PostMessage(new IntPtr(handle.GetInt64()), 0x0010, IntPtr.Zero, IntPtr.Zero))
                    { previewStopped = true; return; }
                }
            }
            catch (IOException) { }
            await Task.Delay(100);
        }
        stopPreview.IsEnabled = true;
    }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private async Task InstallAsync()
    {
        ValidateStorage(); var config = ReadConfig(); config.Validate(Path.Combine(storageBox.Text, "config.json"));
        if (config.Trigger == TriggerKind.SelectedController && recorded.Any(c => !verifiedProfiles.Contains(c.Id)))
            throw new InvalidOperationException("Verify all recorded controller profiles before arming.");
        if (previewFingerprint != Fingerprint()) await PreviewAsync();
        learner?.Dispose(); learner = null;
        preparation?.Dispose(); preparation = null;
        status.Text = "Installing and checking the listener…";
        manifest = await InstallationService.InstallAsync(storageBox.Text, config, mediaBox.Text, executorBox.Text);
        mediaBox.Text = config.ResolveMedia(InstallationService.ConfigPath(manifest.Root));
        step = 4; Render(); await RefreshStatusAsync();
    }
    private void ValidateStorage()
    {
        if (!Path.IsPathFullyQualified(storageBox.Text) || storageBox.Text.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Choose an absolute local installation path.");
        if (manifest is not null && !Path.GetFullPath(storageBox.Text).Equals(Path.GetFullPath(manifest.Root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("To relocate an installation, export it, remove the original, and import into the new folder.");
        if (selected.IsChecked == true && recorded.Count > 0 && learningRoot is not null && !Path.GetFullPath(storageBox.Text).Equals(learningRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installation folder changed. Clear and relearn controller profiles at the new location.");
        _ = SafePaths.ResolveUnder(storageBox.Text, "config.json");
    }
    private async Task ExportAsync()
    {
        if (previewFingerprint != Fingerprint()) await PreviewAsync();
        await InstallationService.VerifyExecutorAsync(executorBox.Text);
        string setup = Environment.ProcessPath!;
        // Export requires the self-contained published distribution, not a debug apphost + DLLs.
        if (File.Exists(Path.ChangeExtension(setup, ".runtimeconfig.json")) || File.Exists(Path.ChangeExtension(executorBox.Text, ".runtimeconfig.json")))
            throw new InvalidOperationException("Portable export requires the two published single-file EXEs. Run build/publish.ps1 first.");
        var dialog = new SaveFileDialog { Filter = "Portable ZIP|*.zip", FileName = "Surprise-portable.zip", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        status.Text = "Packing portable ZIP…";
        var config = ReadConfig(); string exe = executorBox.Text, media = mediaBox.Text;
        await Task.Run(() => PortableExporter.Export(dialog.FileName, setup, exe, config, media));
        status.Text = "Exported: " + dialog.FileName;
    }
    private async Task RemoveAsync()
    {
        if (manifest is null) return;
        if (MessageBox.Show(this, "Remove this surprise, its startup task, and its installed copies? Your original media is preserved.", "Remove surprise", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var leftovers = await InstallationService.RemoveAsync(manifest);
        if (leftovers.Count > 0) throw new IOException("Startup removed, but cleanup needs retry:\n" + string.Join("\n", leftovers));
        manifest = null; installationId = Guid.NewGuid(); learning.Clear(); mediaBox.Text = ""; previewFingerprint = null; step = 0;
        Render(); status.Text = "Surprise removed. Startup and installed files cleaned up.";
    }
    private async Task RefreshStatusAsync()
    {
        if (manifest is null) return;
        var reply = await RuntimeClient.SendAsync(manifest.InstallationId, "status");
        status.Text = reply?.Status ?? (manifest.Paused ? "Paused; listener stopped" : "Listener stopped — use Resume / repair");
    }
    private void TryLoadInstallation(string folder) { try { LoadInstallation(folder); } catch (Exception ex) { status.Text = ex.Message; } }
    private void LoadInstallation(string folder)
    {
        learner?.Dispose(); learner = null;
        preparation?.Dispose(); preparation = null; verifiedProfiles.Clear();
        var loaded = InstallationService.LoadManifest(folder);
        var config = JsonFiles.Read<SurpriseConfig>(InstallationService.ConfigPath(folder));
        if (config.InstallationId != loaded.InstallationId) throw new InvalidDataException("Manifest/config identity mismatch.");
        config.Validate(InstallationService.ConfigPath(folder));
        manifest = loaded; storageBox.Text = loaded.Root; installationId = loaded.InstallationId;
        learningRoot = Path.GetFullPath(loaded.Root);
        ApplyConfig(config, InstallationService.ConfigPath(folder));
        if (!File.Exists(executorBox.Text)) executorBox.Text = Path.Combine(folder, "Surprise.Executor.exe");
        // Previously recorded profiles remain valid for this PC; re-import does not grant this.
        foreach (var profile in recorded) verifiedProfiles.Add(profile.Id);
        status.Text = loaded.Paused ? "Existing installation: paused" : "Existing installation loaded";
    }
    private void TryLoadPortable(string file) { try { LoadPortable(file); } catch (Exception ex) { status.Text = ex.Message; } }
    internal void LoadPortable(string file)
    {
        var config = JsonFiles.Read<SurpriseConfig>(file); config.Validate(file);
        learner?.Dispose(); learner = null; preparation?.Dispose(); preparation = null; learningRoot = null;
        manifest = null; installationId = Guid.NewGuid(); ApplyConfig(config, file); verifiedProfiles.Clear();
        if (config.Trigger == TriggerKind.SelectedController) { recorded.Clear(); profiles.ItemsSource = null; }
        status.Text = config.Trigger == TriggerKind.ControllerModel ? "Portable configuration imported. Saved controller models are ready; preview and arm on this PC." : "Portable configuration imported. Relearn selected-controller profiles on this PC.";
    }
    private void ApplyConfig(SurpriseConfig config, string configFile)
    {
        playbackMode.SelectedIndex = config.PlaybackMode == PlaybackMode.Overlay ? 1 : 0;
        anchor.SelectedIndex = (int)config.OverlayAnchor;
        overlayX.Text = config.OverlayX.ToString(); overlayY.Text = config.OverlayY.ToString();
        overlayWidth.Text = config.OverlayWidth.ToString(); overlayHeight.Text = config.OverlayHeight.ToString();
        overlayDuration.Text = config.OverlayDurationSeconds.ToString(); clickThrough.IsChecked = config.OverlayClickThrough;
        mediaBox.Text = config.ResolveMedia(configFile); fullscreen.IsChecked = config.Fullscreen; fill.IsChecked = config.Fill;
        any.IsChecked = config.Trigger == TriggerKind.AnyController; selected.IsChecked = config.Trigger == TriggerKind.SelectedController; model.IsChecked = config.Trigger == TriggerKind.ControllerModel;
        recorded.Clear(); recorded.AddRange(config.Controllers); profiles.ItemsSource = recorded.ToList();
        volume.Value = config.Volume * 100; duration.Text = config.GifDurationSeconds.ToString(); cooldown.Text = config.CooldownSeconds.ToString();
        oneShot.IsChecked = config.OneShot; monitor.SelectedItem = monitor.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == config.Monitor); if (monitor.SelectedIndex < 0) monitor.SelectedIndex = 0;
        startup.SelectedIndex = config.Startup == StartupKind.TaskScheduler ? 0 : 1; previewFingerprint = null;
    }
    private async Task StartLearningAsync()
    {
        await InstallationService.VerifyExecutorAsync(executorBox.Text);
        learner?.Dispose(); learner = null; learning.ResetObservation();
        ValidateStorage();
        string finalRoot = Path.GetFullPath(storageBox.Text);
        if (selected.IsChecked == true && learningRoot is not null && !learningRoot.Equals(finalRoot, StringComparison.OrdinalIgnoreCase))
        { recorded.Clear(); verifiedProfiles.Clear(); profiles.ItemsSource = null; }
        learningRoot = finalRoot;
        preparation?.Dispose(); preparation = null;
        string learningExe;
        if (manifest is not null) learningExe = SafePaths.ResolveUnder(manifest.Root, "Surprise.Executor.exe");
        else { preparation = new LearningPreparation(finalRoot, executorBox.Text); learningExe = preparation.Executor; }
        var session = new LearningSession(learningExe); learner = session;
        session.Snapshot += snapshot => Dispatcher.InvokeAsync(() => { if (learner == session) UpdateDetection(snapshot); });
        session.Failed += error => Dispatcher.InvokeAsync(() =>
        {
            if (learner != session) return;
            learner.Dispose(); learner = null;
            detection.Text = "Detection stopped: " + error; UpdateTriggerControls();
        });
        _ = session.RunAsync(); detection.Text = "Looking for connected controllers…";
        UpdateTriggerControls();
    }
    private void UpdateDetection(ControllerSnapshot snapshot)
    {
        var current = snapshot.Controllers;
        learning.Observe(current);
        string? selection = (devices.SelectedItem as ControllerIdentity)?.Id;
        devices.ItemsSource = current; devices.SelectedItem = current.FirstOrDefault(c => c.Id == selection) ?? current.FirstOrDefault();
        detection.Text = snapshot.Warning ?? (current.Count == 0 ? "No controllers connected. Connect or turn on your controller." : $"{current.Count} controller(s) connected. Choose yours below.");
        UpdateTriggerControls();
    }
    private void RecordSelected()
    {
        if (devices.SelectedItem is not ControllerIdentity device) throw new InvalidOperationException("Select a connected controller.");
        if (model.IsChecked == true && (!device.HasModelIdentity || recorded.Any(c => c.MatchesModel(device))))
            throw new InvalidOperationException("Choose a controller with valid vendor/product IDs whose model has not already been saved.");
        learning.Record(device);
        MediaChanged(); UpdateTriggerControls();
    }
    private async Task RunAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true; page.IsEnabled = false; back.IsEnabled = next.IsEnabled = install.IsEnabled = export.IsEnabled = false;
        try { await operation(); }
        catch (OperationCanceledException ex) { status.Text = ex.Message; }
        catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "Surprise Setup", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; page.IsEnabled = true; UpdateTriggerControls(); UpdateNavigation(); }
    }
    private void PickExecutor() { var dialog = new OpenFileDialog { Filter = "Surprise executor|Surprise.Executor.exe|Executable|*.exe" }; if (dialog.ShowDialog(this) == true) executorBox.Text = dialog.FileName; }
    private string? PickFolder(string title) { var dialog = new OpenFolderDialog { Title = title }; return dialog.ShowDialog(this) == true ? dialog.FolderName : null; }
    private void Add(UIElement element) => PanelAdd(page, element);
    private static void PanelAdd(Panel panel, UIElement element) { Detach(element); if (element is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(element); }
    private static void Detach(UIElement element)
    {
        var parent = LogicalTreeHelper.GetParent(element);
        if (parent is Panel panel) panel.Children.Remove(element);
        else if (parent is Decorator decorator) decorator.Child = null;
        else if (parent is ContentControl content) content.Content = null;
    }
    private static Border Card(string title, string detail)
    {
        var content = new StackPanel(); PanelAdd(content, Text(title, 17)); PanelAdd(content, Text(detail));
        return new Border { Child = content, Padding = new Thickness(18, 15, 18, 1), CornerRadius = new CornerRadius(9), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(218, 228, 232)), BorderThickness = new Thickness(1) };
    }
    private void UpdateTriggerControls()
    {
        if (learningPanel is not null) learningPanel.Visibility = any.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
        findControllers.IsEnabled = !busy && learner is null;
        findControllers.Content = learner is null ? "Find controllers" : "Detection running";
        recordController.IsEnabled = !busy && devices.SelectedItem is ControllerIdentity c && learning.CanRecord(c) && (model.IsChecked != true || (c.HasModelIdentity && !recorded.Any(rule => rule.MatchesModel(c))));
        profiles.ItemsSource = recorded.Select(c => model.IsChecked == true ? $"{c.Name} — VID {c.VendorId:X4} / PID {c.ProductId:X4}{(c.HasModelIdentity ? "" : " — unavailable; clear and choose another")}" : c.Name + (verifiedProfiles.Contains(c.Id) ? " — Verified ✓" : " — Waiting for reconnect")).ToList();
        profiles.Visibility = recorded.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var pending = recorded.FirstOrDefault(c => !verifiedProfiles.Contains(c.Id));
        learnStatus.Text = pending is not null
            ? (learning.IsMissing(pending.Id) ? "Disconnected — now reconnect " + pending.Name + " the same way. Verification is automatic." : "Saved " + pending.Name + ". Disconnect it now and leave it off until this message changes to Disconnected.")
            : recorded.Count > 0 ? "Controller verified. You can continue, or add another connection method." : "Choose your controller and click Use this controller.";
        if (model.IsChecked == true)
            learnStatus.Text = ModelsReady ? "Controller model saved. You can continue or add another connection method. These model IDs transfer to another PC." : "Choose a connected controller and click Use this controller. Valid vendor/product IDs are required; clear incompatible saved profiles.";
        if (devices.SelectedItem is ControllerIdentity device && (device.Source == "XInput" || (model.IsChecked == true && !device.HasModelIdentity)) && recorded.Count == 0)
            learnStatus.Text = "This controller does not expose the required identity. Choose Any controller above.";
        UpdateNavigation();
    }
    private void MediaChanged() { previewFingerprint = null; UpdatePreviewStatus(); UpdateNavigation(); }
    internal bool CanContinue => next.IsEnabled;
    internal TriggerKind SelectedTrigger => model.IsChecked == true ? TriggerKind.ControllerModel : selected.IsChecked == true ? TriggerKind.SelectedController : TriggerKind.AnyController;
    private bool ModelsReady => recorded.Count > 0 && recorded.All(c => c.HasModelIdentity);
    private bool TriggerReady => any.IsChecked == true || (model.IsChecked == true ? ModelsReady : recorded.Count > 0 && recorded.All(c => verifiedProfiles.Contains(c.Id)));
    private bool PreviewIsCurrent()
    {
        if (previewFingerprint is null) return false;
        try { return previewFingerprint == Fingerprint(); } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
    private void UpdatePreviewStatus() => previewStatus.Text = PreviewIsCurrent() ? "✓ Preview passed. Ready to continue." : string.IsNullOrWhiteSpace(mediaBox.Text) ? "Choose a file to begin." : "Preview needed. Check your media with the settings above.";
    private void UpdateNavigation()
    {
        back.IsEnabled = !busy && step > 0;
        next.IsEnabled = !busy && (step != 1 || TriggerReady) && (step != 2 || PreviewIsCurrent());
        install.IsEnabled = export.IsEnabled = !busy;
        if (!busy && step == 1) status.Text = any.IsChecked == true ? "Ready. Any controller can trigger the surprise." : model.IsChecked == true && ModelsReady ? "Model saved. Click Continue." : selected.IsChecked == true && TriggerReady ? "Verified. Click Continue." : "Complete the controller check below to continue.";
        if (!busy && step == 2) status.Text = PreviewIsCurrent() ? "Ready. Click Review surprise." : "Choose your media, then click Preview surprise.";
    }
    private void Label(string text) => Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
    private static TextBlock Text(string text, double size = 14, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? new SolidColorBrush(Color.FromRgb(41, 53, 63)), TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5 };
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 0, 8, 8), MinHeight = 42 };
    private static Grid PathRow(TextBox box, string label, Action action)
    {
        if (box.Parent is Panel previous) previous.Children.Remove(box);
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.Padding = new Thickness(8); box.VerticalContentAlignment = VerticalAlignment.Center; row.Children.Add(box);
        var button = Button(label); button.Margin = new Thickness(10, 0, 0, 0); button.Click += (_, _) => action(); Grid.SetColumn(button, 1); row.Children.Add(button); return row;
    }
    private void AddAction(Panel row, string title, Func<Task> action) { var button = Button(title); button.Click += async (_, _) => await RunAsync(action); row.Children.Add(button); }
}
