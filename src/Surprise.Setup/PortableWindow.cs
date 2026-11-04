using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using Surprise.Core;
using Surprise.Windows;
using Forms = System.Windows.Forms;

namespace Surprise.Setup;

// The portable product has one page and one output. Legacy installations are not changed here.
internal sealed class PortableWindow : Window
{
    private readonly TextBlock mediaName = Text("Файл ещё не выбран"), deviceHint = Text(""), status = Text("");
    private readonly ComboBox controllers = new() { MinHeight = 38 }, appearance = new() { MinHeight = 36 }, screen = new() { MinHeight = 36 };
    private readonly Slider volume = new() { Minimum = 0, Maximum = 100, Value = 50, TickFrequency = 10 };
    private readonly TextBox seconds = new() { Text = "10" }, cooldown = new() { Text = "30" }, width = new() { Text = "320" }, height = new() { Text = "320" };
    private readonly CheckBox once = new() { Content = "Только один раз за запуск" };
    private readonly Button create = Button("Создать executor…"), preview = Button("Проверить медиа"), stop = Button("Остановить"), choose = Button("Выбрать файл…");
    private readonly StackPanel form = new(), overlayOptions = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ControllerDetector? detector;
    private string? media, output, executor;
    private Process? previewProcess;
    private string? previewResult;
    private bool busy, closing;
    private OverlayAnchor overlayAnchor = OverlayAnchor.BottomRight;
    private int overlayX = 100, overlayY = 100;
    private readonly bool demo;

