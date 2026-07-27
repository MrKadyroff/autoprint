using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using AutoPrint.Models;
using AutoPrint.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ImageSource = System.Windows.Media.ImageSource;

namespace AutoPrint.ViewModels;

[SupportedOSPlatform("windows")]
public class MainViewModel : ViewModelBase
{
    private readonly PrintServer _server = new();
    private readonly PrintPipeline _pipeline = new();
    private readonly HtmlRenderer _htmlRenderer = new();

    /// <summary>Очередь печати (последовательная обработка, автоповтор, пауза).</summary>
    public PrintQueueManager Queue { get; } = new();

    private readonly AppConfig _config = AppConfig.Load();
    private readonly UpdateService _updater;
    private UpdateInfo? _latestUpdate;

    public MainViewModel()
    {
        LoadSettingsFromConfig();   // применяем сохранённую геометрию/шрифты/QR до первого превью

        // Список установленных принтеров. Автовыбираем только то, что реально похоже на
        // чековый термопринтер (Mulex и т.п.) — НЕ первый попавшийся и НЕ виртуальный
        // (XPS/PDF/Fax) принтер. Иначе HasPrinter станет true "на постороннем" принтере,
        // и автонастройка решит, что делать больше нечего, не начав искать настоящий.
        foreach (var p in RawPrinterHelper.InstalledPrinters()) Printers.Add(p);
        SelectedPrinter = Printers.FirstOrDefault(PrinterDiagnostics.IsLikelyReceiptPrinter);

        HtmlInput = TestData.SampleHtmlPayload();

        PrintSystemTestCommand = new AsyncRelayCommand(PrintSystemTestAsync, () => HasPrinter);
        EmulateWebRequestCommand = new AsyncRelayCommand(EmulateWebRequestAsync);
        AddTextLineCommand = new RelayCommand(() => AddLine(LineKind.Text));
        AddTwoColLineCommand = new RelayCommand(() => AddLine(LineKind.TwoCol));
        AddSpacerLineCommand = new RelayCommand(() => AddLine(LineKind.Spacer));
        RemoveLineCommand = new RelayCommand(o => { if (o is TemplateLineVM l) RemoveLine(l); });
        MoveLineUpCommand = new RelayCommand(o => { if (o is TemplateLineVM l) MoveLine(l, -1); });
        MoveLineDownCommand = new RelayCommand(o => { if (o is TemplateLineVM l) MoveLine(l, +1); });
        SaveTemplateCommand = new RelayCommand(SaveTemplate);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetTemplateCommand = new RelayCommand(ResetTemplate);
        TestPrintTypeCommand = new AsyncRelayCommand(TestPrintTypeAsync);
        RestartServerCommand = new RelayCommand(RestartServer);
        RefreshPreviewCommand = new AsyncRelayCommand(UpdatePreviewAsync);
        LoadImageCommand = new RelayCommand(LoadImage);
        InstallDriverCommand = new AsyncRelayCommand(InstallDriverAsync, () => SelectedDriver is not null);
        RefreshPrintersCommand = new RelayCommand(RefreshPrinters);
        DiagnoseCommand = new AsyncRelayCommand(DiagnoseAsync, () => !IsDiagnosing);
        UseNetworkTargetCommand = new AsyncRelayCommand(UseNetworkTargetAsync);
        FullSetupCommand = new AsyncRelayCommand(FullSetupAsync, () => !IsSettingUp);
        _updater = new UpdateService(_config);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !IsUpdateBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => UpdateAvailable && !IsUpdateBusy);
        PauseResumeQueueCommand = new RelayCommand(ToggleQueuePause);
        ClearQueueCommand = new RelayCommand(() => Queue.ClearFinished());
        CancelJobCommand = new RelayCommand(o => { if (o is PrintQueueItem it) Queue.Cancel(it); });
        RetryJobCommand = new RelayCommand(o => { if (o is PrintQueueItem it) Queue.Retry(it); });

        // Печать задания из очереди: строим байты и шлём на активную цель.
        Queue.PrintCallback = async item =>
        {
            byte[] bytes = await _pipeline.BuildBytesAsync(item.Job, item.Geometry);
            await SendToTargetAsync(bytes);
        };
        Queue.CanPrint = () => HasPrinter;
        Queue.Notify = msg => Application.Current.Dispatcher.Invoke(() => { Notify(msg); AppendLog("[QUEUE] " + msg); });
        Queue.PausedChanged += _ => OnPropertyChanged(nameof(QueuePauseLabel));

        DiscoverDrivers();

        _server.JobReceived += OnJobReceivedFromNetwork;
        _server.Log += msg => AppendLog(msg);

        RestartServer();                       // старт на порту по умолчанию

