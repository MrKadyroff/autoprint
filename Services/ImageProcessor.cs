using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;

namespace AutoPrint.Services;

/// <summary>
/// Конвертация произвольного изображения (PNG/JPEG или рендер HTML) в
/// монохромный 1-битный растр для термопечати с алгоритмом Флойда–Стейнберга.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageProcessor
{
    /// <summary>
    /// Готовит изображение к печати: масштабирует под ширину ленты в точках,
    /// делает dithering и упаковывает в формат GS v 0.
    /// </summary>
    /// <param name="source">Исходный битмап (любой формат).</param>
    /// <param name="targetWidthDots">Ширина печати в точках принтера (напр. 576 для 80мм@203dpi).</param>
    public static (byte[] packed, int widthBytes, int heightPx) PrepareRaster(Bitmap source, int targetWidthDots)
    {
        // Ширина растра ESC/POS кратна 8 точкам.
        int width = targetWidthDots - (targetWidthDots % 8);
        int height = (int)Math.Round(source.Height * (width / (double)source.Width));

        using var scaled = ScaleTo(source, width, height);
        byte[,] gray = ToGrayscale(scaled);
        FloydSteinberg(gray, width, height);       // grey -> 0/255 с распространением ошибки

        int widthBytes = width / 8;
        var packed = new byte[widthBytes * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // В ESC/POS установленный бит = чёрная точка. gray==0 (после дизеринга) => чёрный.
                if (gray[x, y] < 128)
                {
                    int idx = y * widthBytes + (x >> 3);
                    packed[idx] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        return (packed, widthBytes, height);
    }

    /// <summary>
    /// То же, что <see cref="PrepareRaster(Bitmap,int)"/>, но с горизонтальным отступом:
    /// содержимое масштабируется под (ширина − 2·inset) и центрируется на белом холсте полной
    /// ширины. Так входящая картинка/PDF печатается уже, с полями по краям — единственный способ
    /// применить «боковые отступы» к готовому растру (размер текста в пикселях изменить нельзя).
    /// </summary>
    public static (byte[] packed, int widthBytes, int heightPx) PrepareRaster(
        Bitmap source, int targetWidthDots, int insetDots)
    {
        if (insetDots <= 0) return PrepareRaster(source, targetWidthDots);

        int width = targetWidthDots - (targetWidthDots % 8);
        int inset = Math.Clamp(insetDots, 0, width / 2 - 8);
        int contentW = width - 2 * inset;
        int contentH = (int)Math.Round(source.Height * (contentW / (double)source.Width));

        using var canvas = new Bitmap(width, contentH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.Clear(Color.White);
            g.DrawImage(source, inset, 0, contentW, contentH);
        }
        return PrepareRaster(canvas, targetWidthDots);
    }

    /// <summary>
    /// Подгоняет готовую картинку под настройки: масштаб выбирается так, чтобы типичный размер
    /// текста на ней совпал со шрифтом из настроек (<paramref name="targetEmPx"/> в точках принтера),
    /// но содержимое не шире печатного поля (ширина − 2·отступ). Результат — холст полной ширины,
    /// содержимое по центру. Если текст на картинке не найден — вписывается в печатное поле.
    /// Вызывающий освобождает результат.
    /// </summary>
    public static Bitmap FitByFont(Bitmap source, int targetWidthDots, int marginDots, double targetEmPx)
    {
        int width = targetWidthDots - (targetWidthDots % 8);
        int contentMax = Math.Max(8, width - 2 * Math.Clamp(marginDots, 0, width / 2 - 8));

        double scale = contentMax / (double)source.Width;
        double em = EstimateTextEm(source);
        if (em > 0 && targetEmPx > 0)
            scale = Math.Min(targetEmPx / em, scale);

        int cw = Math.Max(1, (int)Math.Round(source.Width * scale));
        int ch = Math.Max(1, (int)Math.Round(source.Height * scale));
        var canvas = new Bitmap(width, ch, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(canvas);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.White);
        g.DrawImage(source, (width - cw) / 2, 0, cw, ch);
        return canvas;
    }

    /// <summary>
    /// Оценивает кегль текста на растре (px исходника) по высоте строк: профиль тёмных пикселей
    /// по вертикали → полосы строк → медиана (≈0.8 em с учётом строк без выносных элементов).
    /// Слишком высокие полосы (логотипы, QR, штрих-коды) отбрасываются. 0 — если текст не найден.
    /// </summary>
    private static double EstimateTextEm(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 16 || h < 16) return 0;
        var dark = new int[h];
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * data.Stride;
                    int n = 0;
                    for (int x = 0; x < w; x++)
                        if ((row[x * 3] * 114 + row[x * 3 + 1] * 587 + row[x * 3 + 2] * 299) / 1000 < 128) n++;
                    dark[y] = n;
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        var runs = new List<int>();
        int start = -1;
        for (int y = 0; y <= h; y++)
        {
            bool ink = y < h && dark[y] >= 2;
            if (ink && start < 0) start = y;
            else if (!ink && start >= 0) { runs.Add(y - start); start = -1; }
        }
        runs = runs.Where(r => r >= 4).OrderBy(r => r).ToList();
        if (runs.Count < 3) return 0;
        double med = runs[runs.Count / 2];
        // Отбрасываем явно нетекстовые полосы и берём медиану ещё раз.
        var text = runs.Where(r => r <= med * 2.5).ToList();
        return text[text.Count / 2] / 0.8;
    }

    private static Bitmap ScaleTo(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.Clear(Color.White);
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static byte[,] ToGrayscale(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var gray = new byte[w, h];
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 3];
                        byte gr = row[x * 3 + 1];
                        byte r = row[x * 3 + 2];
                        // Luma (Rec. 601)
                        gray[x, y] = (byte)((r * 299 + gr * 587 + b * 114) / 1000);
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return gray;
    }

    /// <summary>Классический Floyd–Steinberg: ошибка квантования распространяется на соседей.</summary>
    private static void FloydSteinberg(byte[,] g, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int old = g[x, y];
                int nv = old < 128 ? 0 : 255;
                int err = old - nv;
                g[x, y] = (byte)nv;

                Spread(g, x + 1, y, err * 7 / 16, w, h);
                Spread(g, x - 1, y + 1, err * 3 / 16, w, h);
                Spread(g, x, y + 1, err * 5 / 16, w, h);
                Spread(g, x + 1, y + 1, err * 1 / 16, w, h);
            }
        }
    }

    private static void Spread(byte[,] g, int x, int y, int add, int w, int h)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        g[x, y] = (byte)Math.Clamp(g[x, y] + add, 0, 255);
    }

    public static Bitmap FromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return new Bitmap(ms);
    }
}
