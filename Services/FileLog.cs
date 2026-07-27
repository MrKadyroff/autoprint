using System.IO;

namespace AutoPrint.Services;

/// <summary>
/// Простой потокобезопасный файловый лог в %AppData%\AutoPrint\logs.
/// Нужен, чтобы диагностировать падения «бесперебойного» сервера постфактум.
/// </summary>
public static class FileLog
{
    private static readonly object _gate = new();

    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPrint", "logs");

    private static string TodayFile => Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string text)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(TodayFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {text}\n");
            }
        }
        catch { /* логирование не должно ронять приложение */ }
    }

    /// <summary>Удаляет логи и краш-дампы старше keepDays (вызывается при старте).</summary>
    public static void CleanupOldLogs(int keepDays = 14)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(LogDir, "*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    try { File.Delete(file); } catch { }
        }
        catch { /* уборка не должна ронять приложение */ }
    }

    /// <summary>Записывает подробный краш-дамп отдельным файлом.</summary>
    public static string WriteCrash(string source, Exception ex)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(LogDir);
                string path = Path.Combine(LogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                File.WriteAllText(path, $"Источник: {source}\nВремя: {DateTime.Now}\n\n{ex}");
                return path;
            }
        }
        catch { return ""; }
    }
}
