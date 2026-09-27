using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoPrint.Services;

/// <summary>
/// Восстановление картинки из 1-битного ESC/POS-буфера — чтобы показать результат
/// дизеринга ровно таким, каким его получит принтер.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MonoBitmap
{
    /// <summary>
    /// Через LockBits и нативный формат 1bpp — попиксельный SetPixel на растре 576×N
    /// давал сотни тысяч GDI-вызовов на каждый кадр превью (заметная нагрузка при таскании слайдеров).
    /// </summary>
    public static Bitmap Rebuild(byte[] packed, int widthBytes, int height)
    {
        int width = widthBytes * 8;
        var bmp = new Bitmap(width, height, PixelFormat.Format1bppIndexed);
        // Бит=1 (чёрная точка ESC/POS) → индекс 1 палитры = чёрный.
        var pal = bmp.Palette;
        pal.Entries[0] = Color.White;
        pal.Entries[1] = Color.Black;
        bmp.Palette = pal;

        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(packed, y * widthBytes, data.Scan0 + y * data.Stride, widthBytes);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
