using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AutoPrint.Services;

/// <summary>
/// Рендер сырого HTML чека в растровое изображение через offscreen WebView2 (Chromium).
///
/// Логика:
///  1. Один переиспользуемый WebView2 живёт в отдельном off-screen окне (собственный
///     airspace — он не рисует чёрным поверх главного окна).
///  2. Загружаем HTML строкой (NavigateToString) + инжектим CSS (@page, ширина, шрифт).
///  3. Ждём событие NavigationCompleted (DOM отрисован).
///  4. Определяем реальную высоту документа через scrollHeight, ресайзим контрол.
///  5. CapturePreviewAsync(PNG) — снимок viewport в поток → Bitmap.
///  6. Bitmap уходит в <see cref="ImageProcessor"/> для дизеринга и печати как растр.
///
/// Так фронтенд может присылать привычный HTML/CSS, а на термопринтер уходит
/// пиксельно точный слепок вёрстки.
/// </summary>
[SupportedOSPlatform("windows")]
public class HtmlRenderer
{
    private readonly string _userDataFolder;

    // Один WebView2 на всё приложение: его пересоздание на каждый рендер и размещение
    // в Popup со смещением -10000 давало «чёрный экран» в левом углу окна (airspace-глюк
    // WPF при разных DPI/мониторах). Держим контрол в отдельном скрытом окне и переиспользуем.
    private static Window? _host;
    private static WebView2? _web;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public HtmlRenderer()
    {
        _userDataFolder = Path.Combine(Path.GetTempPath(), "AutoPrint.WebView2");
        Directory.CreateDirectory(_userDataFolder);
    }

    /// <summary>Рендерит HTML в Bitmap. Должен вызываться из UI-потока (WebView2 — UI-контрол).</summary>
    public async Task<Bitmap> RenderAsync(string html, int widthPx, double fontSizePt, int sideMarginsPx)
    {
        // WebView2 переиспользуется — параллельные рендеры (превью + печать) сериализуем.
        await _gate.WaitAsync();
        try
        {
            WebView2 web = await EnsureWebViewAsync();
            web.Width = widthPx;
            web.Height = 10;   // временная, скорректируем после загрузки

            // Оборачиваем пользовательский HTML в контейнер с геометрией ленты.
            string wrapped = WrapHtml(html, widthPx, fontSizePt, sideMarginsPx);

            var navDone = new TaskCompletionSource<bool>();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(e.IsSuccess);
            web.CoreWebView2.NavigationCompleted += OnNav;
            web.CoreWebView2.NavigateToString(wrapped);
            await navDone.Task;
            web.CoreWebView2.NavigationCompleted -= OnNav;

            // Узнаём фактическую высоту контента и подгоняем размер вьюпорта.
            string hStr = await web.CoreWebView2.ExecuteScriptAsync(
                "Math.ceil(Math.max(document.body.scrollHeight, document.documentElement.scrollHeight))");
            int contentHeight = (int)Math.Ceiling(double.Parse(hStr.Trim('"'),
                System.Globalization.CultureInfo.InvariantCulture));
            web.Height = Math.Max(contentHeight, 1);

            // Даём кадру перерисоваться в новый размер.
            await Task.Delay(60);

            using var ms = new MemoryStream();
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Лениво создаёт (один раз) off-screen окно с WebView2 и инициализирует Chromium.</summary>
    private async Task<WebView2> EnsureWebViewAsync()
    {
        if (_web?.CoreWebView2 is not null) return _web;

        var web = new WebView2 { Width = 384, Height = 10 };

        // Отдельное скрытое top-level окно за пределами экрана: у него собственный airspace,
        // поэтому HWND WebView2 не может «проступить» чёрным прямоугольником поверх главного окна.
        var host = new Window
        {
            Width = 1, Height = 1,
            Left = -32000, Top = -32000,          // гарантированно вне любого монитора
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            AllowsTransparency = false,
            Content = web
        };
        host.Show();   // WebView2 обязан быть в визуальном дереве, иначе не инициализируется

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await web.EnsureCoreWebView2Async(env);
        web.DefaultBackgroundColor = Color.White;   // без этого возможна чёрная вспышка при загрузке
        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        web.CoreWebView2.Settings.IsScriptEnabled = true;

        _host = host;
        _web = web;
        return web;
    }

    private static string WrapHtml(string inner, int widthPx, double fontSizePt, int marginsPx) => $@"
<!DOCTYPE html>
<html><head><meta charset='utf-8'>
<style>
  html, body {{ margin:0; padding:0; background:#fff; }}
  body {{
    width:{widthPx}px;
    box-sizing:border-box;
    padding:{marginsPx}px;
    font-family:'Segoe UI', Arial, sans-serif;
    font-size:{fontSizePt.ToString(System.Globalization.CultureInfo.InvariantCulture)}pt;
    color:#000;
    -webkit-font-smoothing:none;
  }}
  * {{ max-width:100%; }}
  img {{ image-rendering:pixelated; }}
</style></head>
<body>{inner}</body></html>";
}
