using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.Versioning;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>
/// Рендер чека в растровое изображение средствами GDI+ прямо в приложении.
/// Вся вёрстка (шрифты, выравнивание, разделители, итог) считается на ПК, а на
/// принтер уходит готовый 1-битный растр — принтер не нагружается разбором ESC/POS
/// и печатает пиксели «как есть». Так же снимается проблема кодировок/шрифтов принтера.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReceiptRasterRenderer
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const int MaxHeight = 8000;   // защитный потолок высоты рулона

    /// <summary>Рисует тело чека: заголовок, позиции, разделители, итог (без штрих-кода).</summary>
    public static Bitmap RenderBody(ReceiptModel r, ReceiptGeometry geo, bool includeFooter)
    {
        int width = PrintPipeline.DotsFor(geo.TapeWidthMm);
        width -= width % 8;
        float margin = (float)(geo.SideMarginsPx * 203.0 / 96.0);
        float basePx = (float)(geo.FontSizePt / 72.0 * 203.0);   // pt → точки принтера @203dpi

        using var fBody  = new Font("Consolas", basePx,        FontStyle.Regular, GraphicsUnit.Pixel);
        using var fBold  = new Font("Consolas", basePx,        FontStyle.Bold,    GraphicsUnit.Pixel);
        using var fHead  = new Font("Segoe UI", basePx * 1.45f, FontStyle.Bold,   GraphicsUnit.Pixel);
        using var fTotal = new Font("Segoe UI", basePx * 1.2f,  FontStyle.Bold,   GraphicsUnit.Pixel);

        void Draw(Graphics g, ref float y)
        {
            CenterLine(g, r.Header, fHead, width, margin, ref y);
            if (!string.IsNullOrWhiteSpace(r.SubHeader))
                CenterLine(g, r.SubHeader, fBody, width, margin, ref y);
            Divider(g, width, margin, ref y, basePx);

            foreach (var it in r.Items)
            {
                LeftLine(g, it.Name, fBody, margin, ref y);
                TwoCol(g, $"  {it.Qty} × {it.Price.ToString("N2", Ru)}",
                          it.Sum.ToString("N2", Ru), fBody, width, margin, ref y);
            }

            Divider(g, width, margin, ref y, basePx);
            TwoCol(g, "ИТОГО:", r.Total.ToString("N2", Ru) + " р.", fTotal, width, margin, ref y);

            if (includeFooter && !string.IsNullOrWhiteSpace(r.Footer))
            {
                y += basePx * 0.6f;
                foreach (var ln in r.Footer.Split('\n'))
                    CenterLine(g, ln, fBody, width, margin, ref y);
            }
            y += basePx * 0.5f;   // нижний отступ
        }

        return RenderWithMeasure(width, Draw);
    }

    /// <summary>Рисует произвольный центрированный текстовый блок (напр. подпись под штрих-кодом).</summary>
    public static Bitmap RenderCentered(string text, ReceiptGeometry geo)
    {
        int width = PrintPipeline.DotsFor(geo.TapeWidthMm);
        width -= width % 8;
        float margin = (float)(geo.SideMarginsPx * 203.0 / 96.0);
        float basePx = (float)(geo.FontSizePt / 72.0 * 203.0);
        using var f = new Font("Consolas", basePx, FontStyle.Regular, GraphicsUnit.Pixel);

        return RenderWithMeasure(width, (Graphics g, ref float y) =>
        {
            foreach (var ln in text.Split('\n'))
                CenterLine(g, ln, f, width, margin, ref y);
            y += basePx * 0.4f;
        });
    }

    // ---------- Механика двухпроходного рендера (измерение → отрисовка) ----------
    private delegate void DrawAction(Graphics g, ref float y);

    private static Bitmap RenderWithMeasure(int width, DrawAction draw)
    {
        // Проход 1: измеряем итоговую высоту на временном холсте.
        float measured = 8f;
        using (var probe = new Bitmap(width, 8))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.PageUnit = GraphicsUnit.Pixel;
            draw(pg, ref measured);
        }
        int height = Math.Clamp((int)Math.Ceiling(measured) + 8, 8, MaxHeight);

        // Проход 2: рисуем на реальном холсте.
        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.PageUnit = GraphicsUnit.Pixel;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            float y = 8f;
            draw(g, ref y);
        }
        return bmp;
    }

    // ---------- Примитивы отрисовки ----------
    private static readonly StringFormat Typo = new(StringFormat.GenericTypographic) { FormatFlags = 0 };

    private static void LeftLine(Graphics g, string text, Font f, float margin, ref float y)
    {
        g.DrawString(text, f, Brushes.Black, margin, y, Typo);
        y += LineHeight(g, f);
    }

    private static void CenterLine(Graphics g, string text, Font f, int width, float margin, ref float y)
    {
        float w = g.MeasureString(text, f, int.MaxValue, Typo).Width;
        float x = Math.Max(margin, (width - w) / 2f);
        g.DrawString(text, f, Brushes.Black, x, y, Typo);
        y += LineHeight(g, f);
    }

    private static void TwoCol(Graphics g, string left, string right, Font f, int width, float margin, ref float y)
    {
        float rw = g.MeasureString(right, f, int.MaxValue, Typo).Width;
        g.DrawString(left, f, Brushes.Black, margin, y, Typo);
        g.DrawString(right, f, Brushes.Black, width - margin - rw, y, Typo);
        y += LineHeight(g, f);
    }

    private static void Divider(Graphics g, int width, float margin, ref float y, float basePx)
    {
        y += basePx * 0.25f;
        using var pen = new Pen(Color.Black, Math.Max(1f, basePx * 0.06f)) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, margin, y, width - margin, y);
        y += basePx * 0.45f;
    }

    private static float LineHeight(Graphics g, Font f) => f.GetHeight(g) * 1.02f;
}
