using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Surprise.Core;
using Surprise.Windows;

namespace Surprise.App;

internal sealed class MainWindow : Window
{
    private readonly AppHost host;
    private AnimationSettings draft;
    private bool editing, exit, actionBusy;
    private int page;
    private int selectedFrame = -1;
    private bool designSettings;
    private bool IsSetup => host.Settings is null && !designSettings;
    private readonly Grid shell = new();
    private readonly StackPanel navigation = new(), body = new(), footer = new();
    private readonly TextBlock status = Label(""), message = Label("");
    private readonly List<Action> validators = [];
    private readonly DispatcherTimer controllerTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ComboBox? controllerBox;
    private static readonly string[] Pages = ["Тип медиа", "Файлы и кадры", "Воспроизведение", "Размер и положение", "Контроллер", "Всё готово"];
    private readonly List<Button> navigationButtons = [];
    public MainWindow(AppHost host, bool design = false)
    {
        this.host = host; draft = host.Settings?.Copy() ?? new();
        Title = "Surprise"; Width = Math.Min(1080, SystemParameters.WorkArea.Width); Height = Math.Min(820, SystemParameters.WorkArea.Height); MinWidth = Math.Min(820, Width); MinHeight = Math.Min(600, Height);
        MaxHeight = SystemParameters.WorkArea.Height; MaxWidth = SystemParameters.WorkArea.Width;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F3F5F9"); Foreground = Brush("#182438"); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Resources = Ui.Resources();
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); shell.ColumnDefinitions.Add(new ColumnDefinition());
        var rail = new Border { Background = Brush("#EAF0F8"), Padding = new Thickness(20, 28, 14, 20), Child = new ScrollViewer { Content = navigation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }; shell.Children.Add(rail);
        var main = new Grid { Margin = new Thickness(28, 22, 28, 20) }; Grid.SetColumn(main, 1); shell.Children.Add(main);
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 18) }; heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(Label("ТВОЙ СЮРПРИЗ", 11, "#71829A"));
        status.FontSize = 12; status.Margin = new Thickness(10, 5, 10, 5); status.MaxWidth = 330;
        var chip = new Border { Child = status, Background = Brush("#E7EFFA"), CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top }; Grid.SetColumn(chip, 1); heading.Children.Add(chip); main.Children.Add(heading);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) }; Grid.SetRow(scroll, 1); main.Children.Add(scroll);
        var bottom = new Border { Child = footer, BorderBrush = Brush("#DFE6F0"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(bottom, 2); main.Children.Add(bottom);
        Content = shell;
        message.Foreground = Brush("#A14C24"); message.FontSize = 12; message.Margin = new Thickness(0, 0, 0, 10); message.MaxHeight = 76;
        var messageStyle = new Style(typeof(TextBlock)); messageStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        messageStyle.Triggers.Add(new Trigger { Property = TextBlock.TextProperty, Value = "", Setters = { new Setter(VisibilityProperty, Visibility.Collapsed) } }); message.Style = messageStyle;
        host.Changed += UpdateStatus;
        controllerTimer.Tick += (_, _) => RefreshControllers(); if (!design) controllerTimer.Start();
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
        navigation.Children.Clear(); navigationButtons.Clear(); navigation.Children.Add(Label("Surprise", 26));
        navigation.Children.Add(Label(settings ? IsSetup ? "ПЕРВЫЙ ЗАПУСК" : "НАСТРОЙКИ" : "УПРАВЛЕНИЕ", 10, "#71829A"));
        if (settings)
        {
            for (int i = 0; i < (IsSetup ? Pages.Length : Pages.Length - 1); i++)
            {
                int target = i; var button = Button($"{i + 1}   {Pages[i]}", () => Safe(() => { ValidatePage(); if (target > 1) ValidateFiles(); page = target; RenderPage(); }));
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = (i + 1).ToString("00"), FontSize = 11, Foreground = Brush(i == page ? "#2563EB" : "#8A9AB0"), VerticalAlignment = VerticalAlignment.Center });
                var caption = new TextBlock { Text = Pages[i], TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(caption, 1); row.Children.Add(caption); button.Content = row;
                button.HorizontalContentAlignment = HorizontalAlignment.Left; button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.Background = i == page ? Brush("#D9E6FF") : Brushes.Transparent;
                button.BorderThickness = new Thickness(0); button.Padding = new Thickness(10, 12, 10, 12); button.Margin = new Thickness(0, 0, 0, 6);
                // First-run navigation advances in order; previous pages remain accessible.
                button.IsEnabled = !IsSetup || i <= page; navigation.Children.Add(button); navigationButtons.Add(button);
            }
        }
        else
        {
            navigation.Children.Add(Label("Закрытие окна оставляет Surprise работать в фоне. Открой .exe повторно, чтобы вернуться сюда.", 13, "#52647C"));
        }
        navigation.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 18) });
        navigation.Children.Add(Label("Медиа остаются на своих местах. Если переместишь файл — укажи новый путь в настройках.", 12, "#52647C"));
    }
    private void Dashboard(AnimationSettings? designSettings = null)
    {
        editing = host.Editing = false; ResetBody(); Rail(false);
        body.Children.Add(Label("Всё готово к сюрпризу", 28));
        body.Children.Add(Label("Переподключи контроллер — Surprise покажет выбранное медиа.", 14, "#52647C"));
        var s = designSettings ?? host.Settings!; var summary = Card();
        summary.Children.Add(Label(s.MediaKind switch { MediaKind.Video => "Видео", MediaKind.Image => "Изображение", _ => "Пиксельная анимация" }, 21));
        summary.Children.Add(Label(string.Join(" → ", s.Files.Select(Path.GetFileName)), 14));
        summary.Children.Add(Label(s.MediaKind == MediaKind.PixelArt ? $"{s.Files.Count} кадр(а) · {s.FramesPerSecond:0.#} FPS · цикл {s.DurationSeconds:0.#} с\nПоявление: {EffectName(s.Effect)}" : s.MediaKind == MediaKind.Video && s.FullscreenVideo ? "На весь экран · до конца видео" : $"Прозрачный показ · {s.DurationSeconds:0.#} с", 14, "#52647C"));
        summary.Children.Add(Label(s.Controller is null ? "Любой контроллер" : "Контроллер: " + s.Controller.Name, 14));
        var actions = new WrapPanel(); actions.Children.Add(Button("Показать сюрприз", () => _ = PreviewSavedAsync(), true));
        actions.Children.Add(Button("Остановить показ", host.Stop)); summary.Children.Add(actions);
        var management = new WrapPanel(); management.Children.Add(Button(host.Paused ? "Продолжить ожидание" : "Приостановить ожидание", () => { host.Pause(); Dashboard(); })); management.Children.Add(Button("Изменить настройки…", BeginEdit)); body.Children.Add(management);
        var startupGroup = Card(); startupGroup.Children.Add(Label("Запуск вместе с Windows", 17));
        var startup = new CheckBox { Content = "Запускать при входе в Windows", IsChecked = Autostart.Enabled };
        startup.Click += (_, _) => Safe(() => { Autostart.Set(startup.IsChecked == true); message.Text = startup.IsChecked == true ? "Автозапуск включён. При входе в Windows окно открываться не будет." : "Автозапуск выключен."; });
        startupGroup.Children.Add(startup); startupGroup.Children.Add(Label("Приложение запустится в фоне. После переноса папки включи автозапуск заново.", 12, "#52647C"));
        var diagnostic = Card(); diagnostic.Children.Add(Label("Диагностика", 17));
        diagnostic.Children.Add(Label(AppContext.BaseDirectory, 12, "#52647C"));
        if (DiagnosticLog.Failure is not null) diagnostic.Children.Add(Label("Не удалось записать лог: " + DiagnosticLog.Failure, 12, "#B42318"));
        var diagnosticActions = new WrapPanel(); diagnosticActions.Children.Add(Button("Открыть папку приложения", () => Safe(() => OpenPath(AppContext.BaseDirectory)))); diagnosticActions.Children.Add(Button("Открыть Surprise.log", () => Safe(() => OpenPath(DiagnosticLog.FilePath ?? Path.Combine(AppContext.BaseDirectory, "Surprise.log"))))); diagnostic.Children.Add(diagnosticActions);
        var exitRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; exitRow.Children.Add(Button("Завершить Surprise", host.Exit)); footer.Children.Add(exitRow); UpdateStatus();
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
        DiagnosticLog.Info("ui.settings-open");
    }
    private void RenderPage()
    {
        ResetBody(); Rail(true); body.Children.Add(Label(Pages[page], 28));
        body.Children.Add(Label(IsSetup ? $"Шаг {page + 1} из {Pages.Length}" : "Изменения применятся после сохранения. Превью использует текущие настройки.", 12, "#71829A"));
        switch (page)
        {
            case 0: TypePage(); break;
            case 1: FilesPage(); break;
            case 2: PlaybackPage(); break;
            case 3: PositionPage(); break;
            case 4: ControllerPage(); break;
            case 5: ReviewPage(); break;
        }
        WrapPageContent();
        var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (IsSetup && page > 0) controls.Children.Add(Button("Назад", () => Safe(() => { ValidatePage(); page--; RenderPage(); })));
        if (IsSetup && page < Pages.Length - 1) controls.Children.Add(Button("Далее →", () => Safe(() => { ValidatePage(); if (page == 1) ValidateFiles(); page++; RenderPage(); }), true));
        if (!IsSetup) controls.Children.Add(Button("Отмена", () => { host.Stop(); DiagnosticLog.Info("ui.settings-cancelled"); Dashboard(); }));
        if (page == Pages.Length - 1 || !IsSetup) controls.Children.Add(Button(IsSetup ? "Сохранить и запустить" : "Сохранить изменения", () => Safe(() => { ValidatePage(); host.Save(draft); Dashboard(); }), true));
        footer.Children.Add(controls);
        DiagnosticLog.Info("ui.page", new { Page = page, Title = Pages[page], DraftMedia = draft.MediaKind.ToString(), Wizard = host.Settings is null });
    }
    private void TypePage()
    {
        body.Children.Add(Label("Что показать при подключении контроллера?", 14, "#52647C"));
        var choices = new UniformGrid { Columns = 3, MaxWidth = 630, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (kind, title, icon) in new[] { (MediaKind.Video, "Видео", "video"), (MediaKind.Image, "Изображение", "image"), (MediaKind.PixelArt, "Пиксельарт", "pixels") })
        {
            choices.Children.Add(Ui.Tile(title, icon, draft.MediaKind == kind, () => { if (draft.MediaKind != kind) { draft.MediaKind = kind; draft.Files.Clear(); } DiagnosticLog.Info("ui.media-selected", new { Kind = kind.ToString() }); RenderPage(); }));
        }
        body.Children.Add(choices);
        body.Children.Add(Ui.Description(draft.MediaKind switch { MediaKind.Video => "Видео со звуком", MediaKind.Image => "Одно изображение", _ => "Покадровая PNG-анимация" }, draft.MediaKind switch { MediaKind.Video => "Выбери один видеофайл. Его можно показать на весь экран или поверх рабочего стола.", MediaKind.Image => "Статичная картинка, GIF, APNG или WebP. Прозрачный фон изображения сохранится.", _ => "Добавь один или несколько PNG, задай порядок, скорость и время цикла. Эффект появления выполняется только перед первым кадром." }));
    }
    private void FilesPage()
    {
        bool pixel = draft.MediaKind == MediaKind.PixelArt;
        body.Children.Add(Label(pixel ? "Добавь PNG в порядке воспроизведения. Перемещай выбранный кадр кнопками ↑ и ↓. Размеры холста должны совпадать." : "Выбери файл. Surprise сохранит путь к нему; файл не копируется.", 14, "#52647C"));
        var list = new ListBox { MinHeight = 180, MaxHeight = 280, Margin = new Thickness(0, 8, 0, 12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        for (int i = 0; i < draft.Files.Count; i++) list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = $"{i + 1}.  {Path.GetFileName(draft.Files[i])}", TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = draft.Files[i], Padding = new Thickness(8) });
        list.SelectedIndex = Math.Min(selectedFrame, draft.Files.Count - 1);
        body.Children.Add(list);
        var buttons = new WrapPanel();
        buttons.Children.Add(Button(pixel ? "Добавить PNG…" : "Выбрать файл…", () =>
        {
            var dialog = new OpenFileDialog { Multiselect = pixel, Filter = pixel ? "PNG-кадры|*.png" : draft.MediaKind == MediaKind.Video ? "Видео|*.mp4;*.webm;*.mkv;*.mov;*.m4v;*.avi;*.wmv" : "Изображения|*.png;*.gif;*.apng;*.webp;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true) { DiagnosticLog.Info("ui.file-dialog-cancelled"); return; }
            DiagnosticLog.Info("ui.files-selected", new { Files = dialog.FileNames });
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
            Number("Сколько секунд крутить цикл", draft.DurationSeconds, 1, 3600, v => draft.DurationSeconds = v);
            Field("Скорость смены кадров", Slider(.1, 30, draft.FramesPerSecond, "FPS", v => draft.FramesPerSecond = v));
            body.Children.Add(Label("Эффект появления", 15));
            var effects = new UniformGrid { Columns = 5 };
            foreach (RevealEffect effect in Enum.GetValues<RevealEffect>())
            {
                string caption = effect switch { RevealEffect.None => "Нет", RevealEffect.Random => "Случайно", RevealEffect.Wave => "Волна", RevealEffect.Scatter => "Разлёт", _ => "Снизу вверх" };
                effects.Children.Add(Ui.Tile(caption, effect.ToString().ToLowerInvariant(), draft.Effect == effect, () => Safe(() => { ValidatePage(); draft.Effect = effect; DiagnosticLog.Info("ui.effect-selected", new { Effect = effect.ToString() }); RenderPage(); })));
            }
            body.Children.Add(effects); body.Children.Add(Ui.Description(EffectName(draft.Effect), EffectDescription(draft.Effect)));
            if (draft.Effect != RevealEffect.None) Number("Длительность появления, секунд", draft.RevealSeconds, .1, 30, v => draft.RevealSeconds = v);
            Number("Размер клетки · 0 = автоматически", draft.CellSize, 0, 32768, v => draft.CellSize = (int)v, true);
            var cellField = body.Children[body.Children.Count - 1]; body.Children.Remove(cellField);
            body.Children.Add(new Expander { Header = "Дополнительно: сетка эффекта", Content = cellField, IsExpanded = draft.CellSize != 0, Margin = new Thickness(0, 8, 0, 16), Padding = new Thickness(0, 10, 0, 0) });
        }
        if (draft.MediaKind == MediaKind.Video)
        {
            var full = new CheckBox { Content = "Показывать видео на весь экран", IsChecked = draft.FullscreenVideo };
            full.Click += (_, _) => { draft.FullscreenVideo = full.IsChecked == true; RenderPage(); }; body.Children.Add(full);
            Field("Громкость", Slider(0, 100, draft.Volume * 100, "%", v => draft.Volume = v / 100));
            if (draft.FullscreenVideo) body.Children.Add(Label("Полноэкранное видео воспроизводится один раз до конца.", 13, "#52647C"));
        }
        if (draft.MediaKind == MediaKind.Image || draft.MediaKind == MediaKind.Video && !draft.FullscreenVideo)
        {
            Number(draft.MediaKind == MediaKind.PixelArt ? "Сколько секунд крутить цикл" : "Длительность показа, секунд", draft.DurationSeconds, 1, 3600, v => draft.DurationSeconds = v);
            body.Children.Add(Label("Время показа отсчитывается после завершения эффекта появления. По окончании объект исчезает.", 13, "#52647C"));
        }
        AddPreview();
        if (draft.MediaKind == MediaKind.PixelArt) body.Children.Add(Label("Цикл начинается после появления первого кадра. По окончании выбранного времени объект исчезает.", 12, "#71829A"));
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
        catch (Exception ex) { DiagnosticLog.Error("ui.preview-failed", ex); message.Text = ex.Message; }
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
        catch (Exception ex) { DiagnosticLog.Error("ui.placement-failed", ex); message.Text = ex.Message; }
        finally { actionBusy = false; }
    }
    private void ValidatePage() { foreach (var validate in validators) validate(); }
    private void ValidateFiles() { new AnimationSettings { MediaKind = draft.MediaKind, Files = [.. draft.Files] }.Validate(host.SettingsPath); }
    private void Safe(Action action) { if (actionBusy) return; try { action(); } catch (Exception ex) { DiagnosticLog.Error("ui.action-failed", ex); message.Text = ex.Message; } }
    private void Number(string caption, double value, double min, double max, Action<double> setter, bool integer = false)
    {
        var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), Width = 130, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
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
        AutomationProperties.SetName(slider, units == "FPS" ? "Скорость смены кадров, FPS" : "Громкость, проценты");
        slider.ValueChanged += (_, _) => { double v = Math.Round(slider.Value, 1); setter(v); text.Text = $"{v:0.#} {units}"; };
        panel.Children.Add(text); panel.Children.Add(slider); return panel;
    }
    private void Field(string caption, UIElement control)
    {
        var label = Label(caption, 13, "#52647C"); label.Margin = new Thickness(0, 0, 18, 0); label.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, caption); AutomationProperties.SetLabeledBy(control, label);
        UIElement content;
        if (control is ComboBox)
        {
            var stack = new StackPanel(); label.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(label); stack.Children.Add(control); content = stack;
        }
        else
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(control is TextBox ? 145 : 230) });
            row.Children.Add(label); Grid.SetColumn(control, 1); row.Children.Add(control); content = row;
        }
        body.Children.Add(new Border { Child = content, Padding = new Thickness(0, 12, 0, 12), BorderBrush = Brush("#E8EDF5"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 8) });
    }
    private StackPanel Card()
    {
        var panel = new StackPanel(); body.Children.Add(new Border { Child = panel, Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(22), Margin = new Thickness(0, 12, 0, 18) }); return panel;
    }
    internal static string EffectName(RevealEffect e) => e switch { RevealEffect.None => "Нет", RevealEffect.Random => "Случайное появление клеток", RevealEffect.Wave => "Волна", RevealEffect.Scatter => "Сборка из разлёта", _ => "Сборка снизу вверх" };
    internal static TextBlock Label(string text, double size = 14, string color = "#182438") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), LineHeight = size * 1.4 };
    private static Brush Brush(string color) => Ui.Brush(color);
    private static Button Button(string caption, Action action, bool primary = false) => Ui.Button(caption, action, primary);
    private static string EffectDescription(RevealEffect e) => e switch
    {
        RevealEffect.None => "Первый кадр появляется сразу. Затем начинается обычная смена кадров по кругу.",
        RevealEffect.Random => "Клетки постепенно проявляются в случайном порядке, пока не соберётся первый кадр.",
        RevealEffect.Wave => "Волна открывает первый кадр по клеткам — от верхнего левого угла к нижнему правому.",
        RevealEffect.Scatter => "Клетки прилетают с разных сторон и собираются в первый кадр.",
        _ => "Клетки возникают под изображением, поднимаются вверх и плавно занимают свои места."
    };
    private void WrapPageContent()
    {
        var children = body.Children.Cast<UIElement>().Skip(2).ToArray();
        var content = new StackPanel(); foreach (var child in children) { body.Children.Remove(child); content.Children.Add(child); }
        body.Children.Add(new Border { Child = content, Padding = new Thickness(22), Background = Brushes.White, BorderBrush = Brush("#E3EAF4"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 8, 0, 8) });
    }
    private static void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) throw new IOException("Не найден файл или папка: " + path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    internal void RenderDesignPage(AnimationSettings settings, int targetPage, bool existing = false, bool dashboard = false)
    {
        designSettings = existing; draft = settings.Copy(); page = targetPage;
        if (dashboard) { Dashboard(settings); status.Text = "Ожидает подключения контроллера"; } else { editing = host.Editing = true; RenderPage(); if (existing) status.Text = "Настройка — срабатывания приостановлены"; }
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
        DiagnosticLog.Info("ui.hidden"); Hide();
    }
}
