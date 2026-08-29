using System.IO;
using System.Text.Json;

namespace AutoPrint.Services;

/// <summary>
/// Небольшой персистентный конфиг (%AppData%\AutoPrint\config.json).
/// Хранит настройки, которые должны переживать перезапуск — в частности координаты
/// GitHub-репозитория для автообновления.
/// </summary>
public class AppConfig
{
    public string UpdateOwner { get; set; } = "your-org";
    public string UpdateRepo { get; set; } = "autoprint";
    public bool AllowPrerelease { get; set; } = false;

    /// <summary>Автоматически проверять и ставить обновления в фоне, без участия пользователя.</summary>
    public bool AutoUpdateEnabled { get; set; } = true;
    /// <summary>Как часто проверять обновления в фоне (часы).</summary>
    public double AutoUpdateCheckHours { get; set; } = 4;

    // ---- Сохраняемые настройки геометрии ленты, шрифтов и QR ----
    public double TapeWidthMm { get; set; } = 80;
    public double SideMarginsPx { get; set; } = 12;
    public double HeaderFontPt { get; set; } = 11;
    public double BodyFontPt { get; set; } = 11;
    public bool GraphicsMode { get; set; } = true;
    public bool QrEnabled { get; set; } = true;
    public double QrSizeMm { get; set; } = 22;

    // ---- Настройки принтера ----
    public int TopMarginLines { get; set; } = 0;
    public int BottomMarginLines { get; set; } = 4;
    public bool AutoCut { get; set; } = true;
    public bool FullCut { get; set; } = false;
    public bool OpenDrawer { get; set; } = false;

    /// <summary>Желаемое имя, под которым принтер регистрируется/переименовывается в Windows.</summary>
    public string PrinterFriendlyName { get; set; } = "Mulex P80mm";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPrint");
    private static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch { /* повреждён — вернём дефолт */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не критично */ }
    }
}