        // Загружаем первый тип чека в конструктор (подставит JSON, шаблон и превью).
        _selectedDocType = DocTypes[0];
        LoadType(_selectedDocType.Type);
    }

    // ================= ФОРМАТЫ =================
    private ReceiptFormat _selectedFormat = ReceiptFormat.Json;
    public ReceiptFormat SelectedFormat
    {
        get => _selectedFormat;
        set { if (SetField(ref _selectedFormat, value)) { OnFormatFlagsChanged(); _ = UpdatePreviewAsync(); } }
    }

    // Булевы обёртки для RadioButton
    public bool IsJsonFormat  { get => SelectedFormat == ReceiptFormat.Json;  set { if (value) SelectedFormat = ReceiptFormat.Json; } }
    public bool IsHtmlFormat  { get => SelectedFormat == ReceiptFormat.Html;  set { if (value) SelectedFormat = ReceiptFormat.Html; } }
    public bool IsImageFormat { get => SelectedFormat == ReceiptFormat.Image; set { if (value) SelectedFormat = ReceiptFormat.Image; } }

    private void OnFormatFlagsChanged()
    {
        OnPropertyChanged(nameof(IsJsonFormat));
        OnPropertyChanged(nameof(IsHtmlFormat));
        OnPropertyChanged(nameof(IsImageFormat));
    }

    public string JsonInput { get => _jsonInput; set { SetField(ref _jsonInput, value); if (IsJsonFormat) _ = UpdatePreviewAsync(); } }
    private string _jsonInput = "";

    public string HtmlInput { get => _htmlInput; set { SetField(ref _htmlInput, value); } }
    private string _htmlInput = "";

    public string ImagePath { get => _imagePath; set => SetField(ref _imagePath, value); }
    private string _imagePath = "";
    private byte[]? _imageBytes;

    // ================= ГЕОМЕТРИЯ =================
    private double _tapeWidthMm = 80;
    public double TapeWidthMm
    {
        get => _tapeWidthMm;
        set { if (SetField(ref _tapeWidthMm, value)) { OnPropertyChanged(nameof(TapeWidthInPixels)); OnPropertyChanged(nameof(TapeWidthLabel)); RequestPreview(); } }
    }

    private double _sideMarginsPx = 12;
    public double SideMarginsPx
    {
        get => _sideMarginsPx;
        set { if (SetField(ref _sideMarginsPx, value)) { OnPropertyChanged(nameof(PreviewPadding)); RequestPreview(); } }
    }

    private double _headerFontPt = 11;
    /// <summary>Шрифт шапки/фискального блока (pt), минимум 5.</summary>
    public double HeaderFontPt
    {
        get => _headerFontPt;
        set { if (SetField(ref _headerFontPt, value)) { OnPropertyChanged(nameof(PreviewFontSize)); RequestPreview(); } }
    }

    private double _bodyFontPt = 11;
    /// <summary>Шрифт тела/футера (pt), минимум 5.</summary>
    public double BodyFontPt
    {
        get => _bodyFontPt;
        set { if (SetField(ref _bodyFontPt, value)) { OnPropertyChanged(nameof(PreviewFontSize)); RequestPreview(); } }
    }

    /// <summary>Ширина рулона в WPF-пикселях под 96 DPI: px = мм / 25.4 * 96.</summary>
    public double TapeWidthInPixels => PrintPipeline.PixelsFor(TapeWidthMm);
    public string TapeWidthLabel => $"{TapeWidthMm:0} мм  ({TapeWidthInPixels:0} px)";

    /// <summary>Padding внутри чека (боковые отступы).</summary>
    public Thickness PreviewPadding => new(SideMarginsPx, 8, SideMarginsPx, 8);

    /// <summary>pt → px (device-independent) для превью текста (JSON-текст): px = pt * 96/72.</summary>
    public double PreviewFontSize => BodyFontPt * 96.0 / 72.0;

    /// <summary>Режим «Графика»: вёрстка на ПК → растр (принтер не напрягается).</summary>
    private bool _isGraphicsMode = true;
    public bool IsGraphicsMode
    {
        get => _isGraphicsMode;
        set { if (SetField(ref _isGraphicsMode, value)) { OnPropertyChanged(nameof(RenderModeLabel)); _ = UpdatePreviewAsync(); } }
    }
    public string RenderModeLabel => IsGraphicsMode
        ? "Графика: вёрстка в приложении, принтеру уходит готовый растр."
        : "Текст ESC/POS: вёрстку выполняет принтер (быстрее, но шрифты принтера).";

    // ---- QR-код ссылки проверки чека ----
    private bool _qrEnabled = true;
    /// <summary>Печатать QR со ссылкой проверки чека (checkUrl) внизу по центру.</summary>
    public bool QrEnabled
    {
        get => _qrEnabled;
        set { if (SetField(ref _qrEnabled, value)) _ = UpdatePreviewAsync(); }
    }

    private double _qrSizeMm = 22;
    /// <summary>Размер QR в мм (10..40).</summary>
    public double QrSizeMm
    {
        get => _qrSizeMm;
        set { if (SetField(ref _qrSizeMm, value)) { OnPropertyChanged(nameof(QrSizeLabel)); RequestPreview(); } }
    }
    public string QrSizeLabel => $"{QrSizeMm:0} мм";

    // ---- Настройки принтера: вертикальные отступы, отрез, денежный ящик ----
    private double _topMarginLines;
    /// <summary>Пустых строк сверху чека (0..8).</summary>
    public double TopMarginLines
    {
        get => _topMarginLines;
        set { if (SetField(ref _topMarginLines, value)) RequestPreview(); }
    }

    private double _bottomMarginLines = 4;
    /// <summary>Пустых строк снизу перед отрезом (0..8).</summary>
    public double BottomMarginLines
    {
        get => _bottomMarginLines;
        set => SetField(ref _bottomMarginLines, value);
    }

    private bool _autoCut = true;
    /// <summary>Автоматический отрез ленты после печати.</summary>
    public bool AutoCut { get => _autoCut; set => SetField(ref _autoCut, value); }

    private bool _fullCut;
    /// <summary>Полный отрез вместо частичного.</summary>
    public bool FullCut { get => _fullCut; set => SetField(ref _fullCut, value); }

    private bool _openDrawer;
    /// <summary>Открывать денежный ящик после печати.</summary>
    public bool OpenDrawer { get => _openDrawer; set => SetField(ref _openDrawer, value); }

    private ReceiptGeometry Geometry => new()
    {
        TapeWidthMm = TapeWidthMm, SideMarginsPx = SideMarginsPx,
        HeaderFontPt = HeaderFontPt, BodyFontPt = BodyFontPt,
        RenderMode = IsGraphicsMode ? PrintRenderMode.Graphics : PrintRenderMode.EscPosText,
        QrEnabled = QrEnabled, QrSizeMm = QrSizeMm,
        TopMarginLines = (int)TopMarginLines, BottomMarginLines = (int)BottomMarginLines,
        AutoCut = AutoCut, FullCut = FullCut, OpenDrawer = OpenDrawer
    };

    /// <summary>Сохраняет геометрию/шрифты/QR в %AppData%\AutoPrint\config.json.</summary>
    private void SaveSettings()
    {
        _config.TapeWidthMm = TapeWidthMm;
        _config.SideMarginsPx = SideMarginsPx;
        _config.HeaderFontPt = HeaderFontPt;
        _config.BodyFontPt = BodyFontPt;
        _config.GraphicsMode = IsGraphicsMode;
        _config.QrEnabled = QrEnabled;
        _config.QrSizeMm = QrSizeMm;
        _config.TopMarginLines = (int)TopMarginLines;
        _config.BottomMarginLines = (int)BottomMarginLines;
        _config.AutoCut = AutoCut;
        _config.FullCut = FullCut;
        _config.OpenDrawer = OpenDrawer;
        _config.Save();
        Notify("Настройки печати сохранены.");
        AppendLog("[CFG] Настройки геометрии/QR сохранены");
    }

    /// <summary>Применяет ранее сохранённые настройки печати из конфига (без дёргания превью на каждую).</summary>
    private void LoadSettingsFromConfig()
    {
        _tapeWidthMm  = _config.TapeWidthMm;
        _sideMarginsPx = _config.SideMarginsPx;
        _headerFontPt = _config.HeaderFontPt;
        _bodyFontPt   = _config.BodyFontPt;
        _isGraphicsMode = _config.GraphicsMode;
        _qrEnabled    = _config.QrEnabled;
        _qrSizeMm     = _config.QrSizeMm;
        _topMarginLines    = _config.TopMarginLines;
        _bottomMarginLines = _config.BottomMarginLines;
        _autoCut      = _config.AutoCut;
        _fullCut      = _config.FullCut;
        _openDrawer   = _config.OpenDrawer;
    }

    // ================= ТИПЫ ЧЕКОВ + КОНСТРУКТОР =================
    public System.Collections.ObjectModel.ObservableCollection<DocTypeItem> DocTypes { get; } = new()
    {
        new("Операционный",  FiscalDocType.Sale),
        new("Открытие смены", FiscalDocType.ShiftOpen),
        new("Закрытие смены", FiscalDocType.ShiftClose),
        new("Пополнение",     FiscalDocType.Deposit),
        new("Снятие",         FiscalDocType.Withdrawal),
    };

    private DocTypeItem _selectedDocType = null!;
    public DocTypeItem SelectedDocType
    {
        get => _selectedDocType;
        set { if (SetField(ref _selectedDocType, value) && value is not null) LoadType(value.Type); }
    }

    /// <summary>Строки текущего шаблона (редактируются в конструкторе).</summary>
    public System.Collections.ObjectModel.ObservableCollection<TemplateLineVM> TemplateLines { get; } = new();

    private FiscalDocType _editingType = FiscalDocType.Sale;
    private bool _suppressPreview;   // чтобы массовая загрузка строк не дёргала превью на каждую

    /// <summary>Загружает тип: подставляет пример JSON, его сохранённый шаблон и обновляет превью.</summary>
    private void LoadType(FiscalDocType type)
    {
        _editingType = type;
        _suppressPreview = true;
        try
        {
            JsonInput = SampleFor(type);
            SelectedFormat = ReceiptFormat.Json;

            var tmpl = TemplateStore.Load(type);
            TemplateLines.Clear();
            foreach (var line in tmpl.Lines)
                TemplateLines.Add(new TemplateLineVM(line, OnTemplateChanged));
        }
        finally { _suppressPreview = false; }
        _ = UpdatePreviewAsync();
    }

    private static string SampleFor(FiscalDocType t) => t switch
    {
        FiscalDocType.ShiftOpen  => TestData.SampleShiftOpenJson(),
        FiscalDocType.ShiftClose => TestData.SampleShiftCloseJson(),
        FiscalDocType.Deposit    => TestData.SampleDepositJson(),
        FiscalDocType.Withdrawal => TestData.SampleWithdrawalJson(),
        _                        => TestData.SampleFiscalJson(),
    };

    /// <summary>Собирает актуальный шаблон из строк конструктора.</summary>
    public ReceiptTemplate CurrentTemplate() => new()
    {
        DocType = ReceiptTemplate.KeyOf(_editingType),
        Lines = TemplateLines.Select(l => l.Model).ToList()
    };

    private void OnTemplateChanged() { if (!_suppressPreview) _ = UpdatePreviewAsync(); }

    private void AddLine(LineKind kind)
    {
        var line = new TemplateLine { Kind = kind, Font = FontGroup.Body, Align = LineAlign.Left };
        TemplateLines.Add(new TemplateLineVM(line, OnTemplateChanged));
        OnTemplateChanged();
    }

    private void RemoveLine(TemplateLineVM l) { TemplateLines.Remove(l); OnTemplateChanged(); }

    private void MoveLine(TemplateLineVM l, int delta)
    {
        int i = TemplateLines.IndexOf(l);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= TemplateLines.Count) return;
        TemplateLines.Move(i, j);
        OnTemplateChanged();
    }

    /// <summary>Перемещение строки перетаскиванием (вызывается из code-behind).</summary>
    public void MoveLineTo(TemplateLineVM src, int newIndex)
    {
        int i = TemplateLines.IndexOf(src);
        if (i < 0 || newIndex < 0 || newIndex >= TemplateLines.Count || i == newIndex) return;
        TemplateLines.Move(i, newIndex);
        OnTemplateChanged();
    }

    private void SaveTemplate()
    {
        TemplateStore.Save(_editingType, CurrentTemplate());
        Notify($"Шаблон «{SelectedDocType?.Label}» сохранён.");
        AppendLog($"[TPL] Сохранён шаблон {_editingType}");
    }

    private void ResetTemplate()
    {
        TemplateStore.Reset(_editingType);
        LoadType(_editingType);
        Notify($"Шаблон «{SelectedDocType?.Label}» сброшен к стандартному.");
    }

    private async Task TestPrintTypeAsync()
    {
        // Печать идёт через конвейер, который берёт шаблон из хранилища — сохраняем текущий.
        TemplateStore.Save(_editingType, CurrentTemplate());
        var job = new PrintJob { Format = ReceiptFormat.Json, Payload = JsonInput, Source = "тест-тип" };
        var item = Queue.Enqueue(job, Geometry.Clone());
        Notify(HasPrinter
            ? $"Тест «{SelectedDocType?.Label}» в очереди (#{item.Id})."
            : $"Тест «{SelectedDocType?.Label}» в очереди (#{item.Id}). Подключите принтер.");
        await Task.CompletedTask;
    }

    // ================= ПРИНТЕР / СЕТЬ =================
    public System.Collections.ObjectModel.ObservableCollection<string> Printers { get; } = new();

    private string? _selectedPrinter;
    public string? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetField(ref _selectedPrinter, value) && !string.IsNullOrEmpty(value))
                ActiveTarget = new PrintTarget { Kind = ConnectionKind.WindowsSpooler, PrinterName = value! };
        }
    }

    /// <summary>Активная цель печати (USB/спулер или сеть), выбирается диагностикой или вручную.</summary>
    private PrintTarget _activeTarget = new();
    public PrintTarget ActiveTarget
    {
        get => _activeTarget;
        private set
        {
            SetField(ref _activeTarget, value);
            OnPropertyChanged(nameof(ActiveTargetLabel));
            OnPropertyChanged(nameof(HasPrinter));
            OnPropertyChanged(nameof(TrayStatus));
            if (value.IsReady) Queue.Kick();   // допечатать задания, ждавшие подключения
        }
    }
    public string ActiveTargetLabel => ActiveTarget.Describe();
    public bool HasPrinter => ActiveTarget.IsReady;

    /// <summary>Многострочный статус для подсказки/меню значка в системном трее.</summary>
    public string TrayStatus =>
        $"AutoPrint — принт-сервер\nСервер: {ServerStatus}\nПринтер: {ActiveTargetLabel}\nВ очереди: {Queue.Items.Count}";

    // ---- Сетевая цель (RJ45), заполняется диагностикой или вручную ----
    private string _networkHost = "";
    public string NetworkHost { get => _networkHost; set => SetField(ref _networkHost, value); }

    private string _networkPort = "9100";
    public string NetworkPort { get => _networkPort; set => SetField(ref _networkPort, value); }

    // ---- Диагностика ----
    public System.Collections.ObjectModel.ObservableCollection<DiagnosticStep> DiagnosticSteps { get; } = new();

    private bool _isDiagnosing;
    public bool IsDiagnosing { get => _isDiagnosing; set => SetField(ref _isDiagnosing, value); }

    private bool _isSettingUp;
    public bool IsSettingUp { get => _isSettingUp; set => SetField(ref _isSettingUp, value); }

    private string _setupStage = "";
    /// <summary>Текущий шаг автонастройки — показывается на кнопке, пока идёт процесс.</summary>
    public string SetupStage
    {
        get => _setupStage;
        private set { if (SetField(ref _setupStage, value)) OnPropertyChanged(nameof(SetupButtonLabel)); }
    }
    public string SetupButtonLabel => string.IsNullOrEmpty(SetupStage) ? "⚡  Автонастройка в один клик" : SetupStage;

    private string _portText = "5001";
    public string PortText { get => _portText; set => SetField(ref _portText, value); }

    // ================= ДРАЙВЕРЫ =================
    public System.Collections.ObjectModel.ObservableCollection<DriverPackage> Drivers { get; } = new();

    private DriverPackage? _selectedDriver;
    public DriverPackage? SelectedDriver { get => _selectedDriver; set => SetField(ref _selectedDriver, value); }

    /// <summary>Автоматически переименовать установленный принтер в это имя (пункт 2).</summary>
    public string PrinterFriendlyName
    {
        get => _config.PrinterFriendlyName;
        set { if (_config.PrinterFriendlyName != value) { _config.PrinterFriendlyName = value; _config.Save(); OnPropertyChanged(); } }
    }

    private bool _autoNamePrinter = true;
    /// <summary>Переименовывать ли принтер после установки драйвера.</summary>
    public bool AutoNamePrinter { get => _autoNamePrinter; set => SetField(ref _autoNamePrinter, value); }

    public bool HasDrivers => Drivers.Count > 0;

    private void DiscoverDrivers()
    {
        Drivers.Clear();
        foreach (var d in DriverInstaller.Discover()) Drivers.Add(d);
        SelectedDriver = Drivers.FirstOrDefault();
        OnPropertyChanged(nameof(HasDrivers));
    }

    private Task InstallDriverAsync() => InstallDriverAsync(silent: false);

    private async Task InstallDriverAsync(bool silent)
    {
        if (SelectedDriver is null) return;
        try
        {
            Notify($"Запуск установки: {SelectedDriver.DisplayName}. Подтвердите запрос UAC…");
            AppendLog($"[DRV] Установка драйвера: {SelectedDriver.FileName}" + (silent ? " (тихий режим)" : ""));
            int code = await DriverInstaller.InstallAsync(SelectedDriver, silent);
            Notify($"Инсталлятор «{SelectedDriver.DisplayName}» завершился (код {code}).");
            AppendLog($"[DRV] Готово, exit code {code}");
            RefreshPrinters();   // после установки в системе мог появиться новый принтер
            await TryRenameInstalledPrinterAsync();
        }
        catch (OperationCanceledException)
        {
            Notify("Установка драйвера отменена (UAC).", ok: false);
        }
        catch (Exception ex)
        {
            Notify($"Ошибка установки драйвера: {ex.Message}", ok: false);
        }
    }

    /// <summary>
    /// После установки драйвера присваивает свежепоявившемуся чековому принтеру
    /// стабильное имя (напр. «Mulex P80mm»), чтобы фронт мог адресовать печать по нему.
    /// </summary>
    private async Task TryRenameInstalledPrinterAsync()
    {
        string desired = (PrinterFriendlyName ?? "").Trim();
        if (!AutoNamePrinter || desired.Length == 0) return;

        // Уже есть принтер с нужным именем — ничего делать не надо.
        if (Printers.Any(p => string.Equals(p, desired, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPrinter = desired;
            return;
        }

        string? target = Printers.FirstOrDefault(PrinterDiagnostics.IsLikelyReceiptPrinter);
        if (target is null) return;

        try
        {
            Notify($"Переименовываю принтер «{target}» → «{desired}» (подтвердите UAC)…");
            bool ok = await WindowsPrinterRegistrar.RenamePrinterAsync(target, desired);
            AppendLog(ok ? $"[DRV] Принтер переименован: {target} → {desired}"
                         : $"[DRV] Не удалось переименовать принтер {target}");
            RefreshPrinters();
            if (ok) SelectedPrinter = Printers.FirstOrDefault(p => string.Equals(p, desired, StringComparison.OrdinalIgnoreCase)) ?? SelectedPrinter;
        }
        catch (OperationCanceledException)
        {
            AppendLog("[DRV] Переименование принтера пропущено (отказ UAC)");
        }
        catch (Exception ex)
        {
            AppendLog($"[DRV] Ошибка переименования принтера: {ex.Message}");
        }
    }

    private void RefreshPrinters()
    {
        string? prev = SelectedPrinter;
        Printers.Clear();
        foreach (var p in RawPrinterHelper.InstalledPrinters()) Printers.Add(p);
        SelectedPrinter = Printers.FirstOrDefault(p => p == prev)
                          ?? Printers.FirstOrDefault(PrinterDiagnostics.IsLikelyReceiptPrinter);
    }

    private string _serverStatus = "остановлен";
    public string ServerStatus { get => _serverStatus; private set { if (SetField(ref _serverStatus, value)) OnPropertyChanged(nameof(TrayStatus)); } }

    private Brush _serverStatusBrush = Brushes.Gray;
    public Brush ServerStatusBrush { get => _serverStatusBrush; private set => SetField(ref _serverStatusBrush, value); }

    // ================= ПРЕДПРОСМОТР =================
    // Режим превью определяется тем, что реально отрисовано (текст vs растр),
    // а не выбранной радиокнопкой — иначе PDF/картинка из сети не показались бы.
    private bool _previewShowsImage;
    public bool ShowImagePreview
    {
        get => _previewShowsImage;
        private set { if (SetField(ref _previewShowsImage, value)) OnPropertyChanged(nameof(ShowTextPreview)); }
    }
    public bool ShowTextPreview => !_previewShowsImage;

    private string _previewText = "";
    public string PreviewText { get => _previewText; private set => SetField(ref _previewText, value); }

    private ImageSource? _previewImage;
    public ImageSource? PreviewImage { get => _previewImage; private set => SetField(ref _previewImage, value); }

    private string _log = "";
    public string Log { get => _log; private set => SetField(ref _log, value); }

    private string _notification = "Готов к работе.";
    public string Notification { get => _notification; private set => SetField(ref _notification, value); }

    // ================= КОМАНДЫ =================
    public ICommand PrintSystemTestCommand { get; }
    public ICommand EmulateWebRequestCommand { get; }
    public ICommand RestartServerCommand { get; }
    public ICommand RefreshPreviewCommand { get; }
    public ICommand LoadImageCommand { get; }
    public ICommand InstallDriverCommand { get; }
    public ICommand RefreshPrintersCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand UseNetworkTargetCommand { get; }
    public ICommand FullSetupCommand { get; }
    public ICommand PauseResumeQueueCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand RetryJobCommand { get; }
    // ---- Конструктор чека ----
    public ICommand AddTextLineCommand { get; }
    public ICommand AddTwoColLineCommand { get; }
    public ICommand AddSpacerLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand MoveLineUpCommand { get; }
    public ICommand MoveLineDownCommand { get; }
    public ICommand SaveTemplateCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetTemplateCommand { get; }
    public ICommand TestPrintTypeCommand { get; }

    public string QueuePauseLabel => Queue.IsPaused ? "▶ Возобновить" : "⏸ Пауза";
    private void ToggleQueuePause() { if (Queue.IsPaused) Queue.Resume(); else Queue.Pause(); }

    // ---- Перезапуск сервера (смена порта на лету) ----
    private void RestartServer()
    {
        if (!int.TryParse(PortText, out int port) || port < 1 || port > 65535)
        {
            Notify("Некорректный номер порта (1–65535).", ok: false);
            return;
        }
        try
        {
            _server.Start(port);
            ServerStatus = $"слушает :{port}";
            ServerStatusBrush = (Brush)Application.Current.FindResource("SuccessBrush");
            Notify($"Сервер перезапущен на порту {port}.");
        }
        catch (Exception ex)
        {
            ServerStatus = "ошибка";
            ServerStatusBrush = (Brush)Application.Current.FindResource("DangerBrush");
            Notify($"Не удалось занять порт: {ex.Message}", ok: false);
        }
    }

    /// <summary>Единая отправка байтов на активную цель: USB/спулер или сеть (TCP 9100).</summary>
    private async Task SendToTargetAsync(byte[] bytes)
    {
        switch (ActiveTarget.Kind)
        {
            case ConnectionKind.Network:
                try
                {
                    await RawNetworkPrinter.SendBytesAsync(ActiveTarget.Host, ActiveTarget.Port, bytes);
                }
                catch (Exception)
                {
                    // IP принтера мог смениться (DHCP) — сканируем подсеть заново и пробуем один раз повторно.
                    if (!await TryRediscoverNetworkTargetAsync()) throw;
                    await RawNetworkPrinter.SendBytesAsync(ActiveTarget.Host, ActiveTarget.Port, bytes);
                }
                break;
            case ConnectionKind.WindowsSpooler:
                await Task.Run(() =>
                {
                    // Сначала статус: иначе спулер молча примет задание и повесит его в Error,
                    // а очередь отрапортует «напечатано», хотя бумага не вышла.
                    RawPrinterHelper.EnsureReady(ActiveTarget.PrinterName);
                    if (!RawPrinterHelper.SendBytes(ActiveTarget.PrinterName, bytes))
                        throw new IOException($"Спулер не принял задание для «{ActiveTarget.PrinterName}».");
                });
                break;
            default:
                throw new InvalidOperationException("Принтер не подключён. Запустите «Определить и подключить».");
        }
    }

    /// <summary>
    /// Сетевая печать сорвалась — принтер мог сменить IP по DHCP. Один раз сканируем локальные
    /// подсети заново; если нашли принтер на новом адресе, обновляем ActiveTarget и сообщаем.
    /// </summary>
    private async Task<bool> TryRediscoverNetworkTargetAsync()
    {
        string oldHost = ActiveTarget.Host;
        string? found = await PrinterDiagnostics.RescanForHostAsync(ActiveTarget.Port);
        if (found is null || found == oldHost) return false;

        ActiveTarget = new PrintTarget { Kind = ConnectionKind.Network, Host = found, Port = ActiveTarget.Port };
        NetworkHost = found;
        AppendLog($"[NET] Принтер сменил адрес: {oldHost} → {found}. Обновил цель печати.");
        return true;
    }

    // ---- Кнопка "Напечатать системный тест" ----
    private async Task PrintSystemTestAsync()
    {
        var job = new PrintJob { Format = ReceiptFormat.Json, Payload = TestData.SystemTestJson(), Source = "тест" };
        await RenderJobToPreviewAsync(job);
        var item = Queue.Enqueue(job, Geometry.Clone());
        Notify(HasPrinter
            ? $"Системный тест поставлен в очередь (#{item.Id}) → печать на {ActiveTarget.Describe()}."
            : $"Системный тест в очереди (#{item.Id}). Подключите принтер — напечатается автоматически.");
        AppendLog($"[TEST] В очередь #{item.Id}");
    }

    // ---- Кнопка "Эмулировать входящий веб-запрос" ----
    private async Task EmulateWebRequestAsync()
    {
        var job = SelectedFormat switch
        {
            ReceiptFormat.Html  => new PrintJob { Format = ReceiptFormat.Html,  Payload = HtmlInput, Source = "эмулятор" },
            ReceiptFormat.Image => new PrintJob { Format = ReceiptFormat.Image, RawImage = _imageBytes, Source = "эмулятор" },
            _                   => new PrintJob { Format = ReceiptFormat.Json,  Payload = JsonInput, Source = "эмулятор" },
        };

        AppendLog($"[NET] ← Перехвачен веб-запрос ({job.Format}, {job.Source})");
        await RenderJobToPreviewAsync(job);
        var item = Queue.Enqueue(job, Geometry.Clone());
        Notify($"Перехвачена входящая сделка (#{item.Id}, {job.Format}). Live Preview обновлён, задание в очереди.");
    }

    // ---- Приём реального задания из сети (в UI-поток) ----
    private void OnJobReceivedFromNetwork(PrintJob job)
    {
        Application.Current.Dispatcher.Invoke(async () =>
        {
            AppendLog($"[NET] ← {job.Format} с порта {_server.Port}");
            if (job.Format is ReceiptFormat.Image or ReceiptFormat.Pdf)
                AppendLog("[FMT] Чек пришёл готовой картинкой/PDF: размер шрифта задаётся на фронте " +
                          "(в пикселях его не изменить). Из настроек применяются боковые отступы, отрез и ящик. " +
                          "Чтобы управлять шрифтами из приложения — присылайте чек JSON-шаблоном.");
            LogTemplateResolution(job);          // какой шаблон применится к этому чеку
            ApplyRequestedPrinter(job);          // печать на принтер, указанный в запросе
            await RenderJobToPreviewAsync(job);
            var item = Queue.Enqueue(job, Geometry.Clone());
            Notify($"Входящий чек #{item.Id} ({job.Format}) поставлен в очередь.");
        });
    }

    /// <summary>
    /// Диагностика пункта «с фронта печатается стандартный вид»: показывает, распознан ли
    /// JSON как фискальный, в какой тип документа он смаппился и есть ли для этого типа
    /// сохранённый пользовательский шаблон (иначе применится дефолтный «стандартный» вид).
    /// </summary>
    private void LogTemplateResolution(PrintJob job)
    {
        if (job.Format != ReceiptFormat.Json) return;
        bool fiscal = FiscalReceiptRenderer.IsFiscalJson(job.Payload);
        if (!fiscal)
        {
            AppendLog("[TPL] JSON НЕ распознан как фискальный → печать простым макетом (не конструктор). " +
                      "Проверьте, что фронт шлёт docType/orgName/rnm/znm/bin.");
            return;
        }
        try
        {
            var f = FiscalReceiptRenderer.Parse(job.Payload);
            string key = ReceiptTemplate.KeyOf(f.DocType);
            bool saved = TemplateStore.Exists(f.DocType);
            AppendLog($"[TPL] Тип документа: {f.DocType} ({key}). " +
                      (saved ? "Применяется ВАШ сохранённый шаблон."
                             : "Сохранённого шаблона для этого типа НЕТ → стандартный вид. " +
                               "Откройте этот тип в «Тип чека», настройте и нажмите «Сохранить шаблон»."));
        }
        catch (Exception ex) { AppendLog($"[TPL] Ошибка разбора фискального JSON: {ex.Message}"); }
    }

    /// <summary>
    /// Если в запросе указан "printer" и такой принтер установлен в системе — направляем
    /// печать именно на него (фронт авторитетен в выборе принтера). Иначе печатаем на
    /// цель, выбранную в приложении, и пишем предупреждение в лог.
    /// </summary>
    private void ApplyRequestedPrinter(PrintJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PrinterName)) return;

        string? match = Printers.FirstOrDefault(
            p => string.Equals(p, job.PrinterName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            RefreshPrinters();   // мог появиться после установки драйвера
            match = Printers.FirstOrDefault(
                p => string.Equals(p, job.PrinterName, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            AppendLog($"[NET] Принтер из запроса «{job.PrinterName}» не найден среди установленных — " +
                      $"печать на текущую цель ({ActiveTarget.Describe()}).");
            return;
        }

        bool alreadyThere = ActiveTarget.Kind == ConnectionKind.WindowsSpooler
                            && string.Equals(ActiveTarget.PrinterName, match, StringComparison.OrdinalIgnoreCase);
        if (!alreadyThere)
        {
            SelectedPrinter = match;   // setter выставит ActiveTarget = спулер Windows
            AppendLog($"[NET] Печать направлена на принтер из запроса: {match}");
        }
    }

    // ================= ДИАГНОСТИКА И АВТОПОДКЛЮЧЕНИЕ =================
    private async Task DiagnoseAsync()
    {
        DiagnosticSteps.Clear();
        IsDiagnosing = true;
        Notify("Диагностика: ищу принтер по USB и в сети (порт 9100)…");
        AppendLog("[DIAG] Старт диагностики");

        // Progress<T> публикует шаги в UI-потоке по мере выполнения.
        var progress = new Progress<DiagnosticStep>(step => DiagnosticSteps.Add(step));
        try
        {
            var report = await PrinterDiagnostics.RunAsync(progress);
            ApplyRecommendation(report);
        }
        catch (Exception ex)
        {
            Notify($"Ошибка диагностики: {ex.Message}", ok: false);
        }
        finally
        {
            IsDiagnosing = false;
        }
    }

    /// <summary>Применяет рекомендацию диагностики: сам подключает рабочий канал.</summary>
    private void ApplyRecommendation(DiagnosticReport report)
    {
        AppendLog("[DIAG] " + report.Summary);

        if (report.Recommended is { IsReady: true } tgt)
        {
            if (tgt.Kind == ConnectionKind.WindowsSpooler)
            {
                RefreshPrinters();
                SelectedPrinter = Printers.FirstOrDefault(p => p == tgt.PrinterName) ?? tgt.PrinterName;
                // SelectedPrinter setter уже выставит ActiveTarget = спулер
            }
            else if (tgt.Kind == ConnectionKind.Network)
            {
                NetworkHost = tgt.Host;
                NetworkPort = tgt.Port.ToString();
                ActiveTarget = tgt;
            }
            Notify($"✓ Автоподключение: {tgt.Describe()}. {report.Summary}");
        }
        else
        {
            Notify(report.Summary, ok: false);
        }
    }

    /// <summary>Ручное подключение по сети (пользователь ввёл IP:порт).</summary>
    private async Task UseNetworkTargetAsync()
    {
        if (string.IsNullOrWhiteSpace(NetworkHost)) { Notify("Укажите IP-адрес принтера.", ok: false); return; }
        if (!int.TryParse(NetworkPort, out int port) || port < 1 || port > 65535) port = RawNetworkPrinter.DefaultPort;

        Notify($"Проверяю {NetworkHost}:{port}…");
        bool ok = await RawNetworkPrinter.ProbeAsync(NetworkHost, port, 900);
        if (!ok)
        {
            Notify($"{NetworkHost}:{port} недоступен (порт закрыт или неверный IP).", ok: false);
            return;
        }
        ActiveTarget = new PrintTarget { Kind = ConnectionKind.Network, Host = NetworkHost, Port = port };
        Notify($"✓ Подключено по сети: {NetworkHost}:{port}");
        AppendLog($"[NET] Ручное подключение → {NetworkHost}:{port}");
    }

    /// <summary>
    /// Полная автонастройка в один клик. Порядок продиктован тем, как реально работают
    /// чековые принтеры:
    ///  1) Явно указанный IP (с чека самотеста) — сеть не требует драйвера вовсе, ESC/POS
    ///     уходит прямо в TCP 9100; адрес может быть в другой подсети, скан его не найдёт.
    ///  2) Автопоиск: принтеры в спулере Windows (USB) + сканирование локальной сети.
    ///  3) Если не нашли — тихая установка комплектного драйвера (Inno Setup: /VERYSILENT,
    ///     мастер не показывается, только запрос UAC) и повторный поиск: USB-принтер
    ///     появляется в системе только после установки драйвера.
    ///  4) Для сетевого принтера — регистрация в Windows штатным «Generic / Text Only»
    ///     на RAW-порт 9100, чтобы принтер был настроен и для других программ.
    ///  5) Тестовый чек — подтверждение, что печать реально работает.
    /// </summary>
    private async Task FullSetupAsync()
    {
        IsSettingUp = true;
        try
        {
            AppendLog("[SETUP] Старт автонастройки");

            // --- 1. Явный IP с чека самотеста ---
            if (!HasPrinter && !string.IsNullOrWhiteSpace(NetworkHost))
            {
                SetupStage = $"Пробую IP {NetworkHost}…";
                Notify($"Автонастройка: пробую подключиться по указанному IP {NetworkHost}…");
                await UseNetworkTargetAsync();
            }

            // --- 2. Автопоиск: USB-спулер + скан подсети ---
            if (!HasPrinter)
            {
                SetupStage = "Ищу принтер (USB/сеть)…";
                await DiagnoseAsync();
            }

            // --- 3. Тихая установка драйвера и повторный поиск (USB-сценарий) ---
            if (!HasPrinter && SelectedDriver is not null)
            {
                SetupStage = "Устанавливаю драйвер…";
                Notify("Автонастройка: принтер не найден — устанавливаю драйвер (подтвердите запрос UAC)…");
                await InstallDriverAsync(silent: true);

                SetupStage = "Повторный поиск принтера…";
                await DiagnoseAsync();
            }

            if (!HasPrinter)
            {
                Notify("Автонастройка не нашла принтер. Проверьте кабель (USB/RJ45), а для сети — " +
                       "распечатайте на принтере чек самотеста и впишите его IP в поле выше.", ok: false);
                AppendLog("[SETUP] Принтер не найден");
                return;
            }

            // --- 4. Сетевой принтер: регистрируем в Windows (не критично, при отказе продолжаем) ---
            if (ActiveTarget.Kind == ConnectionKind.Network &&
                !WindowsPrinterRegistrar.IsRegistered(ActiveTarget.Host))
            {
                SetupStage = "Регистрирую принтер в Windows…";
                try
                {
                    bool ok = await WindowsPrinterRegistrar.RegisterAsync(ActiveTarget.Host, ActiveTarget.Port);
                    AppendLog(ok
                        ? $"[SETUP] Принтер зарегистрирован в Windows: {WindowsPrinterRegistrar.PrinterNameFor(ActiveTarget.Host)}"
                        : "[SETUP] Регистрация в Windows не удалась (печать по сети всё равно работает)");
                    if (ok) RefreshPrinters();
                }
                catch (OperationCanceledException)
                {
                    AppendLog("[SETUP] Регистрация в Windows пропущена (отказ UAC) — печать по сети работает без неё");
                }
                catch (Exception ex)
                {
                    AppendLog($"[SETUP] Регистрация в Windows не удалась: {ex.Message}");
                }
            }

            // --- 5. Контрольный тестовый чек ---
            SetupStage = "Печатаю тестовый чек…";
            Notify($"✓ Принтер подключён: {ActiveTarget.Describe()}. Печатаю тестовый чек для проверки…");
            await PrintSystemTestAsync();
            AppendLog($"[SETUP] Готово: {ActiveTarget.Describe()}");
        }
        finally
        {
            SetupStage = "";
            IsSettingUp = false;
        }
    }

    // ================= АВТООБНОВЛЕНИЕ (GitHub Releases) =================
    public string CurrentVersionLabel => "v" + UpdateService.CurrentVersion.ToString(3);

    public string UpdateOwner
    {
        get => _config.UpdateOwner;
        set { if (_config.UpdateOwner != value) { _config.UpdateOwner = value; _config.Save(); OnPropertyChanged(); } }
    }
    public string UpdateRepo
    {
        get => _config.UpdateRepo;
        set { if (_config.UpdateRepo != value) { _config.UpdateRepo = value; _config.Save(); OnPropertyChanged(); } }
    }

    private string _updateStatus = "Нажмите «Проверить обновления».";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }

    private bool _isUpdateBusy;
    public bool IsUpdateBusy { get => _isUpdateBusy; private set { SetField(ref _isUpdateBusy, value); OnPropertyChanged(nameof(ShowUpdateProgress)); } }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => SetField(ref _updateProgress, value); }
    public bool ShowUpdateProgress => IsUpdateBusy;

    private string _updateNotes = "";
    public string UpdateNotes { get => _updateNotes; private set => SetField(ref _updateNotes, value); }

    public ICommand CheckUpdateCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    private async Task CheckUpdateAsync()
    {
        IsUpdateBusy = true;
        UpdateStatus = "Проверка обновлений…";
        UpdateNotes = "";
        try
        {
            var info = await _updater.CheckAsync();
            _latestUpdate = info;
            if (UpdateService.IsNewer(info))
            {
                UpdateAvailable = true;
                UpdateStatus = $"Доступна версия {info.Version} ({info.Tag}). Текущая: {CurrentVersionLabel}.";
                UpdateNotes = info.Notes;
                if (!info.HasAsset) UpdateStatus += " ⚠ В релизе нет .zip/.exe ассета.";
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatus = $"У вас последняя версия ({CurrentVersionLabel}).";
            }
            AppendLog($"[UPD] latest={info.Tag} newer={UpdateService.IsNewer(info)}");
        }
        catch (Exception ex)
        {
            UpdateAvailable = false;
            UpdateStatus = $"Не удалось проверить обновления: {ex.Message}";
        }
        finally { IsUpdateBusy = false; }
    }

    private async Task InstallUpdateAsync()
    {
        if (_latestUpdate is not { HasAsset: true } info)
        {
            UpdateStatus = "Нет ассета для установки.";
            return;
        }
        IsUpdateBusy = true;
        try
        {
            UpdateStatus = $"Скачивание {info.AssetName}…";
            var progress = new Progress<double>(p => UpdateProgress = Math.Round(p * 100));
            string path = await _updater.DownloadAsync(info, progress);

            UpdateStatus = "Установка и перезапуск приложения…";
            AppendLog($"[UPD] Установка {info.Tag}");
            _updater.ApplyAndRestart(info, path);
            Application.Current.Shutdown();   // апдейтер дождётся выхода и подменит файлы
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Ошибка обновления: {ex.Message}";
            IsUpdateBusy = false;
        }
    }

    // Дебаунс превью: слайдеры геометрии/шрифтов/QR при таскании дёргают рендер десятки раз;
    // коалесцируем в один рендер через короткую паузу простоя.
    private System.Windows.Threading.DispatcherTimer? _previewDebounce;
    private void RequestPreview()
    {
        _previewDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewDebounce.Tick -= OnPreviewDebounceTick;
        _previewDebounce.Tick += OnPreviewDebounceTick;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }
    private void OnPreviewDebounceTick(object? s, EventArgs e)
    {
        _previewDebounce!.Stop();
        _ = UpdatePreviewAsync();
    }

    // ================= ПРЕДПРОСМОТР: РЕНДЕР =================
    private async Task UpdatePreviewAsync()
    {
        var job = SelectedFormat switch
        {
            ReceiptFormat.Html  => new PrintJob { Format = ReceiptFormat.Html,  Payload = HtmlInput },
            ReceiptFormat.Image => new PrintJob { Format = ReceiptFormat.Image, RawImage = _imageBytes },
            _                   => new PrintJob { Format = ReceiptFormat.Json,  Payload = JsonInput },
        };
        await RenderJobToPreviewAsync(job);
    }

    private async Task RenderJobToPreviewAsync(PrintJob job)
    {
        try
        {
            int dots = PrintPipeline.DotsFor(TapeWidthMm);
            switch (job.Format)
            {
                case ReceiptFormat.Json:
                    if (FiscalReceiptRenderer.IsFiscalJson(job.Payload))
                    {
                        var fiscal = FiscalReceiptRenderer.Parse(job.Payload);
                        var tmpl = TemplateLines.Count > 0 ? CurrentTemplate() : ReceiptTemplate.Default(fiscal.DocType);
                        var geoNow = Geometry;
                        PreviewImage = await Task.Run(() =>
                            MonoPreviewFrom(FiscalReceiptRenderer.Render(fiscal, tmpl, geoNow), dots));
                        ShowImagePreview = true;
                    }
                    else
                    {
                        PreviewText = BuildTextPreview(ReceiptComposer.ParseJson(job.Payload));
                        ShowImagePreview = false;
                    }
                    break;

                case ReceiptFormat.Html:
                {
                    int widthPx = (int)Math.Round(TapeWidthInPixels);
                    using (Bitmap bmp = await _htmlRenderer.RenderAsync(
                               job.Payload, widthPx, BodyFontPt, (int)SideMarginsPx))
                    {
                        PreviewImage = WpfInterop.ToImageSource(bmp);
                    }
                    ShowImagePreview = true;
                    break;
                }

                case ReceiptFormat.Image:
                {
                    byte[]? data = job.RawImage ?? _imageBytes;   // из сети или из выбранного файла
                    if (data is null) { PreviewText = "Изображение не загружено."; ShowImagePreview = false; break; }
                    int inset = PrintPipeline.MarginDots(SideMarginsPx);
                    PreviewImage = await Task.Run(() => MonoPreviewFrom(ImageProcessor.FromBytes(data), dots, inset));
                    ShowImagePreview = true;
                    break;
                }

                case ReceiptFormat.Pdf:
                {
                    if (job.RawImage is null) { PreviewText = "Нет данных PDF."; ShowImagePreview = false; break; }
                    byte[] pdf = job.RawImage;
                    int insetPdf = PrintPipeline.MarginDots(SideMarginsPx);
                    PreviewImage = await Task.Run(() => MonoPreviewFrom(PdfRenderer.Render(pdf, dots), dots, insetPdf));
                    ShowImagePreview = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            PreviewText = $"⚠ Ошибка предпросмотра:\n{ex.Message}";
            ShowImagePreview = false;
        }
    }

    /// <summary>Прогоняет bitmap через дизеринг и возвращает замороженный 1-битный предпросмотр.</summary>
    private static ImageSource MonoPreviewFrom(Bitmap source, int dots, int insetDots = 0)
    {
        using Bitmap src = source;
        var (packed, wb, h) = ImageProcessor.PrepareRaster(src, dots, insetDots);
        using Bitmap mono = RebuildMonoPreview(packed, wb, h);
        return WpfInterop.ToImageSource(mono);
    }

    /// <summary>Текстовый предпросмотр JSON-чека (моноширинный).</summary>
    private string BuildTextPreview(ReceiptModel r)
    {
        int cols = ReceiptComposer.ColumnsFor(TapeWidthMm);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Center(r.Header, cols));
        if (!string.IsNullOrWhiteSpace(r.SubHeader)) sb.AppendLine(Center(r.SubHeader, cols));
        sb.AppendLine(new string('-', cols));
        foreach (var it in r.Items)
        {
            sb.AppendLine(it.Name);
            string left = $"  {it.Qty} x {it.Price:N2}";
            string right = it.Sum.ToString("N2");
            int sp = Math.Max(1, cols - left.Length - right.Length);
            sb.AppendLine(left + new string(' ', sp) + right);
        }
        sb.AppendLine(new string('-', cols));
        string t = $"ИТОГО: {r.Total:N2} р.";
        sb.AppendLine(t);
        if (!string.IsNullOrWhiteSpace(r.Barcode)) sb.AppendLine("\n" + Center("|| " + r.Barcode + " ||", cols));
        if (!string.IsNullOrWhiteSpace(r.Footer)) sb.AppendLine(Center(r.Footer, cols));
        sb.AppendLine(Center("— — — ✂ отрез — — —", cols));
        return sb.ToString();
    }

    private static string Center(string s, int cols)
    {
        var lines = s.Split('\n');
        return string.Join('\n', lines.Select(l =>
        {
            l = l.Trim();
            int pad = Math.Max(0, (cols - l.Length) / 2);
            return new string(' ', pad) + l;
        }));
    }

    /// <summary>
    /// Восстанавливает картинку из 1-битного буфера для показа результата дизеринга.
    /// Через LockBits и нативный формат 1bpp — попиксельный SetPixel на растре 576×N
    /// давал сотни тысяч GDI-вызовов на каждый кадр превью (заметная нагрузка при таскании слайдеров).
    /// </summary>
    private static Bitmap RebuildMonoPreview(byte[] packed, int widthBytes, int height)
    {
        int width = widthBytes * 8;
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format1bppIndexed);
        // Бит=1 (чёрная точка ESC/POS) → индекс 1 палитры = чёрный.
        var pal = bmp.Palette;
        pal.Entries[0] = Color.White;
        pal.Entries[1] = Color.Black;
        bmp.Palette = pal;

        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format1bppIndexed);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    packed, y * widthBytes, data.Scan0 + y * data.Stride, widthBytes);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private void LoadImage(object? _)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dlg.ShowDialog() == true)
        {
            ImagePath = dlg.FileName;
            _imageBytes = File.ReadAllBytes(dlg.FileName);
            IsImageFormat = true;
            _ = UpdatePreviewAsync();
        }
    }

    // ================= ВСПОМОГАТЕЛЬНОЕ =================
    private void Notify(string text, bool ok = true)
    {
        Notification = (ok ? "✓ " : "⚠ ") + text;
    }

    private void AppendLog(string line)
    {
        Log = $"{DateTime.Now:HH:mm:ss}  {line}\n{Log}";
        if (Log.Length > 6000) Log = Log[..6000];
        // Панель лога — в памяти и обрезается по размеру, поэтому диагностика
        // (сеть, подключение принтера) не переживает перезапуск. Дублируем в файл.
        FileLog.Info(line);
    }
}
