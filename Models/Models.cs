namespace AutoPrint.Models;

/// <summary>Три поддерживаемых формата входных данных на печать.</summary>
public enum ReceiptFormat
{
    /// <summary>JSON — векторный шаблон, собирается во внутренний макет конструктора.</summary>
    Json,
    /// <summary>HTML — сырая веб-вёрстка, рендерится в изображение.</summary>
    Html,
    /// <summary>PNG/JPEG — растровое изображение, dithering в 1 бит.</summary>
    Image,
    /// <summary>PDF — рендерится в растр постранично и печатается 1-битным.</summary>
    Pdf
}

/// <summary>Внутренний макет чека (результат разбора JSON-шаблона).</summary>
public class ReceiptModel
{
    public string Header { get; set; } = "";
    public string SubHeader { get; set; } = "";
    public List<ReceiptItem> Items { get; set; } = new();
    public decimal Total { get; set; }
    public string Barcode { get; set; } = "";
    public string Footer { get; set; } = "";
}

public class ReceiptItem
{
    public string Name { get; set; } = "";
    public int Qty { get; set; } = 1;
    public decimal Price { get; set; }
    public decimal Sum => Qty * Price;
}

/// <summary>Категория чека — для истории, повторной печати и выбора макета.</summary>
public enum ReceiptKind
{
    Normal,       // операционный чек (покупка/продажа/возврат)
    ShiftOpen,    // открытие смены
    ShiftClose,   // закрытие смены (Z-отчёт)
    Deposit,      // внесение / пополнение наличных
    Withdrawal,   // изъятие / снятие наличных
    Report        // прочие отчёты (X-отчёт и т.п.)
}

/// <summary>
/// Тип фискального документа ОФД — определяет тело чека и заголовок операции.
/// «Операционные» чеки (продажа/покупка/возврат) отличаются от служебных
/// (открытие/закрытие смены) и кассовых движений (внесение/изъятие).
/// </summary>
public enum FiscalDocType
{
    Sale,         // САТУ / ПРОДАЖА
    Purchase,     // САТЫП АЛУ / ПОКУПКА
    RefundSale,   // САТУДЫ ҚАЙТАРУ / ВОЗВРАТ ПРОДАЖИ
    RefundPurchase, // САТЫП АЛУДЫ ҚАЙТАРУ / ВОЗВРАТ ПОКУПКИ
    ShiftOpen,    // СМЕНАНЫ АШУ / ОТКРЫТИЕ СМЕНЫ
    ShiftClose,   // СМЕНАНЫ ЖАБУ / ЗАКРЫТИЕ СМЕНЫ (Z-отчёт)
    Deposit,      // АҚША ЕНГІЗУ / ВНЕСЕНИЕ (ПОПОЛНЕНИЕ)
    Withdrawal    // АҚША АЛУ / ИЗЪЯТИЕ (СНЯТИЕ)
}

/// <summary>Позиция операционного фискального чека.</summary>
public class FiscalItem
{
    public int? No { get; set; }                 // порядковый номер (1., 2., …)
    public string Name { get; set; } = "";       // наименование (напр. "USD")
    public decimal Qty { get; set; } = 1;
    public decimal Price { get; set; }
    public string Unit { get; set; } = "";       // ед. изм. (шт., кг), опц.
    private decimal? _sum;
    public decimal Sum { get => _sum ?? Qty * Price; set => _sum = value; }
}

/// <summary>Способ оплаты (строка + сумма) в операционном чеке.</summary>
public class FiscalPayment
{
    public string Method { get; set; } = "Қолма-қол ақша / Наличные";
    public decimal Amount { get; set; }
}

/// <summary>
/// Фискальный чек ОФД (Казахстан). Единая модель для всех типов документа;
/// какие поля используются — зависит от <see cref="DocType"/>. Верстается
/// в растр средствами <see cref="Services.FiscalReceiptRenderer"/>.
/// </summary>
public class FiscalReceipt
{
    // Тип определяется парсером из строкового поля запроса, а не из числа enum.
    [System.Text.Json.Serialization.JsonIgnore]
    public FiscalDocType DocType { get; set; } = FiscalDocType.Sale;

    // ---- Шапка организации ----
    public string OrgName { get; set; } = "";     // Товарищество с ограниченной ответственностью "ECASH"
    public string Address { get; set; } = "";     // г. Алматы, ...
    public string Bin { get; set; } = "";         // ЖСН/БСН (ИИН/БИН)
    public string Znm { get; set; } = "";         // МЗН / ЗНМ — заводской номер модуля
    public string Rnm { get; set; } = "";         // МТН / РНМ — регистрационный номер
    public string ClientBin { get; set; } = "";   // ИИН/БИН клиента

    // ---- Фискальный блок ----
    public string Title { get; set; } = "«Фискалдық чек / Фискальный чек»";
    public string Date { get; set; } = "";        // Күні / Дата
    public string Cashier { get; set; } = "";     // Кассир
    public string? OperationTitle { get; set; }   // переопределение заголовка операции

    // ---- Тело: операционный чек ----
    public List<FiscalItem> Items { get; set; } = new();
    public List<FiscalPayment> Payments { get; set; } = new();
    public decimal Total { get; set; }

    // ---- Тело: кассовые движения / смены ----
    public string ShiftNumber { get; set; } = "";  // № смены
    public decimal? Amount { get; set; }           // сумма внесения/изъятия
    public decimal? CashInDrawer { get; set; }     // наличные в кассе (итог)
    public List<FiscalCounter> Counters { get; set; } = new();  // счётчики Z-отчёта