    public PortableWindow(string? executorOverride = null, bool demo = false)
    {
        this.demo = demo; executor = executorOverride;
        Title = "Surprise — создать executor";
        Width = Math.Min(780, SystemParameters.WorkArea.Width); Height = Math.Min(850, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(540, Width); MinHeight = Math.Min(430, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14; Background = new SolidColorBrush(Color.FromRgb(244, 246, 250));
        Foreground = new SolidColorBrush(Color.FromRgb(30, 42, 58));
        Resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="Button">
                <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#1E2A3A"/>
                <Setter Property="BorderBrush" Value="#CBD5E1"/><Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                  <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.8"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TextBox"><Setter Property="Padding" Value="8"/><Setter Property="MinHeight" Value="34"/><Setter Property="BorderBrush" Value="#CBD5E1"/></Style>
              <Style TargetType="CheckBox"><Setter Property="Margin" Value="0,8,0,16"/></Style>
            </ResourceDictionary>
            """);
        var shell = new DockPanel { Margin = new Thickness(32) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(Text("S U R P R I S E", 12, Brushes.Teal));
        header.Children.Add(Text("Собери сюрприз в один файл", 28));
        header.Children.Add(Text("Выбери медиа и контроллер. Готовый executor можно отправить на другой ПК и запустить двойным кликом."));
        shell.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        create.Background = Brushes.Teal; create.Foreground = Brushes.White; create.HorizontalAlignment = HorizontalAlignment.Stretch;
        footer.Children.Add(create); footer.Children.Add(status);
        footer.Children.Add(Text("Без установки и автозапуска. Чтобы удалить: «Выход» в трее → удалить .exe.", 12));
        shell.Children.Add(footer);
        shell.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = shell;

        var mediaPanel = Section("1", "Что показать?");
        mediaName.FontWeight = FontWeights.SemiBold; mediaPanel.Children.Add(mediaName);
        mediaPanel.Children.Add(Text("Видео, GIF или картинка. Для прозрачного объекта нужен файл с прозрачностью.", 12));
        var mediaButtons = new WrapPanel(); mediaButtons.Children.Add(choose); mediaButtons.Children.Add(preview); stop.Visibility = Visibility.Collapsed; mediaButtons.Children.Add(stop); mediaPanel.Children.Add(mediaButtons);
        var triggerPanel = Section("2", "Какой контроллер?");
        controllers.Items.Add("Любой контроллер"); controllers.SelectedIndex = 0; AutomationProperties.SetName(controllers, "Модель контроллера"); triggerPanel.Children.Add(controllers); triggerPanel.Children.Add(deviceHint);
        triggerPanel.Children.Add(Text("Подключи нужную модель — она появится в списке. Повторная настройка на другом ПК не нужна. Для USB и Bluetooth IDs могут отличаться.", 12));
        var options = new StackPanel();
        appearance.Items.Add("На весь экран"); appearance.Items.Add("Прозрачный объект на рабочем столе"); appearance.SelectedIndex = 0;
        Field(options, "Внешний вид", appearance);
        foreach (var display in Forms.Screen.AllScreens) screen.Items.Add(new ComboBoxItem { Content = $"Экран {screen.Items.Count + 1} · {display.Bounds.Width} × {display.Bounds.Height}", Tag = display.DeviceName });
        screen.SelectedIndex = 0; Field(options, "Экран", screen); Field(options, "Громкость", volume);
        Field(options, "Длительность картинок, GIF и объектов на рабочем столе, секунд", seconds); Field(options, "Пауза между срабатываниями, секунд", cooldown); options.Children.Add(once);
        Field(overlayOptions, "Ширина объекта, пикселей", width); Field(overlayOptions, "Высота объекта, пикселей", height);
        var position = Button("Расположить на экране…"); position.Click += (_, _) => Safe(() =>
        {
            var config = Config(); var display = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == config.Monitor) ?? Forms.Screen.PrimaryScreen!;
            var picker = new OverlayPlacementPicker(config, display) { Owner = this };
            if (picker.ShowDialog() == true) { overlayAnchor = OverlayAnchor.Custom; overlayX = picker.Placement.X; overlayY = picker.Placement.Y; width.Text = picker.Placement.Width.ToString(); height.Text = picker.Placement.Height.ToString(); }
        }); overlayOptions.Children.Add(position); options.Children.Add(overlayOptions); overlayOptions.Visibility = Visibility.Collapsed;
        form.Children.Add(new Expander { Header = "Настроить внешний вид и поведение", Content = options, Margin = new Thickness(0, 12, 0, 8), Padding = new Thickness(8) });
        appearance.SelectionChanged += (_, _) => overlayOptions.Visibility = appearance.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        choose.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Медиа|*.mp4;*.webm;*.mkv;*.mov;*.m4v;*.avi;*.wmv;*.gif;*.png;*.apng;*.webp" }; if (dialog.ShowDialog(this) == true) { media = dialog.FileName; mediaName.Text = Path.GetFileName(media); mediaName.ToolTip = media; UpdateActions(); } };
        controllers.SelectionChanged += (_, _) => UpdateActions();
        preview.Click += async (_, _) => await Run(PreviewAsync);
        stop.Click += async (_, _) => await StopPreviewAsync();
        create.Click += async (_, _) => await Run(CreateAsync);
        Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Дождись завершения. Предпросмотр можно остановить кнопкой «Остановить»."; } };
        Closed += (_, _) => { closing = true; timer.Stop(); detector?.Dispose(); };
        if (!demo) { detector = new ControllerDetector(); timer.Tick += (_, _) => RefreshControllers(); RefreshControllers(); timer.Start(); }
        UpdateActions();
    }

    private StackPanel Section(string number, string title)
    {
        var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(Text(number + "   " + title, 19));
        form.Children.Add(new Border { Child = panel, Background = Brushes.White, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 16, 0, 0) }); return panel;
    }
    private void RefreshControllers()
    {
        if (busy || controllers.IsDropDownOpen) return;
        var snapshot = detector!.Snapshot();
        // Keep a chosen model after unplugging: the generated rule is portable.
        foreach (var device in snapshot.Controllers.Where(c => c.HasModelIdentity))
            if (!controllers.Items.OfType<ComboBoxItem>().Any(i => ((ControllerIdentity)i.Tag).MatchesModel(device)))
                controllers.Items.Add(new ComboBoxItem { Content = device.Name + $" · {device.VendorId:X4}:{device.ProductId:X4}", Tag = device });
        if (snapshot.Warning is not null) deviceHint.Text = "Не удалось получить модели: " + snapshot.Warning;
        else UpdateActions();
    }
    private void UpdateActions()
    {
        create.IsEnabled = preview.IsEnabled = !busy && media is not null; choose.IsEnabled = !busy;
        controllers.IsEnabled = !busy;
        deviceHint.Text = controllers.SelectedItem is ComboBoxItem ? "Сработает любой экземпляр выбранной модели." : "Сработает подключение любого распознанного контроллера.";
    }
    private SurpriseConfig Config()
    {
        if (media is null) throw new InvalidOperationException("Сначала выбери медиафайл.");
        var model = (controllers.SelectedItem as ComboBoxItem)?.Tag as ControllerIdentity;
        int duration = Number(seconds), wait = Number(cooldown);
        return new SurpriseConfig { Trigger = model is null ? TriggerKind.AnyController : TriggerKind.ControllerModel, Controllers = model is null ? [] : [model],
            MediaPath = Path.GetFullPath(media), Startup = StartupKind.Manual, PlaybackMode = appearance.SelectedIndex == 1 ? PlaybackMode.Overlay : PlaybackMode.Standard,
            Fullscreen = true, Monitor = (screen.SelectedItem as ComboBoxItem)?.Tag as string ?? "", Volume = volume.Value / 100,
            GifDurationSeconds = duration, OverlayDurationSeconds = duration, CooldownSeconds = wait, OneShot = once.IsChecked == true,
            OverlayWidth = appearance.SelectedIndex == 1 ? Number(width) : 320, OverlayHeight = appearance.SelectedIndex == 1 ? Number(height) : 320, OverlayAnchor = overlayAnchor, OverlayX = overlayX, OverlayY = overlayY, OverlayClickThrough = true };
    }
    private static int Number(TextBox box) => int.TryParse(box.Text, out int value) ? value : throw new InvalidDataException("Введи целое число в настройках времени и размера.");
    private async Task<string> ExecutorAsync()
    {
        executor ??= await Task.Run(ExecutorBundle.Prepare);
        await InstallationService.VerifyExecutorAsync(executor); return executor;
    }
    private async Task CreateAsync()
    {
        var config = Config(); config.Validate(Path.Combine(Path.GetTempPath(), "surprise-check.json"));
        var dialog = new SaveFileDialog { Title = "Куда сохранить готовый executor?", Filter = "Приложение Windows|*.exe", FileName = "MySurprise.exe", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName)) throw new IOException("Выбери новое имя: существующие файлы не перезаписываются.");
        status.Text = "Собираю executor с медиа внутри…";
        string source = await ExecutorAsync();
        await Task.Run(() => PortableBuilder.Build(dialog.FileName, source, config, media!));
        output = dialog.FileName;
        status.Text = "Готово: " + output + "\nПеренеси этот .exe на нужный ПК и запусти. Затем переподключи контроллер.";
        var reveal = Button("Показать файл"); reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + output + "\"", UseShellExecute = true });
        // Only one completion action, even when several executors are generated.
        if (form.Children.OfType<Button>().FirstOrDefault() is Button previous) form.Children.Remove(previous);
        form.Children.Add(reveal);
    }
    private async Task PreviewAsync()
    {
        var config = Config(); string root = Path.Combine(Path.GetTempPath(), "Surprise-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string file = Path.Combine(root, "config.json"); previewResult = Path.Combine(root, "result.json");
        try
        {
            config.Validate(file); string source = await ExecutorAsync(); JsonFiles.Write(file, config);
            status.Text = "Предпросмотр. Можно остановить кнопкой или Escape в окне плеера.";
            previewProcess = InstallationService.Start(source, "--play", "--config", file, "--output", previewResult); stop.Visibility = Visibility.Visible;
            await previewProcess.WaitForExitAsync();
            using var result = File.Exists(previewResult) ? JsonDocument.Parse(File.ReadAllText(previewResult)) : null;
            if (result is null || !result.RootElement.GetProperty("Opened").GetBoolean() || previewProcess.ExitCode != 0)
            {
                string? message = "Предпросмотр не открыл медиа.";
                if (result is not null && result.RootElement.TryGetProperty("Error", out var error)) message = error.GetString();
                throw new IOException(message);
            }
            status.Text = "Медиа работает. Можно создавать executor.";
        }
        finally { previewProcess?.Dispose(); previewProcess = null; previewResult = null; stop.Visibility = Visibility.Collapsed; Directory.Delete(root, true); }
    }
    private async Task StopPreviewAsync()
    {
        stop.IsEnabled = false;
        try
        {
            for (int attempt = 0; attempt < 200 && previewProcess is not null; attempt++)
            {
                try
                {
                    if (previewResult is not null && File.Exists(previewResult))
                    {
                        using var result = JsonDocument.Parse(File.ReadAllText(previewResult));
                        if (result.RootElement.TryGetProperty("ControlWindow", out var handle) && handle.GetInt64() != 0 && PostMessage(new IntPtr(handle.GetInt64()), 0x0010, IntPtr.Zero, IntPtr.Zero)) return;
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
                await Task.Delay(100);
            }
        }
        finally { stop.IsEnabled = true; }
    }
    private async Task Run(Func<Task> action)
    {
        if (busy || demo) return; busy = true; UpdateActions();
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; if (!closing) UpdateActions(); }
    }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
    internal void RenderDemo(bool overlay = false)
    {
        media = @"C:\Media\surprise.gif"; mediaName.Text = "surprise.gif";
        controllers.Items.Add(new ComboBoxItem { Content = "Xbox Wireless Controller · 045E:0B13", Tag = new ControllerIdentity("demo", "Xbox Wireless Controller", "WGI", 0x045e, 0x0b13) }); controllers.SelectedIndex = 1;
        if (overlay) { appearance.SelectedIndex = 1; ((Expander)form.Children[2]).IsExpanded = true; }
        UpdateActions();
    }
    internal bool CanCreate => create.IsEnabled;
    internal SurpriseConfig CurrentConfig => Config();
    private static TextBlock Text(string value, double size = 14, Brush? color = null) => new() { Text = value, FontSize = size, Foreground = color ?? new SolidColorBrush(Color.FromRgb(30, 42, 58)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = size * 1.4 };
    private static Button Button(string label) => new() { Content = label, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 4, 10, 8), MinHeight = 40, Cursor = System.Windows.Input.Cursors.Hand };
    private static void Field(Panel panel, string label, UIElement control) { var caption = Text(label, 12); panel.Children.Add(caption); AutomationProperties.SetLabeledBy(control, caption); AutomationProperties.SetName(control, label); if (control is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(control); }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
