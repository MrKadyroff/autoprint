using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AutoPrint.Services;

/// <summary>Сведения о доступном релизе.</summary>
public class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0, 0);
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string AssetUrl { get; init; } = "";
    public bool IsZip => AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public bool HasAsset => !string.IsNullOrEmpty(AssetUrl);
}

/// <summary>
/// Автообновление через GitHub Releases:
///  1. GET api.github.com/repos/{owner}/{repo}/releases/latest
///  2. Сравнение версии тега (v1.2.3) с текущей версией сборки.
///  3. Скачивание ассета (.zip — портативная сборка, либо .exe — инсталлятор).
///  4. Установка: для .zip распаковка + скрипт-апдейтер (ждёт выхода приложения,
///     копирует файлы, перезапускает); для .exe — запуск инсталлятора.
/// </summary>
public class UpdateService
{
    // Координаты репозитория обновлений зашиты жёстко — приложение всегда обновляется
    // из официального релиза, настройка пользователем не предусмотрена.
    private const string Owner = "MrKadyroff";
    private const string Repo = "autoprint";

    private readonly AppConfig _cfg;
    private static readonly HttpClient Http = CreateClient();

    public UpdateService(AppConfig cfg) => _cfg = cfg;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        // GitHub API требует User-Agent, иначе 403.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoPrint", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    /// <summary>Запрашивает последний релиз. Возвращает info даже если версия не новее (см. IsNewer).</summary>
    public async Task<UpdateInfo> CheckAsync()
    {
        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        string json = await Http.GetStringAsync(url);
        return ParseRelease(json);
    }

    public static bool IsNewer(UpdateInfo info) => info.Version > CurrentVersion;

    /// <summary>Разбор JSON релиза GitHub во внутреннюю модель (вынесен для тестируемости).</summary>
    public static UpdateInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        // Выбор ассета: приоритет .zip (портативная сборка), затем .exe (инсталлятор).
        string assetName = "", assetUrl = "";
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            JsonElement? zip = null, exe = null;
            foreach (var a in assets.EnumerateArray())
            {
                string an = a.TryGetProperty("name", out var ae) ? ae.GetString() ?? "" : "";
                if (an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip ??= a; }
                else if (an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { exe ??= a; }
            }
            var chosen = zip ?? exe;
            if (chosen is { } c)
            {
                assetName = c.GetProperty("name").GetString() ?? "";
                assetUrl = c.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            }
        }

