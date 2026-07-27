using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AutoPrint.Services;

/// <summary>
/// Регистрирует сетевой чековый принтер в Windows штатными средствами:
/// Standard TCP/IP-порт в режиме RAW (9100) + встроенный драйвер «Generic / Text Only».
/// Отдельный вендорский драйвер для этого не нужен — Generic/Text Only отдаёт байты
/// (ESC/POS) на порт как есть. После регистрации принтер видят и другие программы.
/// Требует прав администратора, поэтому исполняется отдельным повышенным PowerShell.
/// </summary>
public static class WindowsPrinterRegistrar
{
    public const string GenericDriver = "Generic / Text Only";

    /// <summary>Имя, под которым принтер появится в Windows.</summary>
    public static string PrinterNameFor(string host) => $"AutoPrint POS ({host})";

    /// <summary>Проверяет, зарегистрирован ли уже такой принтер (без повышения прав).</summary>
    public static bool IsRegistered(string host)
    {
        try
        {
            return RawPrinterHelper.InstalledPrinters()
                .Any(p => p.Equals(PrinterNameFor(host), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Переименовывает установленный принтер в желаемое имя (напр. «Mulex P80mm»),
    /// чтобы фронт мог адресовать печать по стабильному имени. Требует прав администратора
    /// (Rename-Printer). Возвращает true при успехе; бросает
    /// <see cref="OperationCanceledException"/> при отказе UAC.
    /// </summary>
    public static async Task<bool> RenamePrinterAsync(string currentName, string newName)
    {
        if (string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase)) return true;

        // Если целевое имя уже занято другим принтером — не трогаем, считаем что всё готово.
        string script = $@"
$ErrorActionPreference = 'Stop'
try {{
    if (Get-Printer -Name '{newName}' -ErrorAction SilentlyContinue) {{ exit 0 }}
    Rename-Printer -Name '{currentName}' -NewName '{newName}'
    exit 0
}} catch {{
    $_ | Out-String | Set-Content -Path '{Path.Combine(FileLog.LogDir, "printer-rename-error.log")}'
    exit 1
}}";
        string scriptPath = Path.Combine(Path.GetTempPath(), $"autoprint-rename-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Переименование принтера отменено (UAC).", ex);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Создаёт порт и принтер. Возвращает true при успехе; бросает
    /// <see cref="OperationCanceledException"/>, если пользователь отклонил UAC.
    /// </summary>
    public static async Task<bool> RegisterAsync(string host, int port = 9100)
    {
        string printer = PrinterNameFor(host);
        string portName = $"IP_{host}";

        // Скрипт кладём во временный файл — так не воюем с экранированием кавычек
        // и получаем код выхода, по которому судим об успехе.
        string script = $@"
$ErrorActionPreference = 'Stop'
try {{
    if (-not (Get-PrinterPort -Name '{portName}' -ErrorAction SilentlyContinue)) {{
        Add-PrinterPort -Name '{portName}' -PrinterHostAddress '{host}' -PortNumber {port}
    }}
    if (-not (Get-Printer -Name '{printer}' -ErrorAction SilentlyContinue)) {{
        Add-Printer -Name '{printer}' -DriverName '{GenericDriver}' -PortName '{portName}'
    }}
    exit 0
}} catch {{
    $_ | Out-String | Set-Content -Path '{Path.Combine(FileLog.LogDir, "printer-register-error.log")}'
    exit 1
}}";
        string scriptPath = Path.Combine(Path.GetTempPath(), $"autoprint-register-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить PowerShell.");
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Регистрация принтера отменена (UAC).", ex);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}
