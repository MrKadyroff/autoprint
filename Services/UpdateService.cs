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

        // Найти папку с AutoPrint.exe (zip может иметь верхнюю папку).
        string srcDir = Directory.EnumerateFiles(extract, "AutoPrint.exe", SearchOption.AllDirectories)
                            .Select(Path.GetDirectoryName).FirstOrDefault() ?? extract;
        string dstDir = AppContext.BaseDirectory.TrimEnd('\\');

        string script = Path.Combine(tmpDir, "apply_update.cmd");
        int pid = Environment.ProcessId;
        File.WriteAllText(script, BuildUpdaterScript(pid, srcDir!, dstDir, script), Encoding.Default);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    /// <summary>Batch-апдейтер: ждёт завершения процесса по PID, копирует файлы, перезапускает.</summary>
    private static string BuildUpdaterScript(int pid, string srcDir, string dstDir, string selfPath) => $"""
        @echo off
        setlocal
        :waitloop
        tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
        if not errorlevel 1 (
            ping -n 2 127.0.0.1 >nul
            goto waitloop
        )
        ping -n 2 127.0.0.1 >nul
        xcopy /E /Y /I "{srcDir}\*" "{dstDir}\" >nul
        start "" "{dstDir}\AutoPrint.exe"
        rmdir /S /Q "{Path.GetDirectoryName(srcDir)}" >nul 2>&1
        del "%~f0" >nul 2>&1
        """;
}