    // ---- Футер ОФД ----
    public string Ofd { get; set; } = "ОФД - АО \"КАЗАХТЕЛЕКОМ\"";
    public string KkmCode { get; set; } = "";      // Код ККМ КГД
    public string CheckUrl { get; set; } = "https://consumer.oofd.kz";

    /// <summary>Псевдоним поля <see cref="CheckUrl"/> — принимает ссылку ОФД из JSON под именем "ofd_url".
    /// Только для десериализации (в сериализованный JSON не попадает).</summary>
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("ofd_url")]
    internal string OfdUrl { set { if (!string.IsNullOrWhiteSpace(value)) CheckUrl = value; } }
    public string FiscalSign { get; set; } = "";   // ФП / фискальный признак
    public int? CheckNumber { get; set; }          // № чека

    [System.Text.Json.Serialization.JsonIgnore]
    public ReceiptKind Kind => DocType switch
    {
        FiscalDocType.ShiftOpen  => ReceiptKind.ShiftOpen,
        FiscalDocType.ShiftClose => ReceiptKind.ShiftClose,
        FiscalDocType.Deposit    => ReceiptKind.Deposit,
        FiscalDocType.Withdrawal => ReceiptKind.Withdrawal,
        _                        => ReceiptKind.Normal
    };
}

/// <summary>Строка счётчика для Z-отчёта (закрытие смены).</summary>
public class FiscalCounter
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Единица данных, пришедшая на печать (из сети или эмулятора).</summary>
public class PrintJob
{
    public ReceiptFormat Format { get; set; }
    public string Payload { get; set; } = "";          // JSON или HTML-строка
    public byte[]? RawImage { get; set; }              // для формата Image/Pdf
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "network";
    public ReceiptKind Kind { get; set; } = ReceiptKind.Normal;
    /// <summary>Имя принтера из запроса ("printer"). Пусто — печать на выбранную в приложении цель.</summary>
    public string PrinterName { get; set; } = "";
    /// <summary>Организация из чека ("orgName"). Пусто — в истории показываем ECASH по умолчанию.</summary>
    public string OrgName { get; set; } = "";
}

/// <summary>Способ подключения принтера.</summary>
public enum ConnectionKind
{
    None,
    /// <summary>USB / локальный порт — печать через спулер Windows.</summary>
    WindowsSpooler,
    /// <summary>Ethernet (RJ45) — прямая печать по TCP-сокету на порт 9100.</summary>
    Network
}

/// <summary>Активная цель печати: либо имя принтера в спулере, либо сетевой endpoint.</summary>
public class PrintTarget
{
    public ConnectionKind Kind { get; set; } = ConnectionKind.None;
    public string PrinterName { get; set; } = "";   // для WindowsSpooler
    public string Host { get; set; } = "";          // для Network
    public int Port { get; set; } = 9100;           // RAW/JetDirect

    public bool IsReady => Kind != ConnectionKind.None;

    public string Describe() => Kind switch
    {
        ConnectionKind.WindowsSpooler => $"USB/спулер: {PrinterName}",
        ConnectionKind.Network        => $"Сеть: {Host}:{Port}",
        _                             => "не подключён"
    };
}

/// <summary>Как формируется поток печати.</summary>
public enum PrintRenderMode
{
    /// <summary>ESC/POS текст — вёрстку делает сам принтер (лёгкие данные, шрифты принтера).</summary>
    EscPosText,
    /// <summary>Графика — приложение рендерит чек в 1-битный растр, принтер печатает готовые пиксели.</summary>
    Graphics
}

/// <summary>Настройки геометрии ленты и шрифтов.</summary>
public class ReceiptGeometry
{
    public double TapeWidthMm { get; set; } = 80;      // 58..80 мм
    public double SideMarginsPx { get; set; } = 12;    // 0..40 px
    /// <summary>Шрифт шапки/фискального блока (pt), 5..18.</summary>
    public double HeaderFontPt { get; set; } = 11;
    /// <summary>Шрифт тела/футера (pt), 5..18.</summary>
    public double BodyFontPt { get; set; } = 11;
    /// <summary>Совместимость: старый единый шрифт = шрифт тела.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double FontSizePt { get => BodyFontPt; set => BodyFontPt = value; }
    public PrintRenderMode RenderMode { get; set; } = PrintRenderMode.Graphics;

    /// <summary>Печатать QR-код ссылки проверки чека (checkUrl) внизу по центру.</summary>
    public bool QrEnabled { get; set; } = true;
    /// <summary>Размер QR-кода в миллиметрах (сторона), 10..40 мм.</summary>
    public double QrSizeMm { get; set; } = 22;

    // ---- Настройки принтера ----
    /// <summary>Пустых строк сверху чека (вертикальный отступ до содержимого), 0..8.</summary>
    public int TopMarginLines { get; set; } = 0;
    /// <summary>Пустых строк снизу перед отрезом (протяжка до ножа), 0..8.</summary>
    public int BottomMarginLines { get; set; } = 4;
    /// <summary>Автоматический отрез ленты после печати чека.</summary>
    public bool AutoCut { get; set; } = true;
    /// <summary>Полный отрез (true) вместо частичного (false).</summary>
    public bool FullCut { get; set; } = false;
    /// <summary>Открывать денежный ящик импульсом после печати чека.</summary>
    public bool OpenDrawer { get; set; } = false;

    public ReceiptGeometry Clone() => (ReceiptGeometry)MemberwiseClone();
}
