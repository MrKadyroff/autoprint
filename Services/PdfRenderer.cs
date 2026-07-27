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

    /// <summary>Рендерит PDF в единый Bitmap шириной ~<paramref name="targetWidth"/> точек.</summary>
    public static Bitmap Render(byte[] pdfBytes, int targetWidth)
    {
        lock (_gate)
        {
            var lib = DocLib.Instance;

            // 1) Пробный проход: узнаём натуральную ширину первой страницы (в точках @72dpi).
            double nativeW;
            using (var probe = lib.GetDocReader(pdfBytes, new PageDimensions(1.0)))
            using (var p0 = probe.GetPageReader(0))
                nativeW = p0.GetPageWidth();
            if (nativeW <= 0) nativeW = targetWidth;

            double scale = targetWidth / nativeW;   // масштаб под ширину печати

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
            g.DrawImage(p, 0, y, width, p.Height);
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
