using System.Diagnostics;
using Microsoft.Win32;

namespace AutoPrint.Services;

/// <summary>
/// Автозапуск с Windows через HKCU\...\Run — вместо ярлыка в папке автозагрузки.
/// Переживает переустановку/обновление (реестр не трогается инсталлятором/распаковкой zip),
/// не пропадает при ручном перемещении exe без переустановки записи, и не требует прав
/// администратора (HKCU, а не HKLM).
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoPrint";

    /// <summary>Аргумент командной строки, с которым запускается процесс из автозагрузки —
    /// чтобы окно не выскакивало на экран при каждом включении компьютера.</summary>
    public const string MinimizedArg = "--minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            FileLog.Error("AutostartService.IsEnabled", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                string? exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("AutostartService.SetEnabled", ex);
        }
    }
}
