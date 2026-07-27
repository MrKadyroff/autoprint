namespace AutoPrint.Models;

/// <summary>Тип строки в шаблоне чека.</summary>
public enum LineKind
{
    Text,      // одна строка текста (Content), может содержать {{поле}}
    TwoCol,    // слева Left, справа Right (сумма); каждый — с {{поле}}
    Spacer,    // пустой вертикальный отступ
    Items,     // список позиций чека (name + qty×price / sum)
    Payments   // список способов оплаты (method / amount)
}

public enum LineAlign { Left, Center, Right }

/// <summary>Из какой группы брать шрифт (шапка или тело) — раздельное управление размерами.</summary>
public enum FontGroup { Header, Body }

/// <summary>Одна строка шаблона чека — редактируется в конструкторе, перетаскивается по порядку.</summary>
public class TemplateLine
{
    public LineKind Kind { get; set; } = LineKind.Text;
    public LineAlign Align { get; set; } = LineAlign.Left;
    public FontGroup Font { get; set; } = FontGroup.Body;
    public bool Bold { get; set; }

    /// <summary>Text: строка (статичный текст + {{поле}}). Пример: "Кассир: {{cashier}}".</summary>
    public string Content { get; set; } = "";

    /// <summary>TwoCol: левая часть (метка).</summary>
    public string Left { get; set; } = "";
    /// <summary>TwoCol: правая часть (значение/сумма).</summary>
    public string Right { get; set; } = "";

    /// <summary>Items: шаблон строки позиции. Доступно {{no}},{{name}},{{qty}},{{price}},{{sum}},{{unit}}.</summary>
    public string ItemLine1 { get; set; } = "{{no}}.    {{name}}";
    public string ItemLine2Left { get; set; } = "    {{qty}} x {{price}}";
    public string ItemLine2Right { get; set; } = "{{sum}}";

    public TemplateLine Clone() => (TemplateLine)MemberwiseClone();
}

/// <summary>Шаблон чека одного типа документа — упорядоченный список строк.</summary>
public class ReceiptTemplate
{
    public string DocType { get; set; } = "sale";
    public List<TemplateLine> Lines { get; set; } = new();

    public ReceiptTemplate Clone() => new()
    {
        DocType = DocType,
        Lines = Lines.Select(l => l.Clone()).ToList()
    };

    // ======== Дефолтные шаблоны (повторяют текущий вид фискального чека ОФД) ========

    /// <summary>Шаблон по умолчанию для указанного типа документа.</summary>
    public static ReceiptTemplate Default(FiscalDocType type)
    {
        var lines = new List<TemplateLine>();
        void T(string content, LineAlign a = LineAlign.Left, FontGroup f = FontGroup.Body, bool bold = false)
            => lines.Add(new TemplateLine { Kind = LineKind.Text, Content = content, Align = a, Font = f, Bold = bold });
        void Two(string l, string r, bool bold = false)
            => lines.Add(new TemplateLine { Kind = LineKind.TwoCol, Left = l, Right = r, Bold = bold });
        void Gap() => lines.Add(new TemplateLine { Kind = LineKind.Spacer });

        // --- Шапка организации (шрифт шапки, по центру) ---
        T("{{orgName}}", LineAlign.Center, FontGroup.Header);
        T("{{address}}", LineAlign.Center, FontGroup.Header);
        T("ЖСН/БСН (ИИН/БИН): {{bin}}", LineAlign.Center, FontGroup.Header);
        T("МЗН / ЗНМ: {{znm}}", LineAlign.Center, FontGroup.Header);
        T("МТН / РНМ: {{rnm}}", LineAlign.Center, FontGroup.Header);
        T("ИИН/БИН клиента: {{clientBin}}", LineAlign.Center, FontGroup.Header);
        Gap();
        // --- Фискальный блок ---
        T("{{title}}", LineAlign.Center, FontGroup.Header, bold: true);
        T("Күні / Дата: {{date}}", LineAlign.Center, FontGroup.Header);
        T("Кассир: {{cashier}}", LineAlign.Center, FontGroup.Header);
        Gap();
        // --- Заголовок операции ---
        T("{{operationTitle}}", LineAlign.Left, FontGroup.Body, bold: true);
        Gap();

        // --- Тело: зависит от типа ---
        switch (type)
        {
            case FiscalDocType.ShiftOpen:
                T("Смена / Ауысым: №{{shiftNumber}}");
                Two("Кассадағы ақша / Наличные в кассе:", "{{cashInDrawer}}");
                break;
            case FiscalDocType.ShiftClose:
                T("Смена / Ауысым: №{{shiftNumber}}");
                lines.Add(new TemplateLine { Kind = LineKind.Payments }); // счётчики отрисуются как «метка/значение»
                Two("Кассадағы ақша / Наличные в кассе:", "{{cashInDrawer}}", bold: true);
                break;
            case FiscalDocType.Deposit:
            case FiscalDocType.Withdrawal:
                Two("Сомасы / Сумма:", "{{amount}}", bold: true);
                Two("Кассадағы ақша / Наличные в кассе:", "{{cashInDrawer}}");
                break;
            default: // операционные
                lines.Add(new TemplateLine { Kind = LineKind.Items });
                lines.Add(new TemplateLine { Kind = LineKind.Payments });
                Two("ЖИЫНЫ / ИТОГО:", "{{total}}", bold: true);
                break;
        }

        Gap();
        // --- Футер ОФД ---
        T("{{ofd}}");
        T("Код ККМ КГД: {{kkmCode}}");
        T("Для проверки чека зайдите на сайт:");
        T("{{checkUrl}}");

        return new ReceiptTemplate { DocType = KeyOf(type), Lines = lines };
    }

    /// <summary>Строковый ключ типа документа (для файла шаблона).</summary>
    public static string KeyOf(FiscalDocType t) => t switch
    {
        FiscalDocType.ShiftOpen  => "shift_open",
        FiscalDocType.ShiftClose => "shift_close",
        FiscalDocType.Deposit    => "deposit",
        FiscalDocType.Withdrawal => "withdrawal",
        FiscalDocType.Purchase   => "purchase",
        FiscalDocType.RefundSale => "refund_sale",
        FiscalDocType.RefundPurchase => "refund_purchase",
        _                        => "sale"
    };
}
