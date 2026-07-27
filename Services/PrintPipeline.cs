using System.Drawing;
using System.Runtime.Versioning;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>
/// Единая точка обработки задания печати: маршрутизирует по формату,
/// формирует ESC/POS байты и (опционально) шлёт их на принтер.
/// </summary>
[SupportedOSPlatform("windows")]
public class PrintPipeline
{
    private readonly HtmlRenderer _html = new();

    /// <summary>
    /// Печатных точек по ширине для растровых форматов (203 dpi).
    /// Отраслевой стандарт: 80мм → 576 точек, 58мм → 384. Значение кратно 8
    /// (требование ESC/POS-растра) и совпадает с шириной, которую рисует фронт.
    /// </summary>
    public static int DotsFor(double tapeWidthMm)
    {
        double printableMm = tapeWidthMm <= 62 ? 48 : 72;
        int dots = (int)Math.Round(printableMm / 25.4 * 203);
        return (dots + 7) / 8 * 8;   // округлить вверх до кратного 8 → 576 / 384
    }

    /// <summary>WPF-пиксели по ширине ленты для Live Preview (96 DPI).</summary>
    public static double PixelsFor(double tapeWidthMm) => tapeWidthMm / 25.4 * 96.0;

    /// <summary>WPF-пиксели (96 DPI) → точки принтера (203 DPI). Для перевода боковых отступов в растр.</summary>
    public static int MarginDots(double sideMarginsPx) => (int)Math.Round(sideMarginsPx * 203.0 / 96.0);

    /// <summary>
    /// Готовит финальные ESC/POS байты для задания. Для HTML/Image выполняется
    /// рендер + дизеринг; для JSON — сборка векторного макета.
    /// </summary>
    public async Task<byte[]> BuildBytesAsync(PrintJob job, ReceiptGeometry geo)
    {
        switch (job.Format)
        {
            case ReceiptFormat.Json:
                // GDI-рендер/сборка — CPU-bound, уводим с UI-потока.
                return await Task.Run(() =>
                {
                    // Фискальный чек ОФД (структурный JSON с фронта) — всегда графикой,
                    // макет зависит от типа документа (продажа/смена/внесение/изъятие).
                    if (FiscalReceiptRenderer.IsFiscalJson(job.Payload))
                    {
                        var fiscal = FiscalReceiptRenderer.Parse(job.Payload);
                        var tmpl = TemplateStore.Load(fiscal.DocType);   // редактируемый макет типа
                        using Bitmap page = FiscalReceiptRenderer.Render(fiscal, tmpl, geo);
                        return RasterToEscPos(page, geo);
                    }

                    var model = ReceiptComposer.ParseJson(job.Payload);
                    return geo.RenderMode == PrintRenderMode.Graphics
                        ? ComposeJsonAsGraphics(model, geo)   // вёрстка на ПК → растр
                        : ReceiptComposer.Compose(model, geo); // текст ESC/POS (вёрстка на принтере)
                });

            case ReceiptFormat.Html:
            {
                // WebView2 обязан работать на UI-потоке — здесь Task.Run нельзя.
                int widthPx = (int)Math.Round(PixelsFor(geo.TapeWidthMm));
                using Bitmap rendered = await _html.RenderAsync(
                    job.Payload, widthPx, geo.FontSizePt, (int)geo.SideMarginsPx);
                return RasterToEscPos(rendered, geo);
            }

            case ReceiptFormat.Image:
            {
                if (job.RawImage is null) throw new InvalidOperationException("Нет данных изображения.");
                return await Task.Run(() =>
                {
                    using Bitmap src = ImageProcessor.FromBytes(job.RawImage);
                    // Картинка приходит готовым растром — вёрстки нет, поэтому боковые отступы
                    // применяем здесь (текст в пикселях не масштабируем — его в картинке уже нет).
                    return RasterToEscPos(src, geo, MarginDots(geo.SideMarginsPx));
                });
            }

            case ReceiptFormat.Pdf:
            {
                if (job.RawImage is null) throw new InvalidOperationException("Нет данных PDF.");
                return await Task.Run(() =>
                {
                    using Bitmap page = PdfRenderer.Render(job.RawImage, DotsFor(geo.TapeWidthMm));
                    return RasterToEscPos(page, geo, MarginDots(geo.SideMarginsPx));
                });
            }

            default:
                throw new NotSupportedException();
        }
    }

    /// <summary>
    /// JSON-чек, целиком собранный на ПК: тело рисуется в растр (GDI+), штрих-код
    /// печатается нативной командой (принтеры делают это надёжно и без нагрузки),
    /// подпись/футер — снова растром. Принтер получает почти готовые пиксели.
    /// </summary>
    private static byte[] ComposeJsonAsGraphics(ReceiptModel model, ReceiptGeometry geo)
    {
        int dots = DotsFor(geo.TapeWidthMm);
        bool hasBarcode = !string.IsNullOrWhiteSpace(model.Barcode);

        var e = new EscPos();
        if (geo.TopMarginLines > 0) e.Feed(geo.TopMarginLines);
        e.AlignCenter();

        using (Bitmap body = ReceiptRasterRenderer.RenderBody(model, geo, includeFooter: !hasBarcode))
        {
            var (packed, wb, h) = ImageProcessor.PrepareRaster(body, dots);
            e.RasterImage(packed, wb, h);
        }

        if (hasBarcode)
        {
            e.Feed(1).BarcodeCode128(model.Barcode).Feed(1);
            if (!string.IsNullOrWhiteSpace(model.Footer))
                using (Bitmap footer = ReceiptRasterRenderer.RenderCentered(model.Footer, geo))
                {
                    var (packed, wb, h) = ImageProcessor.PrepareRaster(footer, dots);
                    e.RasterImage(packed, wb, h);
                }
        }

        e.AlignLeft().Finish(geo.BottomMarginLines, geo.AutoCut, geo.FullCut, geo.OpenDrawer);
        return e.ToArray();
    }

    private static byte[] RasterToEscPos(Bitmap bmp, ReceiptGeometry geo, int insetDots = 0)
    {
        int dots = DotsFor(geo.TapeWidthMm);
        var (packed, widthBytes, height) = ImageProcessor.PrepareRaster(bmp, dots, insetDots);
        var e = new EscPos();
        if (geo.TopMarginLines > 0) e.Feed(geo.TopMarginLines);
        e.AlignCenter()
            .RasterImage(packed, widthBytes, height)
            .AlignLeft()
            .Finish(geo.BottomMarginLines, geo.AutoCut, geo.FullCut, geo.OpenDrawer);
        return e.ToArray();
    }

    /// <summary>Собирает и печатает задание на указанный принтер.</summary>
    public async Task<int> PrintAsync(PrintJob job, ReceiptGeometry geo, string printerName)
    {
        byte[] bytes = await BuildBytesAsync(job, geo);
        await Task.Run(() => RawPrinterHelper.SendBytes(printerName, bytes));
        return bytes.Length;
    }
}