        return new UpdateInfo
        {
            Tag = tag,
            Name = string.IsNullOrWhiteSpace(name) ? tag : name,
            Notes = notes,
            Version = ParseVersion(tag),
            AssetName = assetName,
            AssetUrl = assetUrl
        };
    }

    /// <summary>Нормализует тег "v1.2.3" / "1.2.3-beta" → Version.</summary>
    public static Version ParseVersion(string tag)
    {
        string s = (tag ?? "").Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });   // отбросить префикс пре-релиза/метаданных
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }

    /// <summary>Скачивает ассет с отчётом прогресса (0..1).</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        if (!info.HasAsset) throw new InvalidOperationException("У релиза нет подходящего ассета (.zip/.exe).");

        string tmpDir = Path.Combine(Path.GetTempPath(), "AutoPrint_update");
        Directory.CreateDirectory(tmpDir);
        CleanupOldDownloads(tmpDir, info.AssetName);
        string dest = Path.Combine(tmpDir, info.AssetName);

        using var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long read = 0; int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is > 0) progress.Report((double)read / total.Value);
        }
        progress.Report(1.0);
        return dest;
    }

    /// <summary>
    /// Удаляет архивы прошлых обновлений. Self-contained сборка весит десятки мегабайт,
    /// и без уборки %TEMP% на кассе постепенно забивался копиями каждого релиза.
    /// </summary>
    private static void CleanupOldDownloads(string tmpDir, string keepName)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(tmpDir))
                if (!string.Equals(Path.GetFileName(f), keepName, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { /* занят — удалится в следующий раз */ }
        }
        catch { /* уборка не должна мешать обновлению */ }
    }

    /// <summary>
    /// Применяет скачанный ассет и перезапускает приложение.
    /// Для .exe — просто запускает инсталлятор. Для .zip — распаковывает и запускает
    /// скрипт-апдейтер, который дождётся выхода приложения и подменит файлы.
    /// Вызывающий код должен после этого завершить приложение.
    /// </summary>
    public void ApplyAndRestart(UpdateInfo info, string downloadedPath)
    {
        if (info.IsZip) ApplyZip(downloadedPath);
        else RunInstaller(downloadedPath);
    }

    private static void RunInstaller(string exePath)
    {
        Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
    }

    private void ApplyZip(string zipPath)
    {
        string tmpDir = Path.GetDirectoryName(zipPath)!;
        string extract = Path.Combine(tmpDir, "extracted");
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(zipPath, extract);

        // Найти папку с AutoPrint.exe (zip может иметь верхнюю папку, напр. dist\).
        string? srcDir = Directory.EnumerateFiles(extract, "AutoPrint.exe", SearchOption.AllDirectories)
                            .Select(Path.GetDirectoryName).FirstOrDefault();
        if (srcDir is null)
            throw new InvalidOperationException("В архиве обновления не найден AutoPrint.exe.");

        string dstDir = AppContext.BaseDirectory.TrimEnd('\\');

        // Проверяем права ДО выхода из приложения: иначе апдейтер молча не скопирует
        // файлы (напр. установка в Program Files), приложение перезапустится со старой
        // версией, и пользователь увидит «обновление не ставится».
        EnsureWritable(dstDir);

        string log = Path.Combine(tmpDir, "apply_update.log");
        string script = Path.Combine(tmpDir, "apply_update.cmd");
        int pid = Environment.ProcessId;
        // Сохраняем аргументы запуска (в частности --minimized): без них тихое фоновое
        // обновление вытаскивало окно приложения поверх кассового ПО.
        string args = string.Join(' ', Environment.GetCommandLineArgs().Skip(1)
                                          .Where(a => !a.Contains('"')));
        File.WriteAllText(script, BuildUpdaterScript(pid, srcDir, dstDir, log, extract, args),
                          new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    /// <summary>Бросает понятную ошибку, если в папку приложения нельзя писать.</summary>
    private static void EnsureWritable(string dir)
    {
        string probe = Path.Combine(dir, ".update_probe.tmp");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"Нет прав на запись в папку приложения «{dir}» — обновление невозможно. " +
                $"Запустите AutoPrint от имени администратора или установите его в папку пользователя. ({ex.Message})");
        }
    }

    /// <summary>
    /// Batch-апдейтер: ждёт завершения процесса по PID, копирует файлы, перезапускает.
    /// Пишет лог рядом со скриптом — без него неудачное копирование выглядит как
    /// «обновилось, но версия та же».
    /// </summary>
    private static string BuildUpdaterScript(int pid, string srcDir, string dstDir, string logPath,
                                             string extractRoot, string appArgs) => $"""
        @echo off
        chcp 65001 >nul
        setlocal
        set LOG="{logPath}"
        echo [%date% %time%] updater start pid={pid} > %LOG%
        set /a tries=0
        :waitloop
        tasklist /FI "PID eq {pid}" /NH 2>nul | find "{pid}" >nul
        if errorlevel 1 goto copy
        set /a tries+=1
        if %tries% GTR 60 (
            echo [%date% %time%] ОШИБКА: процесс {pid} не завершился за 60 c >> %LOG%
            goto restart
        )
        ping -n 2 127.0.0.1 >nul
        goto waitloop
        :copy
        ping -n 2 127.0.0.1 >nul
        echo [%date% %time%] copy "{srcDir}" -^> "{dstDir}" >> %LOG%
        robocopy "{srcDir}" "{dstDir}" /E /R:5 /W:1 /NFL /NDL /NJH /NJS >> %LOG% 2>&1
        rem robocopy: код ^>= 8 — реальная ошибка, 0..7 — успех
        if errorlevel 8 (
            echo [%date% %time%] ОШИБКА копирования, код %errorlevel% >> %LOG%
        ) else (
            echo [%date% %time%] копирование ок, код %errorlevel% >> %LOG%
        )
        :restart
        echo [%date% %time%] restart >> %LOG%
        start "" "{dstDir}\AutoPrint.exe" {appArgs}
        rmdir /S /Q "{extractRoot}" >nul 2>&1
        del "%~f0" >nul 2>&1
        """;
}
