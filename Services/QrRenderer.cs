using System.Drawing;
using System.Runtime.Versioning;
using QRCoder;

namespace AutoPrint.Services;

/// <summary>
/// Генерация QR-кода ссылки проверки чека в чёткий 1-битный растр под термопечать.
/// Модули рисуются целыми пикселями (без сглаживания) — иначе после дизеринга
/// QR «плывёт» и не сканируется. Итоговый размер подгоняется под заданную ширину в точках.
/// </summary>
[SupportedOSPlatform("windows")]
public static class QrRenderer
{
    /// <summary>Перевод миллиметров в точки принтера (203 dpi).</summary>
    public static int MmToDots(double mm) => (int)Math.Round(mm / 25.4 * 203.0);

    /// <summary>
    /// Рисует QR из <paramref name="text"/> с целевой шириной <paramref name="targetPx"/> точек.
    /// Размер модуля выбирается так, чтобы код был максимально близок к целевому размеру,
    /// но кратен модулю (для чёткости). Возвращает квадратный чёрно-белый Bitmap.
    /// </summary>
    public static Bitmap Render(string text, int targetPx)
    {
        using var gen = new QRCodeGenerator();
        using QRCodeData data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);

        int modules = data.ModuleMatrix.Count;              // включает тихую зону (4 модуля с каждой стороны)
        int pixelsPerModule = Math.Max(1, targetPx / modules);
        int side = modules * pixelsPerModule;

        var bmp = new Bitmap(side, side);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.Clear(Color.White);
            for (int r = 0; r < modules; r++)
            {
                var row = data.ModuleMatrix[r];
                for (int cX = 0; cX < modules; cX++)
                {
                    if (!row[cX]) continue;
                    g.FillRectangle(Brushes.Black,
                        cX * pixelsPerModule, r * pixelsPerModule, pixelsPerModule, pixelsPerModule);
                }
            }
        }
        return bmp;
    }
}
