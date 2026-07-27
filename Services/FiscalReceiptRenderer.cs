using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>
/// Разбор фискального JSON-чека ОФД (Казахстан) и его вёрстка в растр средствами GDI+.
/// Макет повторяет печатную форму ОФД (Казахтелеком): шапка организации → фискальный
/// блок → заголовок операции → тело (зависит от типа документа) → футер ОФД.
///
/// Тело под каждый тип <see cref="FiscalDocType"/> своё:
///  • операционные (продажа/покупка/возврат) — позиции + оплата + ИТОГО;
///  • открытие/закрытие смены — № смены, счётчики, наличные в кассе;
///  • внесение/изъятие — сумма движения и остаток наличных в кассе.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FiscalReceiptRenderer
{
    private static readonly CultureInfo Money = MakeMoneyCulture();
    private const int MaxHeight = 12000;

    // ================= РАЗБОР JSON =================

    /// <summary>Похоже ли тело на фискальный чек ОФД (а не на старый простой шаблон).</summary>
    public static bool IsFiscalJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{")) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var k in new[] { "docType", "type", "operation", "orgName", "rnm", "znm", "bin", "fiscal" })
                if (root.TryGetProperty(k, out _)) return true;
        }
        catch { /* не JSON */ }
        return false;
    }

    /// <summary>Разбирает JSON во внутреннюю модель фискального чека.</summary>
    public static FiscalReceipt Parse(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var r = JsonSerializer.Deserialize<FiscalReceipt>(json, opts)
                ?? throw new FormatException("Пустой или некорректный JSON чека.");

        // Тип документа определяем из строкового поля (docType/type/operation/kind).
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string raw = "";
        foreach (var k in new[] { "docType", "type", "operation", "kind" })
            if (root.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String)
            { raw = e.GetString() ?? ""; break; }
        r.DocType = MapDocType(raw);

        // Если оплаты не заданы явно, а итог есть — считаем «Наличные» по умолчанию.
        if (r.Payments.Count == 0 && r.Total > 0 && IsOperational(r.DocType))
            r.Payments.Add(new FiscalPayment { Amount = r.Total });

        return r;
    }

    private static FiscalDocType MapDocType(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return FiscalDocType.Sale;

        if (s.Contains("open") || s.Contains("откр") || s.Contains("ашу")) return FiscalDocType.ShiftOpen;
        if (s.Contains("close") || s.Contains("закр") || s.Contains("жабу") || s == "z") return FiscalDocType.ShiftClose;
        if (s.Contains("deposit") || s.Contains("внес") || s.Contains("попол") || s.Contains("енгізу") || s.Contains("engizu"))
            return FiscalDocType.Deposit;
        if (s.Contains("withdraw") || s.Contains("изъят") || s.Contains("сня") || s.Contains("алу"))
            return FiscalDocType.Withdrawal;
        if (s.Contains("refund") || s.Contains("возвр") || s.Contains("қайтару") || s.Contains("kaitaru"))
            return s.Contains("purchas") || s.Contains("покуп") ? FiscalDocType.RefundPurchase : FiscalDocType.RefundSale;
        if (s.Contains("purchas") || s.Contains("покуп") || s.Contains("сатып"))
            return FiscalDocType.Purchase;
        return FiscalDocType.Sale;
    }

    private static bool IsOperational(FiscalDocType t) => t is FiscalDocType.Sale or FiscalDocType.Purchase
        or FiscalDocType.RefundSale or FiscalDocType.RefundPurchase;

    /// <summary>Двуязычный заголовок операции по типу документа.</summary>
    public static string OperationTitle(FiscalDocType t) => t switch
    {
        FiscalDocType.Sale           => "САТУ / ПРОДАЖА",
        FiscalDocType.Purchase       => "САТЫП АЛУ / ПОКУПКА",
        FiscalDocType.RefundSale     => "САТУДЫ ҚАЙТАРУ / ВОЗВРАТ ПРОДАЖИ",
        FiscalDocType.RefundPurchase => "САТЫП АЛУДЫ ҚАЙТАРУ / ВОЗВРАТ ПОКУПКИ",
        FiscalDocType.ShiftOpen      => "СМЕНАНЫ АШУ / ОТКРЫТИЕ СМЕНЫ",
        FiscalDocType.ShiftClose     => "СМЕНАНЫ ЖАБУ / ЗАКРЫТИЕ СМЕНЫ (Z-ОТЧЁТ)",
        FiscalDocType.Deposit        => "АҚША ЕНГІЗУ / ВНЕСЕНИЕ НАЛИЧНЫХ",
        FiscalDocType.Withdrawal     => "АҚША АЛУ / ИЗЪЯТИЕ НАЛИЧНЫХ",
        _                            => "ПРОДАЖА"
    };

    // ================= РЕНДЕР =================

    public static Bitmap Render(FiscalReceipt r, ReceiptGeometry geo)
    {
        int width = PrintPipeline.DotsFor(geo.TapeWidthMm);
        width -= width % 8;
        float margin = (float)(geo.SideMarginsPx * 203.0 / 96.0);
        float headPx = (float)(geo.HeaderFontPt / 72.0 * 203.0);
        float bodyPx = (float)(geo.BodyFontPt / 72.0 * 203.0);

        // Шапка/фискальный блок — своим шрифтом; тело/футер — своим.
        var fHeadReg  = new Font("Segoe UI", headPx,         FontStyle.Regular, GraphicsUnit.Pixel);
        var fHeadBold = new Font("Segoe UI", headPx,         FontStyle.Bold,    GraphicsUnit.Pixel);
        var fReg      = new Font("Segoe UI", bodyPx,         FontStyle.Regular, GraphicsUnit.Pixel);
        var fBold     = new Font("Segoe UI", bodyPx,         FontStyle.Bold,    GraphicsUnit.Pixel);
        var fTotal    = new Font("Segoe UI", bodyPx * 1.08f, FontStyle.Bold,    GraphicsUnit.Pixel);

        void Draw(Graphics g, ref float y)
        {
            var head = new Ctx(g, width, margin, headPx, fHeadReg, fHeadBold, fHeadBold);
            var ctx  = new Ctx(g, width, margin, bodyPx, fReg, fBold, fTotal);
            DrawHeader(head, r, ref y);
            Gap(ref y, headPx * 0.7f);
            DrawFiscalBlock(head, r, ref y);
            Gap(ref y, bodyPx * 0.9f);
            DrawOperationTitle(ctx, r, ref y);
            Gap(ref y, bodyPx * 0.5f);
            DrawBody(ctx, r, ref y);
            Gap(ref y, bodyPx * 0.9f);
            DrawFooter(ctx, r, ref y);
            Gap(ref y, bodyPx * 0.6f);
        }

        try { return RenderWithMeasure(width, Draw); }
        finally { fHeadReg.Dispose(); fHeadBold.Dispose(); fReg.Dispose(); fBold.Dispose(); fTotal.Dispose(); }
    }

    // ================= РЕНДЕР ПО ШАБЛОНУ (конструктор) =================

    /// <summary>Рисует чек по редактируемому шаблону, подставляя значения из данных.</summary>
    public static Bitmap Render(FiscalReceipt r, ReceiptTemplate tmpl, ReceiptGeometry geo)
    {
        int width = PrintPipeline.DotsFor(geo.TapeWidthMm);
        width -= width % 8;
        float margin = (float)(geo.SideMarginsPx * 203.0 / 96.0);
        float headPx = (float)(geo.HeaderFontPt / 72.0 * 203.0);
        float bodyPx = (float)(geo.BodyFontPt / 72.0 * 203.0);

        var fHeadReg  = new Font("Segoe UI", headPx,         FontStyle.Regular, GraphicsUnit.Pixel);
        var fHeadBold = new Font("Segoe UI", headPx,         FontStyle.Bold,    GraphicsUnit.Pixel);
        var fReg      = new Font("Segoe UI", bodyPx,         FontStyle.Regular, GraphicsUnit.Pixel);
        var fBold     = new Font("Segoe UI", bodyPx,         FontStyle.Bold,    GraphicsUnit.Pixel);

        var values = BuildValues(r);

        // QR ссылки проверки чека (checkUrl) — рисуется внизу по центру, размер задаётся в настройках.
        Bitmap? qr = null;
        if (geo.QrEnabled && Has(r.CheckUrl))
        {
            try { qr = QrRenderer.Render(r.CheckUrl, Math.Min(width, QrRenderer.MmToDots(geo.QrSizeMm))); }
            catch { qr = null; }
        }

        void Draw(Graphics g, ref float y)
        {
            var head = new Ctx(g, width, margin, headPx, fHeadReg, fHeadBold, fHeadBold);
            var body = new Ctx(g, width, margin, bodyPx, fReg, fBold, fBold);

            foreach (var line in tmpl.Lines)
            {
                var c = line.Font == FontGroup.Header ? head : body;
                Font f = line.Bold ? c.Bold : c.Reg;

                switch (line.Kind)
                {
                    case LineKind.Spacer:
                        Gap(ref y, c.Base * 0.6f);
                        break;

                    case LineKind.Text:
                    {
                        var (text, had, filled) = Resolve(line.Content, values);
                        if (had && !filled) break;               // все параметры пустые — строку прячем
                        if (!Has(text)) break;
                        DrawAligned(c, text, f, line.Align, ref y);
                        break;
                    }

                    case LineKind.TwoCol:
                    {
                        var (left, _, _) = Resolve(line.Left, values);
                        var (right, hadR, filledR) = Resolve(line.Right, values);
                        if (hadR && !filledR) break;
                        TwoCol(c, left, right, f, ref y, rightBold: line.Bold);
                        break;
                    }

                    case LineKind.Items:
                        DrawItems(c, r, line, ref y);
                        break;

                    case LineKind.Payments:
                        DrawPaymentsAndCounters(c, r, ref y);
                        break;
                }
            }

            // QR внизу по центру.
            if (qr is not null)
            {
                Gap(ref y, bodyPx * 0.8f);
                float x = Math.Max(0, (width - qr.Width) / 2f);
                var savedInterp = g.InterpolationMode;
                var savedOffset = g.PixelOffsetMode;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImageUnscaled(qr, (int)x, (int)Math.Ceiling(y));
                g.InterpolationMode = savedInterp;
                g.PixelOffsetMode = savedOffset;
                y += qr.Height;
            }
        }

        try { return RenderWithMeasure(width, Draw); }
        finally { fHeadReg.Dispose(); fHeadBold.Dispose(); fReg.Dispose(); fBold.Dispose(); qr?.Dispose(); }
    }

    private static void DrawAligned(Ctx c, string text, Font f, LineAlign a, ref float y)
    {
        switch (a)
        {
            case LineAlign.Center: CenterWrap(c, text, f, ref y); break;
            case LineAlign.Right:  RightWrap(c, text, f, ref y); break;
            default:               Left(c, text, f, ref y); break;
        }
    }

    private static void DrawItems(Ctx c, FiscalReceipt r, TemplateLine line, ref float y)
    {
        int i = 1;
        foreach (var it in r.Items)
        {
            var v = ItemValues(it, i);
            string l1 = Resolve(line.ItemLine1, v).text;
            string l2l = Resolve(line.ItemLine2Left, v).text;
            string l2r = Resolve(line.ItemLine2Right, v).text;
            if (Has(l1)) Left(c, l1, c.Reg, ref y);
            if (Has(l2l) || Has(l2r)) TwoCol(c, l2l, l2r, c.Reg, ref y);
            i++;
        }
    }

    private static void DrawPaymentsAndCounters(Ctx c, FiscalReceipt r, ref float y)
    {
        foreach (var ct in r.Counters) TwoCol(c, ct.Label, ct.Value, c.Reg, ref y);
        foreach (var p in r.Payments) TwoCol(c, "    " + p.Method, Num(p.Amount == 0 ? r.Total : p.Amount), c.Reg, ref y);
    }

    // ---------- Значения для подстановки ----------

    private static Dictionary<string, string> BuildValues(FiscalReceipt r)
    {
        string title = Has(r.OperationTitle) ? r.OperationTitle! : OperationTitle(r.DocType);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["orgName"] = r.OrgName, ["address"] = r.Address, ["bin"] = r.Bin,
            ["znm"] = r.Znm, ["rnm"] = r.Rnm, ["clientBin"] = r.ClientBin,
            ["title"] = r.Title, ["date"] = r.Date, ["cashier"] = r.Cashier,
            ["operationTitle"] = title,
            ["total"] = Num(r.Total),
            ["amount"] = r.Amount is decimal a ? Num(a) : "",
            ["cashInDrawer"] = r.CashInDrawer is decimal cd ? Num(cd) : "",
            ["shiftNumber"] = r.ShiftNumber,
            ["ofd"] = r.Ofd, ["kkmCode"] = r.KkmCode, ["checkUrl"] = r.CheckUrl,
            ["fiscalSign"] = r.FiscalSign,
            ["checkNumber"] = r.CheckNumber?.ToString() ?? ""
        };
    }

    private static Dictionary<string, string> ItemValues(FiscalItem it, int idx) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["no"] = (it.No ?? idx).ToString(),
            ["name"] = it.Name,
            ["qty"] = Num(it.Qty),
            ["price"] = Num(it.Price),
            ["sum"] = Num(it.Sum),
            ["unit"] = it.Unit
        };

    /// <summary>Подстановка {{ключ}}. Возвращает текст, был ли плейсхолдер и заполнен ли хоть один.</summary>
    private static (string text, bool hadPlaceholder, bool anyFilled) Resolve(
        string tmpl, Dictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(tmpl)) return ("", false, false);
        bool had = false, filled = false;
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < tmpl.Length)
        {
            int open = tmpl.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(tmpl, i, tmpl.Length - i); break; }
            sb.Append(tmpl, i, open - i);
            int close = tmpl.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(tmpl, open, tmpl.Length - open); break; }
            string key = tmpl.Substring(open + 2, close - open - 2).Trim();
            had = true;
            if (values.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
            { sb.Append(val); filled = true; }
            i = close + 2;
        }
        return (sb.ToString().Trim(), had, filled);
    }

    // ---------- Секции (дефолтный рендер без шаблона — оставлен для совместимости) ----------

    private static void DrawHeader(Ctx c, FiscalReceipt r, ref float y)
    {
        CenterWrap(c, r.OrgName, c.Reg, ref y);
        CenterWrap(c, r.Address, c.Reg, ref y);
        if (Has(r.Bin))       CenterWrap(c, $"ЖСН/БСН (ИИН/БИН): {r.Bin}", c.Reg, ref y);
        if (Has(r.Znm))       CenterWrap(c, $"МЗН / ЗНМ: {r.Znm}", c.Reg, ref y);
        if (Has(r.Rnm))       CenterWrap(c, $"МТН / РНМ: {r.Rnm}", c.Reg, ref y);
        if (Has(r.ClientBin)) CenterWrap(c, $"ИИН/БИН клиента: {r.ClientBin}", c.Reg, ref y);
    }

    private static void DrawFiscalBlock(Ctx c, FiscalReceipt r, ref float y)
    {
        CenterWrap(c, r.Title, c.Bold, ref y);
        if (Has(r.Date))      CenterWrap(c, $"Күні / Дата: {r.Date}", c.Reg, ref y);
        if (Has(r.ClientBin)) CenterWrap(c, $"ИИН/БИН клиента: {r.ClientBin}", c.Reg, ref y);
        if (Has(r.Cashier))   CenterWrap(c, $"Кассир: {r.Cashier}", c.Reg, ref y);
        if (r.CheckNumber is int n) CenterWrap(c, $"Чек / Смена: №{n}", c.Reg, ref y);
    }

    private static void DrawOperationTitle(Ctx c, FiscalReceipt r, ref float y)
    {
        string title = Has(r.OperationTitle) ? r.OperationTitle! : OperationTitle(r.DocType);
        Left(c, title, c.Bold, ref y);
    }

    private static void DrawBody(Ctx c, FiscalReceipt r, ref float y)
    {
        switch (r.DocType)
        {
            case FiscalDocType.ShiftOpen:  DrawShiftOpen(c, r, ref y);  break;
            case FiscalDocType.ShiftClose: DrawShiftClose(c, r, ref y); break;
            case FiscalDocType.Deposit:
            case FiscalDocType.Withdrawal: DrawCashMovement(c, r, ref y); break;
            default:                       DrawOperational(c, r, ref y); break;
        }
    }

    /// <summary>Операционный чек: нумерованные позиции, способы оплаты, ИТОГО.</summary>
    private static void DrawOperational(Ctx c, FiscalReceipt r, ref float y)
    {
        int i = 1;
        foreach (var it in r.Items)
        {
            string no = (it.No ?? i).ToString();
            Left(c, $"{no}.    {it.Name}", c.Reg, ref y);
            string qtyPart = Has(it.Unit)
                ? $"    {Num(it.Qty)} {it.Unit} x {Num(it.Price)}"
                : $"    {Num(it.Qty)} x {Num(it.Price)}";
            TwoCol(c, qtyPart, Num(it.Sum), c.Reg, ref y);
            i++;
        }

        Gap(ref y, c.Base * 0.2f);
        foreach (var p in r.Payments)
            TwoCol(c, "    " + p.Method, Num(p.Amount == 0 ? r.Total : p.Amount), c.Reg, ref y);

        Gap(ref y, c.Base * 0.15f);
        TwoCol(c, "ЖИЫНЫ / ИТОГО:", Num(r.Total), c.Total, ref y, rightBold: true);
    }

    private static void DrawShiftOpen(Ctx c, FiscalReceipt r, ref float y)
    {
        if (Has(r.ShiftNumber)) Left(c, $"Смена / Ауысым: №{r.ShiftNumber}", c.Reg, ref y);
        if (r.CashInDrawer is decimal cash)
            TwoCol(c, "Кассадағы ақша / Наличные в кассе:", Num(cash), c.Reg, ref y);
        foreach (var ct in r.Counters) TwoCol(c, ct.Label, ct.Value, c.Reg, ref y);
    }

    private static void DrawShiftClose(Ctx c, FiscalReceipt r, ref float y)
    {
        if (Has(r.ShiftNumber)) Left(c, $"Смена / Ауысым: №{r.ShiftNumber}", c.Reg, ref y);
        Gap(ref y, c.Base * 0.15f);
        foreach (var ct in r.Counters) TwoCol(c, ct.Label, ct.Value, c.Reg, ref y);
        if (r.CashInDrawer is decimal cash)
        {
            Gap(ref y, c.Base * 0.15f);
            TwoCol(c, "Кассадағы ақша / Наличные в кассе:", Num(cash), c.Total, ref y, rightBold: true);
        }
    }

    private static void DrawCashMovement(Ctx c, FiscalReceipt r, ref float y)
    {
        decimal amount = r.Amount ?? r.Total;
        TwoCol(c, "Сомасы / Сумма:", Num(amount), c.Total, ref y, rightBold: true);
        if (r.CashInDrawer is decimal cash)
            TwoCol(c, "Кассадағы ақша / Наличные в кассе:", Num(cash), c.Reg, ref y);
    }

    private static void DrawFooter(Ctx c, FiscalReceipt r, ref float y)
    {
        if (Has(r.Ofd))        Left(c, r.Ofd, c.Reg, ref y);
        if (Has(r.KkmCode))    Left(c, $"Код ККМ КГД: {r.KkmCode}", c.Reg, ref y);
        if (Has(r.FiscalSign)) Left(c, $"ФП / Фискальный признак: {r.FiscalSign}", c.Reg, ref y);
        if (Has(r.CheckUrl))
        {
            Left(c, "Для проверки чека зайдите на сайт:", c.Reg, ref y);
            Left(c, r.CheckUrl, c.Reg, ref y);
        }
    }

    // ================= Примитивы отрисовки =================

    private sealed class Ctx
    {
        public readonly Graphics G;
        public readonly int Width;
        public readonly float Margin, Base;
        public readonly Font Reg, Bold, Total;
        public Ctx(Graphics g, int w, float m, float b, Font reg, Font bold, Font total)
        { G = g; Width = w; Margin = m; Base = b; Reg = reg; Bold = bold; Total = total; }
        public float Inner => Width - 2 * Margin;
    }

    private static readonly StringFormat Typo = new(StringFormat.GenericTypographic) { FormatFlags = 0 };

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);
    private static void Gap(ref float y, float px) => y += px;

    private static void Left(Ctx c, string text, Font f, ref float y)
    {
        foreach (var line in Wrap(c.G, text, f, c.Inner))
        {
            c.G.DrawString(line, f, Brushes.Black, c.Margin, y, Typo);
            y += Lh(c.G, f);
        }
    }

    private static void CenterWrap(Ctx c, string text, Font f, ref float y)
    {
        if (!Has(text)) return;
        foreach (var line in Wrap(c.G, text, f, c.Inner))
        {
            float w = c.G.MeasureString(line, f, int.MaxValue, Typo).Width;
            float x = Math.Max(c.Margin, (c.Width - w) / 2f);
            c.G.DrawString(line, f, Brushes.Black, x, y, Typo);
            y += Lh(c.G, f);
        }
    }

    private static void RightWrap(Ctx c, string text, Font f, ref float y)
    {
        if (!Has(text)) return;
        foreach (var line in Wrap(c.G, text, f, c.Inner))
        {
            float w = c.G.MeasureString(line, f, int.MaxValue, Typo).Width;
            float x = Math.Max(c.Margin, c.Width - c.Margin - w);
            c.G.DrawString(line, f, Brushes.Black, x, y, Typo);
            y += Lh(c.G, f);
        }
    }

    /// <summary>Строка «слева текст, справа сумма». Левая часть переносится, сумма всегда справа последней строки.</summary>
    private static void TwoCol(Ctx c, string left, string right, Font f, ref float y, bool rightBold = false)
    {
        Font rf = rightBold ? c.Total : f;
        float rw = c.G.MeasureString(right, rf, int.MaxValue, Typo).Width;
        float leftMax = c.Inner - rw - c.Base * 0.5f;

        var lines = Wrap(c.G, left, f, Math.Max(leftMax, c.Base));
        float startY = y;
        for (int k = 0; k < lines.Count; k++)
        {
            c.G.DrawString(lines[k], f, Brushes.Black, c.Margin, y, Typo);
            if (k < lines.Count - 1) y += Lh(c.G, f);
        }
        c.G.DrawString(right, rf, Brushes.Black, c.Width - c.Margin - rw, y, Typo);
        y += Lh(c.G, rf);
        _ = startY;
    }

    // Двухпроходный рендер (измерить высоту → нарисовать).
    private delegate void DrawAction(Graphics g, ref float y);

    private static Bitmap RenderWithMeasure(int width, DrawAction draw)
    {
        float measured = 10f;
        using (var probe = new Bitmap(width, 8))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.PageUnit = GraphicsUnit.Pixel;
            pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            draw(pg, ref measured);
        }
        int height = Math.Clamp((int)Math.Ceiling(measured) + 10, 8, MaxHeight);

        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.PageUnit = GraphicsUnit.Pixel;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            float y = 10f;
            draw(g, ref y);
        }
        return bmp;
    }

    private static float Lh(Graphics g, Font f) => f.GetHeight(g) * 1.06f;

    /// <summary>Перенос текста по ширине (в пикселях), с учётом явных \n.</summary>
    private static List<string> Wrap(Graphics g, string text, Font f, float maxWidth)
    {
        var result = new List<string>();
        foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (para.Length == 0) { result.Add(""); continue; }
            var words = para.Split(' ');
            string cur = "";
            foreach (var word in words)
            {
                string candidate = cur.Length == 0 ? word : cur + " " + word;
                if (g.MeasureString(candidate, f, int.MaxValue, Typo).Width <= maxWidth || cur.Length == 0)
                    cur = candidate;
                else { result.Add(cur); cur = word; }
            }
            result.Add(cur);
        }
        return result;
    }

    private static string Num(decimal d)
    {
        // Целые — без дробной части (как на чеке: "502"); иначе 2 знака.
        return d == Math.Truncate(d)
            ? ((long)d).ToString("#,0", Money)
            : d.ToString("#,0.00", Money);
    }

    private static CultureInfo MakeMoneyCulture()
    {
        var ci = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        ci.NumberFormat.NumberGroupSeparator = " ";   // 1 234 567
        return ci;
    }
}
