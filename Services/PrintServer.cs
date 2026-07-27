using System.IO;
using System.Net;
using System.Text;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>
/// Лёгкий HTTP-сервер приёма чеков из браузера/фронтенда на базе HttpListener.
/// Перезапускается на лету (смена порта без рестарта приложения).
///
/// Контракт:
///  POST /print
///    - Content-Type: application/json         -> ReceiptFormat.Json  (тело = JSON шаблон)
///    - Content-Type: text/html                -> ReceiptFormat.Html  (тело = HTML)
///    - Content-Type: image/png | image/jpeg   -> ReceiptFormat.Image (тело = байты картинки)
///  GET /status -> "OK" (health-check / проверка что порт слушается)
/// </summary>
public class PrintServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public int Port { get; private set; }
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Событие поднимается при поступлении корректного задания печати.</summary>
    public event Action<PrintJob>? JobReceived;
    /// <summary>Диагностические сообщения (для лога в UI).</summary>
    public event Action<string>? Log;

    public void Start(int port)
    {
        Stop(); // освободить старый порт, если был

        Port = port;
        _listener = new HttpListener();
        // '+' — принимать на всех интерфейсах. Требует урл-резервации либо запуска от админа.
        _listener.Prefixes.Add($"http://+:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Фолбэк на localhost, если нет прав на биндинг ко всем интерфейсам.
            Log?.Invoke($"Не удалось слушать 0.0.0.0:{port} ({ex.Message}). Пробую localhost.");
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke($"Сервер слушает порт {port}.");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            if (_listener?.IsListening == true) _listener.Stop();
            _listener?.Close();
        }
        catch { /* игнорируем при остановке */ }
        finally
        {
            _listener = null;
            _cts = null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex)
            {
                // Останавливаемся только если listener действительно закрыт;
                // одиночная сетевая ошибка не должна убивать приём навсегда.
                if (ct.IsCancellationRequested || _listener is not { IsListening: true }) break;
                Log?.Invoke($"Ошибка приёма подключения: {ex.Message}");
                FileLog.Error("PrintServer.AcceptLoop", ex);
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        // CORS — чтобы браузерный фронтенд мог слать fetch() напрямую.
        // Фронт шлёт запрос с credentials:"include" и заголовками authorization/auth2.
        // При credentials браузер ЗАПРЕЩАЕТ ответ с "*": нужно вернуть конкретный Origin
        // и Access-Control-Allow-Credentials: true. Заголовки в preflight — отражаем
        // те, что запросил браузер (Access-Control-Request-Headers), иначе OPTIONS падает.
        string origin = req.Headers["Origin"] ?? "";
        if (origin.Length > 0)
        {
            res.AddHeader("Access-Control-Allow-Origin", origin);
            res.AddHeader("Access-Control-Allow-Credentials", "true");
            res.AddHeader("Vary", "Origin");
        }
        else
        {
            res.AddHeader("Access-Control-Allow-Origin", "*");
        }
        string reqHeaders = req.Headers["Access-Control-Request-Headers"] ?? "";
        res.AddHeader("Access-Control-Allow-Headers",
            reqHeaders.Length > 0 ? reqHeaders : "Content-Type, Authorization, auth2");
        res.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
        res.AddHeader("Access-Control-Max-Age", "600");

        try
        {
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; return; }

            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/status")
            {
                await WriteText(res, 200, "OK");
                return;
            }

            // Фронт вызывает printerAgent.autoPrint({fileBase64}); принимаем на любом из путей.
            string path = (req.Url?.AbsolutePath ?? "").TrimEnd('/').ToLowerInvariant();
            if (req.HttpMethod == "POST" && path is "/print" or "/autoprint" or "")
            {
                var job = await ReadJob(req);
                JobReceived?.Invoke(job);
                await WriteText(res, 200, "{\"status\":\"accepted\"}");
                return;
            }

            await WriteText(res, 404, "Not Found");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Ошибка обработки запроса: {ex.Message}");
            try { await WriteText(res, 500, "Server Error"); } catch { }
        }
        finally
        {
            res.Close();
        }
    }

    private static async Task<PrintJob> ReadJob(HttpListenerRequest req)
    {
        string ct = (req.ContentType ?? "").ToLowerInvariant();

        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms);
        byte[] body = ms.ToArray();

        // Бинарная картинка напрямую (Content-Type: image/*)
        if (ct.Contains("image/"))
            return new PrintJob { Format = DetectBinaryFormat(body), RawImage = body, Source = "network" };

        string text = (req.ContentEncoding ?? Encoding.UTF8).GetString(body);

        // Конверт фронта: { "fileBase64": "<PDF|PNG в base64>", "printer": "...", "kind": "shift_open" }
        if (TryExtractFileBase64(text, out byte[] fileBytes, out string kind, out string printer))
        {
            var fmt = DetectBinaryFormat(fileBytes);
            // Если явного kind нет, а файл — PDF с текстовым слоем, сканируем его содержимое
            // (например, "закрытие смены" / "Z-отчёт"), чтобы распознать категорию чека.
            string scanText = "";
            if (string.IsNullOrWhiteSpace(kind) && fmt == ReceiptFormat.Pdf)
                scanText = PdfRenderer.ExtractText(fileBytes);
            return new PrintJob
            {
                Format = fmt, RawImage = fileBytes, Source = "autoPrint",
                PrinterName = printer,
                Kind = DetectKind(kind, scanText)
            };
        }

        // Иначе — как раньше: HTML или JSON-шаблон.
        var format = ct.Contains("text/html") ? ReceiptFormat.Html : ReceiptFormat.Json;
        return new PrintJob
        {
            Format = format, Payload = text, Source = "network",
            PrinterName = ExtractPrinter(text),
            Kind = DetectKind(null, text)     // сканируем содержимое на ключевые слова смены
        };
    }

    /// <summary>Достаёт имя принтера ("printer"/"printerName") из JSON, если это JSON-конверт.</summary>
    private static string ExtractPrinter(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{")) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var pk in new[] { "printer", "printerName", "printer_name" })
                if (doc.RootElement.TryGetProperty(pk, out var pe)
                    && pe.ValueKind == System.Text.Json.JsonValueKind.String)
                    return pe.GetString()?.Trim() ?? "";
        }
        catch { /* не JSON */ }
        return "";
    }

    /// <summary>Достаёт fileBase64 (или file/base64/data), kind/type и printer из JSON.</summary>
    private static bool TryExtractFileBase64(string json, out byte[] bytes, out string kind, out string printer)
    {
        bytes = Array.Empty<byte>();
        kind = "";
        printer = "";
        if (string.IsNullOrWhiteSpace(json) || !json.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var kk in new[] { "kind", "type", "docType", "receiptKind" })
                if (root.TryGetProperty(kk, out var ke) && ke.ValueKind == System.Text.Json.JsonValueKind.String)
                { kind = ke.GetString() ?? ""; break; }

            foreach (var pk in new[] { "printer", "printerName", "printer_name" })
                if (root.TryGetProperty(pk, out var pe) && pe.ValueKind == System.Text.Json.JsonValueKind.String)
                { printer = pe.GetString()?.Trim() ?? ""; break; }

            foreach (var key in new[] { "fileBase64", "file", "base64", "data", "image", "pdf" })
            {
                if (root.TryGetProperty(key, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string b64 = el.GetString() ?? "";
                    int comma = b64.IndexOf("base64,", StringComparison.Ordinal);   // срезать data:...;base64,
                    if (comma >= 0) b64 = b64[(comma + 7)..];
                    bytes = Convert.FromBase64String(b64.Trim());
                    return bytes.Length > 0;
                }
            }
        }
        catch { /* не наш конверт */ }
        return false;
    }

    /// <summary>Определяет категорию чека по явному полю kind или по ключевым словам (RU/KZ).</summary>
    private static ReceiptKind DetectKind(string? explicitKind, string textForScan)
    {
        string k = (explicitKind ?? "").ToLowerInvariant();
        if (k.Length > 0)
        {
            if (k.Contains("open") || k.Contains("откр") || k.Contains("ашу")) return ReceiptKind.ShiftOpen;
            if (k.Contains("close") || k.Contains("закр") || k.Contains("жабу") || k == "z" || k.Contains("z-")) return ReceiptKind.ShiftClose;
            if (k.Contains("deposit") || k.Contains("внес") || k.Contains("попол") || k.Contains("енгізу")) return ReceiptKind.Deposit;
            if (k.Contains("withdraw") || k.Contains("изъят") || k.Contains("сня") || k.Contains("алу")) return ReceiptKind.Withdrawal;
            if (k.Contains("report") || k.Contains("отч") || k == "x" || k.Contains("x-")) return ReceiptKind.Report;
        }

        string t = (textForScan ?? "").ToLowerInvariant();
        if (t.Length > 0)
        {
            if (t.Contains("закрытие смены") || t.Contains("z-отч") || t.Contains("z отч")) return ReceiptKind.ShiftClose;
            if (t.Contains("открытие смены")) return ReceiptKind.ShiftOpen;
            if (t.Contains("внесение") || t.Contains("пополнение")) return ReceiptKind.Deposit;
            if (t.Contains("изъятие") || t.Contains("снятие")) return ReceiptKind.Withdrawal;
            if (t.Contains("x-отч") || t.Contains("x отч")) return ReceiptKind.Report;
        }
        return ReceiptKind.Normal;
    }

    /// <summary>Определяет формат по сигнатуре файла (magic bytes).</summary>
    private static ReceiptFormat DetectBinaryFormat(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) // %PDF
            return ReceiptFormat.Pdf;
        // PNG (89 50 4E 47) и JPEG (FF D8) → растр
        return ReceiptFormat.Image;
    }

    private static async Task WriteText(HttpListenerResponse res, int code, string body)
    {
        res.StatusCode = code;
        res.ContentType = body.StartsWith("{") ? "application/json" : "text/plain";
        byte[] b = Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
    }
}
