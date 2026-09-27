using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>Результат предпросмотра: либо картинка ленты, либо моноширинный текст.</summary>
public record ReceiptPreview(BitmapSource? Image, string? Text)
{
    public static ReceiptPreview FromText(string text) => new(null, text);
    public static ReceiptPreview FromImage(BitmapSource img) => new(img, null);
}

/// <summary>
/// Рендер задания печати в то, что увидит кассир на ленте.
/// Повторяет маршрутизацию <see cref="PrintPipeline.BuildBytesAsync"/>, но останавливается
/// на растре и прогоняет его через тот же дизеринг — поэтому просмотр чека из истории
/// показывает ровно то, что ушло (или уйдёт) на принтер, а не приблизительную вёрстку.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReceiptPreviewRenderer
{
    /// <summary>
    /// Рендерит задание с ЕГО СОБСТВЕННОЙ геометрией (сохранённой в момент постановки
    /// в очередь), а не с текущими настройками конструктора — иначе чек месячной давности
    /// показывался бы шрифтом и шириной, выставленными сегодня.
    /// Для HTML требуется UI-поток (WebView2), поэтому метод асинхронный.
    /// </summary>
    public static async Task<ReceiptPreview> RenderAsync(PrintJob job, ReceiptGeometry geo)
    {
        int dots = PrintPipeline.DotsFor(geo.TapeWidthMm);
        int inset = PrintPipeline.MarginDots(geo.SideMarginsPx);

        switch (job.Format)
        {
            case ReceiptFormat.Json:
                if (FiscalReceiptRenderer.IsFiscalJson(job.Payload))
                {
                    var fiscal = FiscalReceiptRenderer.Parse(job.Payload);
                    // Тот же макет, что берёт печать, — TemplateStore по типу документа.
                    var tmpl = TemplateStore.Load(fiscal.DocType);
                    return ReceiptPreview.FromImage(await Task.Run(() =>
                        Mono(FiscalReceiptRenderer.Render(fiscal, tmpl, geo), dots)));
                }
                var model = ReceiptComposer.ParseJson(job.Payload);
                if (geo.RenderMode == PrintRenderMode.Graphics)
                    return ReceiptPreview.FromImage(await Task.Run(() =>
                        Mono(ReceiptRasterRenderer.RenderBody(model, geo, includeFooter: true), dots)));
                // Текстовый режим: вёрстку делает сам принтер, показываем её символами.
                return ReceiptPreview.FromText(TextPreview(model, geo));

            case ReceiptFormat.Html:
            {
                int widthPx = (int)Math.Round(PrintPipeline.PixelsFor(geo.TapeWidthMm));
                var html = new HtmlRenderer();
                using Bitmap bmp = await html.RenderAsync(
                    job.Payload, widthPx, geo.BodyFontPt, (int)geo.SideMarginsPx);
                return ReceiptPreview.FromImage(Mono(bmp, dots, 0, disposeSource: false));
            }

            case ReceiptFormat.Image:
                if (job.RawImage is null) return ReceiptPreview.FromText("Нет данных изображения.");
                return ReceiptPreview.FromImage(await Task.Run(() =>
                    Mono(ImageProcessor.FromBytes(job.RawImage), dots, inset)));

            case ReceiptFormat.Pdf:
                if (job.RawImage is null) return ReceiptPreview.FromText("Нет данных PDF.");
                return ReceiptPreview.FromImage(await Task.Run(() =>
                    Mono(PdfRenderer.Render(job.RawImage, dots), dots, inset)));

            default:
                return ReceiptPreview.FromText("Формат не поддерживается.");
        }
    }

    /// <summary>Дизеринг в 1 бит и обратно в картинку — показываем результат, а не исходник.</summary>
    private static BitmapSource Mono(Bitmap source, int dots, int insetDots = 0, bool disposeSource = true)
    {
        try
        {
            var (packed, wb, h) = ImageProcessor.PrepareRaster(source, dots, insetDots);
            using Bitmap mono = MonoBitmap.Rebuild(packed, wb, h);
            return WpfInterop.ToImageSource(mono);
        }
        finally { if (disposeSource) source.Dispose(); }
    }

    /// <summary>Моноширинный предпросмотр для текстового (не графического) режима JSON.</summary>
    private static string TextPreview(ReceiptModel r, ReceiptGeometry geo)
    {
        int cols = ReceiptComposer.ColumnsFor(geo.TapeWidthMm);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Center(r.Header, cols));
        if (!string.IsNullOrWhiteSpace(r.SubHeader)) sb.AppendLine(Center(r.SubHeader, cols));
        sb.AppendLine(new string('-', cols));
        foreach (var it in r.Items)
        {
            sb.AppendLine(it.Name);
            string left = $"  {it.Qty} x {it.Price:N2}";
            string right = it.Sum.ToString("N2");
            sb.AppendLine(left + new string(' ', Math.Max(1, cols - left.Length - right.Length)) + right);
        }
        sb.AppendLine(new string('-', cols));
        sb.AppendLine($"ИТОГО: {r.Total:N2} р.");
        if (!string.IsNullOrWhiteSpace(r.Barcode)) sb.AppendLine("\n" + Center("|| " + r.Barcode + " ||", cols));
        if (!string.IsNullOrWhiteSpace(r.Footer)) sb.AppendLine(Center(r.Footer, cols));
        return sb.ToString();
    }

    private static string Center(string s, int cols) => string.Join('\n',
        s.Split('\n').Select(l =>
        {
            l = l.Trim();
            return new string(' ', Math.Max(0, (cols - l.Length) / 2)) + l;
        }));
}
