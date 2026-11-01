using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Docnet.Core;
using Docnet.Core.Models;

namespace AutoPrint.Services;

/// <summary>
/// Рендер PDF-чека (приходит с фронта из pdfMake) в растровое изображение через PDFium
/// (Docnet.Core). Все страницы склеиваются вертикально в одну ленту, ширина подгоняется
/// под печатное поле принтера. Дальше растр уходит в <see cref="ImageProcessor"/> на дизеринг.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PdfRenderer
{
    // DocLib.Instance — процессный синглтон PDFium, не потокобезопасен: сериализуем доступ.
    private static readonly object _gate = new();

    /// <summary>
    /// Рендерит PDF в единый Bitmap шириной ровно <paramref name="targetWidth"/> точек.
    /// Если задан <paramref name="targetEmPx"/> и в PDF есть текстовый слой, масштаб подбирается так,
    /// чтобы размер текста совпал с настройкой шрифта приложения (как в JSON-чеках); содержимое
    /// центрируется, но не шире <paramref name="targetWidth"/> − 2·<paramref name="marginDots"/>.
    /// Без текстового слоя содержимое просто растягивается на печатное поле.
    /// </summary>
    public static Bitmap Render(byte[] pdfBytes, int targetWidth, double targetEmPx = 0, int marginDots = 0)
    {
        lock (_gate)
        {
            var lib = DocLib.Instance;
            int contentW = Math.Max(8, targetWidth - 2 * Math.Max(0, marginDots));

            // 1) Пробный проход @72dpi: натуральная ширина и типичный размер глифов.
            double nativeW, emNative = 0;
            using (var probe = lib.GetDocReader(pdfBytes, new PageDimensions(1.0)))
            using (var p0 = probe.GetPageReader(0))
            {
                nativeW = p0.GetPageWidth();
                if (targetEmPx > 0) emNative = MedianGlyphEm(p0);
            }
            if (nativeW <= 0) nativeW = contentW;

            double scale = contentW / nativeW;   // по умолчанию — вписать в печатное поле
            if (emNative > 0)
            {
                // Подгоняем размер текста под шрифт из настроек, но не шире печатного поля.
                scale = Math.Min(targetEmPx / emNative, scale);
            }

            // 2) Реальный рендер всех страниц.
            using var reader = lib.GetDocReader(pdfBytes, new PageDimensions(scale));
            int pageCount = reader.GetPageCount();

            var pages = new List<Bitmap>();
            try
            {
                for (int i = 0; i < pageCount; i++)
                {
                    using var pr = reader.GetPageReader(i);
                    int w = pr.GetPageWidth();
                    int h = pr.GetPageHeight();
                    byte[] bgra = pr.GetImage();     // BGRA, w*h*4, прозрачный фон
                    pages.Add(BgraToBitmap(bgra, w, h));
                }

                return StackVertical(pages, targetWidth);
            }
            finally
            {
                foreach (var b in pages) b.Dispose();
            }
        }
    }

    /// <summary>
    /// Типичный кегль текста страницы (px @72dpi): медиана высоты рамок символов / 0.72
    /// (рамка PDFium облегает глиф, а он в среднем ~0.72 em). 0 — если текста нет.
    /// </summary>
    private static double MedianGlyphEm(Docnet.Core.Readers.IPageReader page)
    {
        try
        {
            var heights = page.GetCharacters()
                .Where(c => !char.IsWhiteSpace(c.Char) && c.Box.Bottom > c.Box.Top)
                .Select(c => (double)(c.Box.Bottom - c.Box.Top))
                .OrderBy(x => x)
                .ToList();
            if (heights.Count < 5) return 0;
            return heights[heights.Count / 2] / 0.72;
        }
        catch { return 0; }
    }

    /// <summary>Извлекает текстовый слой PDF (все страницы) — для определения категории чека.
    /// Возвращает пустую строку, если текста нет (скан) или парсинг не удался.</summary>
    public static string ExtractText(byte[] pdfBytes)
    {
        try
        {
            lock (_gate)
            {
                var lib = DocLib.Instance;
                using var reader = lib.GetDocReader(pdfBytes, new PageDimensions(1.0));
                int pageCount = reader.GetPageCount();
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < pageCount; i++)
                {
                    using var pr = reader.GetPageReader(i);
                    sb.Append(pr.GetText()).Append(' ');
                }
                // PDFium часто разбивает текст переносами между словами — сводим все пробельные
                // последовательности к одному пробелу, чтобы фразы ("закрытие смены") искались надёжно.
                return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");
            }
        }
        catch { return ""; }
    }

    /// <summary>Склеивает страницы в одну ленту на белом фоне.</summary>
    private static Bitmap StackVertical(List<Bitmap> pages, int width)
    {
        if (pages.Count == 0) return new Bitmap(width, 1);
        int totalH = pages.Sum(p => p.Height);

        var canvas = new Bitmap(width, totalH, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(canvas);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.White);              // прозрачные области PDF → белый

        int y = 0;
        foreach (var p in pages)
        {
            g.DrawImage(p, (width - p.Width) / 2, y, p.Width, p.Height);   // по центру, без растяжения
            y += p.Height;
        }
        return canvas;
    }

    /// <summary>Оборачивает сырой BGRA-буфер PDFium в GDI+ Bitmap.</summary>
    private static Bitmap BgraToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Копируем построчно с учётом stride (PDFium отдаёт плотный w*4).
            int srcStride = w * 4;
            for (int row = 0; row < h; row++)
                Marshal.Copy(bgra, row * srcStride, data.Scan0 + row * data.Stride, srcStride);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
