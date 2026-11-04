using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Surprise.Core;

namespace Surprise.App;

internal sealed class MainWindow : Window
{
    private readonly AppHost host;
    private AnimationSettings draft;
    private bool editing, exit, actionBusy;
    private int page;
    private int selectedFrame = -1;
    private readonly Grid shell = new();
    private readonly StackPanel navigation = new(), body = new(), footer = new();
    private readonly TextBlock status = Label(""), message = Label("");
    private readonly List<Action> validators = [];
    private readonly DispatcherTimer controllerTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ComboBox? controllerBox;
    private static readonly string[] Pages = ["Тип медиа", "Файлы и кадры", "Воспроизведение", "Размер и положение", "Контроллер", "Всё готово"];
    private readonly List<Button> navigationButtons = [];
    public MainWindow(AppHost host)
    {
        this.host = host; draft = host.Settings?.Copy() ?? new();
        Title = "Surprise"; Width = 1000; Height = 790; MinWidth = 780; MinHeight = 610;
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F3F5F9"); Foreground = Brush("#182438"); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="Button"><Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#182438"/><Setter Property="BorderBrush" Value="#D4DCE8"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.8"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
              <Style TargetType="TextBox"><Setter Property="Padding" Value="10,8"/><Setter Property="BorderBrush" Value="#CBD5E1"/><Setter Property="MinHeight" Value="38"/></Style>
              <Style TargetType="ComboBox"><Setter Property="MinHeight" Value="38"/><Setter Property="Padding" Value="8"/></Style>
              <Style TargetType="CheckBox"><Setter Property="Margin" Value="0,10,0,10"/></Style>
            </ResourceDictionary>
            """);
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) }); shell.ColumnDefinitions.Add(new ColumnDefinition());
        var rail = new Border { Background = Brush("#E9EEF6"), Padding = new Thickness(24), Child = navigation }; shell.Children.Add(rail);
        var main = new DockPanel { Margin = new Thickness(30, 26, 30, 20) }; Grid.SetColumn(main, 1); shell.Children.Add(main);
        var heading = new StackPanel(); heading.Children.Add(Label("S U R P R I S E", 12, "#2563EB")); heading.Children.Add(status); DockPanel.SetDock(heading, Dock.Top); main.Children.Add(heading);
        DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);
        main.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = shell;
        message.Foreground = Brush("#B42318");
        host.Changed += UpdateStatus;
        controllerTimer.Tick += (_, _) => RefreshControllers(); controllerTimer.Start();
        Closing += OnClosing; Closed += (_, _) => { host.Changed -= UpdateStatus; controllerTimer.Stop(); };
        if (host.Settings is null || host.Error is not null) BeginEdit(); else Dashboard();
    }
    private void UpdateStatus()
    {
        status.Text = host.Status;
        if (host.Error is not null) message.Text = host.Error;
    }
    private void ResetBody()
    {
        body.Children.Clear(); footer.Children.Clear(); validators.Clear(); controllerBox = null;
        footer.Children.Add(message); message.Text = host.Error ?? ""; UpdateStatus();
    }
    private void Rail(bool settings)
    {
        navigation.Children.Clear(); navigationButtons.Clear(); navigation.Children.Add(Label("Surprise", 25));
        navigation.Children.Add(Label(settings ? "Настрой сюрприз шаг за шагом" : "Твой сюрприз всегда под рукой", 13, "#52647C"));
        if (settings)
        {
            for (int i = 0; i < Pages.Length; i++)
            {
                int target = i; var button = Button($"{i + 1}   {Pages[i]}", () => Safe(() => { ValidatePage(); if (target > 1) ValidateFiles(); page = target; RenderPage(); }));
                button.Content = new TextBlock { Text = $"{i + 1}   {Pages[i]}", TextWrapping = TextWrapping.Wrap };
                button.HorizontalContentAlignment = HorizontalAlignment.Left; button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.Background = i == page ? Brush("#D9E6FF") : Brushes.Transparent;
                // First-run navigation advances in order; previous pages remain accessible.
                button.IsEnabled = host.Settings is not null || i <= page; navigation.Children.Add(button); navigationButtons.Add(button);
            }
        }
        else
        {
            navigation.Children.Add(Label("Управление", 17));
            navigation.Children.Add(Label("Закрытие окна оставляет Surprise работать в фоне. Открой .exe повторно, чтобы вернуться сюда.", 13, "#52647C"));
        }
        navigation.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 18) });
        navigation.Children.Add(Label("Медиа остаются на своих местах. Если переместишь файл — укажи новый путь в настройках.", 12, "#52647C"));
    }
    private void Dashboard()
    {
        editing = host.Editing = false; ResetBody(); Rail(false);
        body.Children.Add(Label("Всё готово к сюрпризу", 28));
        body.Children.Add(Label("Переподключи контроллер — Surprise покажет выбранное медиа.", 14, "#52647C"));
        var s = host.Settings!; var summary = Card();
        summary.Children.Add(Label(s.MediaKind switch { MediaKind.Video => "Видео", MediaKind.Image => "Изображение", _ => "Пиксельная анимация" }, 21));
        summary.Children.Add(Label(string.Join(" → ", s.Files.Select(Path.GetFileName)), 14));
        summary.Children.Add(Label(s.MediaKind == MediaKind.PixelArt ? $"{s.Files.Count} кадр(а) · {s.FramesPerSecond:0.#} FPS · цикл {s.DurationSeconds:0.#} с\nПоявление: {EffectName(s.Effect)}" : s.MediaKind == MediaKind.Video && s.FullscreenVideo ? "На весь экран · до конца видео" : $"Прозрачный показ · {s.DurationSeconds:0.#} с", 14, "#52647C"));
        summary.Children.Add(Label(s.Controller is null ? "Любой контроллер" : "Контроллер: " + s.Controller.Name, 14));
        var actions = new WrapPanel(); actions.Children.Add(Button("Показать сюрприз", () => _ = PreviewSavedAsync(), true));
        actions.Children.Add(Button("Остановить показ", host.Stop)); summary.Children.Add(actions);
        body.Children.Add(Button(host.Paused ? "Продолжить ожидание" : "Приостановить ожидание", () => { host.Pause(); Dashboard(); }));
        body.Children.Add(Button("Изменить настройки…", BeginEdit));
        var startup = new CheckBox { Content = "Запускать при входе в Windows", IsChecked = Autostart.Enabled };
        startup.Click += (_, _) => Safe(() => { Autostart.Set(startup.IsChecked == true); message.Text = startup.IsChecked == true ? "Автозапуск включён. При входе в Windows окно открываться не будет." : "Автозапуск выключен."; });
        body.Children.Add(startup); body.Children.Add(Label("Автозапуск привязан к этому Surprise.exe. После переноса приложения включи его заново.", 12, "#52647C"));
        footer.Children.Add(Button("Завершить Surprise", host.Exit)); UpdateStatus();
    }
    private async Task PreviewSavedAsync()
    {
        await host.PlayAsync(host.Settings!); UpdateStatus();
    }
    private void BeginEdit()
    {
        host.Stop(); draft = host.Settings?.Copy() ?? new();
        draft.Files = draft.Files.Select(f => draft.Resolve(f, host.SettingsPath)).ToList();
        editing = host.Editing = true; page = 0; RenderPage();
    }
    private void RenderPage()
    {
        ResetBody(); Rail(true); body.Children.Add(Label(Pages[page], 28));
        body.Children.Add(Label($"Шаг {page + 1} из {Pages.Length}", 12, "#52647C"));
        switch (page)
        {
            case 0: TypePage(); break;
            case 1: FilesPage(); break;
            case 2: PlaybackPage(); break;
            case 3: PositionPage(); break;
            case 4: ControllerPage(); break;
            case 5: ReviewPage(); break;
        }
        var controls = new WrapPanel();
        if (page > 0) controls.Children.Add(Button("Назад", () => Safe(() => { ValidatePage(); page--; RenderPage(); })));
        if (page < Pages.Length - 1) controls.Children.Add(Button("Далее", () => Safe(() => { ValidatePage(); if (page == 1) ValidateFiles(); page++; RenderPage(); }), true));
        if (page == Pages.Length - 1 || host.Settings is not null) controls.Children.Add(Button(host.Settings is null ? "Сохранить и запустить" : "Сохранить", () => Safe(() => { ValidatePage(); host.Save(draft); Dashboard(); }), true));
        if (host.Settings is not null) controls.Children.Add(Button("Отмена", () => { host.Stop(); Dashboard(); }));
        footer.Children.Add(controls);
    }
    private void TypePage()
    {
        body.Children.Add(Label("Что появится при подключении контроллера?", 15, "#52647C"));
        foreach (var (kind, title, description) in new[] { (MediaKind.Video, "Видео", "Один видеофайл со звуком."), (MediaKind.Image, "Изображение", "Одна картинка, GIF, APNG или WebP. Прозрачность сохраняется."), (MediaKind.PixelArt, "Пиксельарт · PNG-кадры", "Один или несколько PNG, эффекты появления и зацикленная смена кадров.") })
        {
            var content = new StackPanel(); content.Children.Add(Label((draft.MediaKind == kind ? "●  " : "○  ") + title, 19)); content.Children.Add(Label(description, 13, "#52647C"));
            var button = Button("", () => { if (draft.MediaKind != kind) { draft.MediaKind = kind; draft.Files.Clear(); } RenderPage(); });
            button.Content = content; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (draft.MediaKind == kind) { button.Background = Brush("#EAF1FF"); button.BorderBrush = Brush("#2563EB"); }
            AutomationProperties.SetName(button, title); body.Children.Add(button);
        }
    }
    private void FilesPage()
    {
        bool pixel = draft.MediaKind == MediaKind.PixelArt;
        body.Children.Add(Label(pixel ? "Добавь PNG в порядке воспроизведения. Перемещай выбранный кадр кнопками ↑ и ↓. Размеры холста должны совпадать." : "Выбери файл. Surprise сохранит путь к нему; файл не копируется.", 14, "#52647C"));
        var list = new ListBox { MinHeight = 180, MaxHeight = 280, Margin = new Thickness(0, 8, 0, 12) };
        for (int i = 0; i < draft.Files.Count; i++) list.Items.Add(new ListBoxItem { Content = $"{i + 1}.  {Path.GetFileName(draft.Files[i])}", ToolTip = draft.Files[i], Padding = new Thickness(8) });
        list.SelectedIndex = Math.Min(selectedFrame, draft.Files.Count - 1);
        body.Children.Add(list);
        var buttons = new WrapPanel();
        buttons.Children.Add(Button(pixel ? "Добавить PNG…" : "Выбрать файл…", () =>
        {
            var dialog = new OpenFileDialog { Multiselect = pixel, Filter = pixel ? "PNG-кадры|*.png" : draft.MediaKind == MediaKind.Video ? "Видео|*.mp4;*.webm;*.mkv;*.mov;*.m4v;*.avi;*.wmv" : "Изображения|*.png;*.gif;*.apng;*.webp;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true) return;
            if (!pixel) draft.Files.Clear(); draft.Files.AddRange(dialog.FileNames); selectedFrame = draft.Files.Count - 1; RenderPage();
        }, true));
        if (pixel)
        {
            void Move(int delta) { int i = list.SelectedIndex, j = i + delta; if (i < 0 || j < 0 || j >= draft.Files.Count) return; (draft.Files[i], draft.Files[j]) = (draft.Files[j], draft.Files[i]); selectedFrame = j; RenderPage(); }
            buttons.Children.Add(Button("↑ Выше", () => Move(-1))); buttons.Children.Add(Button("↓ Ниже", () => Move(1)));
        }
        buttons.Children.Add(Button("Убрать из списка", () => { if (list.SelectedIndex < 0) return; draft.Files.RemoveAt(list.SelectedIndex); RenderPage(); }));
        body.Children.Add(buttons); body.Children.Add(Label("Исходные файлы при этом не изменяются и не удаляются.", 12, "#52647C"));
        list.SelectionChanged += (_, _) => { selectedFrame = list.SelectedIndex; if (list.SelectedIndex >= 0) message.Text = draft.Files[list.SelectedIndex]; };
    }
    private void PlaybackPage()
    {
        if (draft.MediaKind == MediaKind.PixelArt)
        {
            Field("Скорость смены кадров", Slider(.1, 30, draft.FramesPerSecond, "FPS", v => draft.FramesPerSecond = v));
            var effects = new ComboBox(); foreach (RevealEffect effect in Enum.GetValues<RevealEffect>()) effects.Items.Add(EffectName(effect)); effects.SelectedIndex = (int)draft.Effect;
            effects.SelectionChanged += (_, _) => { draft.Effect = (RevealEffect)effects.SelectedIndex; RenderPage(); }; Field("Эффект появления первого кадра", effects);
            if (draft.Effect != RevealEffect.None) Number("Длительность появления, секунд", draft.RevealSeconds, .1, 30, v => draft.RevealSeconds = v);
            Number("Размер клетки эффекта в исходном PNG (0 = автоматически)", draft.CellSize, 0, 32768, v => draft.CellSize = (int)v, true);
            body.Children.Add(Label("Эффект выполняется один раз. Затем кадры сменяются по кругу без повторного эффекта. Чёткие края и PNG-альфа сохраняются.", 13, "#52647C"));
        }
        if (draft.MediaKind == MediaKind.Video)
        {
            var full = new CheckBox { Content = "Показывать видео на весь экран", IsChecked = draft.FullscreenVideo };
            full.Click += (_, _) => { draft.FullscreenVideo = full.IsChecked == true; RenderPage(); }; body.Children.Add(full);
            Field("Громкость", Slider(0, 100, draft.Volume * 100, "%", v => draft.Volume = v / 100));
            if (draft.FullscreenVideo) body.Children.Add(Label("Полноэкранное видео воспроизводится один раз до конца.", 13, "#52647C"));
        }
        if (draft.MediaKind != MediaKind.Video || !draft.FullscreenVideo)
        {
            Number(draft.MediaKind == MediaKind.PixelArt ? "Сколько секунд крутить цикл" : "Длительность показа, секунд", draft.DurationSeconds, 1, 3600, v => draft.DurationSeconds = v);
            body.Children.Add(Label("Время показа отсчитывается после завершения эффекта появления. По окончании объект исчезает.", 13, "#52647C"));
        }
        AddPreview();
    }
    private void PositionPage()
    {
        var displays = new ComboBox();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens) displays.Items.Add(new ComboBoxItem { Content = $"Экран {displays.Items.Count + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}", Tag = screen.DeviceName });
        displays.SelectedItem = displays.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == draft.Monitor) ?? displays.Items[0];
        draft.Monitor = (string)((ComboBoxItem)displays.SelectedItem).Tag;
        displays.SelectionChanged += (_, _) => draft.Monitor = (string)((ComboBoxItem)displays.SelectedItem).Tag; Field("Экран", displays);
        if (draft.MediaKind != MediaKind.Video || !draft.FullscreenVideo)
        {
            body.Children.Add(Label("Объект показывается поверх рабочего стола без рамки, фона и кнопок. Клики проходят сквозь него.", 14, "#52647C"));
            Number("Ширина области показа, пикселей", draft.Width, 16, 16384, v => draft.Width = (int)v, true);
            Number("Высота области показа, пикселей", draft.Height, 16, 16384, v => draft.Height = (int)v, true);
            body.Children.Add(Label("Пропорции рисунка сохраняются внутри этой области.", 12, "#52647C"));
            body.Children.Add(Button("Расположить на рабочем столе…", () => _ = PlaceAsync(), true));
        }
        AddPreview();
    }
    private void ControllerPage()
    {
        body.Children.Add(Label("Подключи контроллер и выбери его модель или оставь «Любой контроллер». Уже подключённый контроллер не запускает сюрприз — нужно переподключение.", 14, "#52647C"));
        controllerBox = new ComboBox(); controllerBox.Items.Add(new ComboBoxItem { Content = "Любой контроллер" });
        if (draft.Controller is not null) controllerBox.Items.Add(ControllerItem(draft.Controller));
        controllerBox.SelectedIndex = draft.Controller is null ? 0 : 1; RefreshControllers();
        controllerBox.SelectionChanged += (_, _) => draft.Controller = (controllerBox.SelectedItem as ComboBoxItem)?.Tag as ControllerIdentity;
        Field("Какой контроллер запускает сюрприз", controllerBox);
        body.Children.Add(Label("Выбор модели подходит для любого экземпляра этой модели. USB и Bluetooth могут определяться как разные модели.", 12, "#52647C"));
        Number("Пауза между срабатываниями, секунд", draft.CooldownSeconds, 0, 86400, v => draft.CooldownSeconds = (int)v, true);
    }
    private static ComboBoxItem ControllerItem(ControllerIdentity c) => new() { Content = $"{c.Name} · {c.VendorId:X4}:{c.ProductId:X4}", Tag = c };
    private void RefreshControllers()
    {
        if (controllerBox is null || controllerBox.IsDropDownOpen) return;
        foreach (var controller in host.Controllers().Controllers.Where(c => c.HasModelIdentity))
            if (!controllerBox.Items.OfType<ComboBoxItem>().Any(i => i.Tag is ControllerIdentity c && c.MatchesModel(controller))) controllerBox.Items.Add(ControllerItem(controller));
    }
    private void ReviewPage()
    {
        body.Children.Add(Label("Проверь сюрприз перед сохранением", 19));
        body.Children.Add(Label(string.Join("\n", draft.Files.Select((f, i) => $"{i + 1}. {Path.GetFileName(f)}"))));
        if (draft.MediaKind == MediaKind.PixelArt) body.Children.Add(Label($"Цикл: {draft.DurationSeconds:0.#} с · {draft.FramesPerSecond:0.#} FPS\nПоявление: {EffectName(draft.Effect)}"));
        body.Children.Add(Label(draft.Controller is null ? "Срабатывание: любой контроллер" : "Срабатывание: " + draft.Controller.Name));
        body.Children.Add(Label("После сохранения начнётся ожидание подключения контроллера. Автозапуск можно включить в окне управления.", 14, "#52647C")); AddPreview();
    }
    private void AddPreview()
    {
        var buttons = new WrapPanel(); buttons.Children.Add(Button("Превью сюрприза", () => _ = PreviewDraftAsync(), true)); buttons.Children.Add(Button("Остановить превью", host.Stop)); body.Children.Add(buttons);
    }
    private async Task PreviewDraftAsync()
    {
        if (actionBusy || host.Playing) return;
        try { ValidatePage(); actionBusy = true; message.Text = "Подготавливаю медиа… Большие PNG читаются по строкам."; await host.PlayAsync(draft); if (host.Error is null) message.Text = "Превью запущено. Изменения ещё не сохранены."; }
        catch (Exception ex) { message.Text = ex.Message; }
        finally { actionBusy = false; }
    }
    private async Task PlaceAsync()
    {
        if (actionBusy) return;
        try
        {
            ValidatePage(); ValidateFiles(); host.Stop(); actionBusy = true; message.Text = "Подготавливаю изображение для размещения…";
            ImageSource? source = null;
            if (draft.MediaKind == MediaKind.PixelArt) source = FrameLoader.Bitmap((await FrameLoader.LoadAsync(draft, host.SettingsPath, CancellationToken.None))[0]);
            else if (draft.MediaKind == MediaKind.Image && Path.GetExtension(draft.Files[0]).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                try { source = FrameLoader.Bitmap(await Task.Run(() => PixelPng.Read(draft.Resolve(draft.Files[0], host.SettingsPath)))); } catch (InvalidDataException) { }
            }
            var picker = new PlacementWindow(draft, source) { Owner = this };
            if (picker.ShowDialog() == true) { draft.X = picker.Result.X; draft.Y = picker.Result.Y; draft.Width = picker.Result.Width; draft.Height = picker.Result.Height; }
            RenderPage();
        }
        catch (Exception ex) { message.Text = ex.Message; }
        finally { actionBusy = false; }
    }
    private void ValidatePage() { foreach (var validate in validators) validate(); }
    private void ValidateFiles() { new AnimationSettings { MediaKind = draft.MediaKind, Files = [.. draft.Files] }.Validate(host.SettingsPath); }
    private void Safe(Action action) { if (actionBusy) return; try { action(); } catch (Exception ex) { message.Text = ex.Message; } }
    private void Number(string caption, double value, double min, double max, Action<double> setter, bool integer = false)
    {
        var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), MaxWidth = 250, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
        void Apply()
        {
            if (!double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < min || number > max || (integer && number != Math.Floor(number))) throw new InvalidDataException($"{caption}: введи {(integer ? "целое число" : "число")} от {min} до {max}.");
            setter(number);
        }
        box.TextChanged += (_, _) => { try { Apply(); } catch (InvalidDataException) { } }; validators.Add(Apply); Field(caption, box);
    }
    private static StackPanel Slider(double min, double max, double value, string units, Action<double> setter)
    {
        var panel = new StackPanel(); var text = Label($"{value:0.#} {units}", 16);
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = units == "FPS" ? .1 : 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 5, 0, 12), SmallChange = units == "FPS" ? .1 : 1 };
        slider.ValueChanged += (_, _) => { double v = Math.Round(slider.Value, 1); setter(v); text.Text = $"{v:0.#} {units}"; };
        panel.Children.Add(text); panel.Children.Add(slider); return panel;
    }
    private void Field(string caption, UIElement control)
    {
        var label = Label(caption, 13, "#52647C"); body.Children.Add(label); AutomationProperties.SetName(control, caption); AutomationProperties.SetLabeledBy(control, label);
        if (control is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(control);
    }
    private StackPanel Card()
    {
        var panel = new StackPanel(); body.Children.Add(new Border { Child = panel, Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(22), Margin = new Thickness(0, 12, 0, 18) }); return panel;
    }
    internal static string EffectName(RevealEffect e) => e switch { RevealEffect.None => "Нет", RevealEffect.Random => "Случайное появление клеток", RevealEffect.Wave => "Волна", RevealEffect.Scatter => "Сборка из разлёта", _ => "Сборка снизу вверх" };
    internal static TextBlock Label(string text, double size = 14, string color = "#182438") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), LineHeight = size * 1.4 };
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private static Button Button(string caption, Action action, bool primary = false)
    {
        var button = new Button { Content = caption, Padding = new Thickness(15, 10, 15, 10), Margin = new Thickness(0, 4, 10, 10), MinHeight = 42, Background = primary ? Brush("#2563EB") : Brushes.White, Foreground = primary ? Brushes.White : Brush("#182438") };
        button.Click += (_, _) => action(); return button;
    }
    public void AllowExit() => exit = true;
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exit) return;
        e.Cancel = true;
        if (actionBusy) { message.Text = "Дождись подготовки медиа. Запущенное превью можно остановить."; return; }
        if (host.Settings is null)
        {
            if (MessageBox.Show(this, "Закрыть Surprise без сохранения настройки?", "Surprise", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Dispatcher.BeginInvoke(new Action(host.Exit));
            return;
        }
        if (editing)
        {
            if (MessageBox.Show(this, "Выйти из настроек без сохранения изменений?", "Surprise", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            host.Stop(); Dashboard();
        }
        if (!host.Settings.CloseNoticeShown)
        {
            MessageBox.Show(this, "Surprise продолжает работать в фоне. Открой Surprise.exe повторно для управления. Для полного выхода нажми «Завершить Surprise».", "Surprise", MessageBoxButton.OK, MessageBoxImage.Information);
            host.Settings.CloseNoticeShown = true;
            try { JsonFiles.Write(host.SettingsPath, host.Settings); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Hide();
    }
}
