using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;

namespace AutoPrint.Services;

[SupportedOSPlatform("windows")]
public static class WpfInterop
{
    /// <summary>Конвертирует GDI+ Bitmap в замороженный WPF BitmapSource (потокобезопасно для UI).</summary>
    public static BitmapSource ToImageSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
