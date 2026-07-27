using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AutoPrint.Services;

/// <summary>Описание найденного инсталлятора драйвера принтера.</summary>
public class DriverPackage
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public long SizeBytes { get; init; }

    public string SizeLabel => $"{SizeBytes / 1024.0 / 1024.0:0.0} МБ";
    public override string ToString() => $"{DisplayName}  ({SizeLabel})";
}

/// <summary>
/// Обнаружение и запуск инсталляторов драйверов принтера, поставляемых вместе
/// с приложением. Установка драйвера требует прав администратора, поэтому
/// процесс запускается через ShellExecute с verb="runas" (UAC-повышение).
/// </summary>
public static class DriverInstaller
{
    /// <summary>Понятные названия для известных инсталляторов.</summary>
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DriverInstall.exe"] = "Mulex P80 — драйвер принтера (DriverInstall)",
        ["Normal POS Driver VL2.0.1.exe"] = "Normal POS Driver v2.0.1 (универсальный)",
    };

    /// <summary>Папка с инсталляторами: Drivers\ рядом с приложением.</summary>
    public static string DriversFolder =>
        Path.Combine(AppContext.BaseDirectory, "Drivers");

    /// <summary>Сканирует папку Drivers на предмет *.exe инсталляторов.</summary>
    public static IReadOnlyList<DriverPackage> Discover()
    {
        var result = new List<DriverPackage>();
        if (!Directory.Exists(DriversFolder)) return result;

        foreach (var path in Directory.EnumerateFiles(DriversFolder, "*.exe"))
        {
            var fi = new FileInfo(path);
            result.Add(new DriverPackage
            {
                FileName = fi.Name,
                FullPath = fi.FullName,
                SizeBytes = fi.Length,
                DisplayName = FriendlyNames.TryGetValue(fi.Name, out var n) ? n : fi.Name
            });
        }
        return result;
    }

    /// <summary>Движок, которым собран инсталлятор — определяет флаги тихой установки.</summary>
    public enum InstallerEngine { Unknown, InnoSetup, Nsis }

    /// <summary>Определяет движок инсталлятора по сигнатурам в самом exe.</summary>
    public static InstallerEngine DetectEngine(string path)
    {
        try
        {
            // Сигнатуры лежат в данных установщика; читаем файл целиком (инсталляторы небольшие).
            byte[] bytes = File.ReadAllBytes(path);
            string ascii = System.Text.Encoding.ASCII.GetString(bytes);
            if (ascii.Contains("Inno Setup Setup Data")) return InstallerEngine.InnoSetup;
            if (ascii.Contains("Nullsoft Install System")) return InstallerEngine.Nsis;
        }
        catch { }
        return InstallerEngine.Unknown;
    }

    /// <summary>Аргументы тихой установки для известных движков (null — тихий режим недоступен).</summary>
    public static string? SilentArgsFor(InstallerEngine engine) => engine switch
    {
        InstallerEngine.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        InstallerEngine.Nsis => "/S",
        _ => null
    };

    /// <summary>
    /// Запускает инсталлятор с повышением прав. При <paramref name="silent"/> и известном
    /// движке ставит драйвер без мастера (останется только запрос UAC). Возвращает код
    /// выхода процесса, либо бросает <see cref="OperationCanceledException"/>, если
    /// пользователь отклонил запрос UAC.
    /// </summary>
    public static async Task<int> InstallAsync(DriverPackage pkg, bool silent = false)
    {
        if (!File.Exists(pkg.FullPath))
            throw new FileNotFoundException("Инсталлятор не найден.", pkg.FullPath);

        var psi = new ProcessStartInfo
        {
            FileName = pkg.FullPath,
            Arguments = silent ? SilentArgsFor(DetectEngine(pkg.FullPath)) ?? "" : "",
            WorkingDirectory = DriversFolder,
            UseShellExecute = true,   // обязательно для verb="runas"
            Verb = "runas"            // запрос прав администратора (UAC)
        };

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить процесс установки.");
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — пользователь нажал «Нет» в окне UAC.
            throw new OperationCanceledException("Установка отменена пользователем (UAC).", ex);
        }
    }
}
