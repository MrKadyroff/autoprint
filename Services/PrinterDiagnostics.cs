using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Printing;
using AutoPrint.Models;

namespace AutoPrint.Services;

public enum StepStatus { Running, Ok, Warn, Error, Info }

/// <summary>Один шаг диагностики (для прогрессивного вывода в UI).</summary>
public class DiagnosticStep
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public StepStatus Status { get; set; } = StepStatus.Info;

    public string Icon => Status switch
    {
        StepStatus.Ok => "✓",
        StepStatus.Warn => "⚠",
        StepStatus.Error => "✕",
        StepStatus.Running => "…",
        _ => "•"
    };
}

/// <summary>Итог диагностики: рекомендованная цель + собранные факты.</summary>
public class DiagnosticReport
{
    public PrintTarget? Recommended { get; set; }
    public bool DriverInstalled { get; set; }
    public List<string> NetworkHits { get; } = new();
    public string Summary { get; set; } = "";
}

/// <summary>
/// Определяет подключённый термопринтер по USB (спулер Windows) и по сети (RJ45 → TCP 9100),
/// проверяет статус очереди и выдаёт рекомендованную цель печати с автоподключением.
/// </summary>
public static class PrinterDiagnostics
{
    private const int RawPort = RawNetworkPrinter.DefaultPort;   // 9100

    // Только явные признаки чекового термопринтера. Общие слова вроде "printer" или "80"
    // намеренно исключены — под них подходит почти любой офисный принтер, а нам нужно
    // выбирать именно Mulex/термопринтер и не трогать остальные устройства пользователя.
    private static readonly string[] ThermalHints =
        { "mulex", "xprinter", "gprinter", "rongta", "epson tm", "star tsp",
          "pos80", "pos-80", "pos58", "pos-58", "80mm", "58mm", "thermal", "receipt", "esc/pos" };

    /// <summary>true, если имя принтера похоже на чековый термопринтер (Mulex и т.п.).</summary>
    public static bool IsLikelyReceiptPrinter(string printerName) =>
        ThermalHints.Any(h => printerName.ToLowerInvariant().Contains(h));

    /// <summary>Запускает полную диагностику, публикуя шаги через <paramref name="progress"/>.</summary>
    public static async Task<DiagnosticReport> RunAsync(IProgress<DiagnosticStep> progress)
    {
        var report = new DiagnosticReport();

        // --- 1. Драйвер / спулер: перечисление принтеров и их статус ---
        Report(progress, "Поиск принтеров в системе (USB/спулер)…", StepStatus.Running);
        var spoolerCandidates = InspectSpooler(progress, report);

        // --- 2. Сеть: определить локальные подсети и просканировать порт 9100 ---
        Report(progress, "Сканирование локальной сети на порт 9100 (RAW/RJ45)…", StepStatus.Running);
        var netHits = await ScanNetworkAsync(progress);
        report.NetworkHits.AddRange(netHits.Select(h => h.host));

        // --- 3. Выбор рекомендованной цели ---
        // Подтверждённые ESC/POS-статусом адреса — в приоритете (это точно чековый принтер,
        // а не случайный МФУ на том же порту).
        var orderedHits = netHits.OrderByDescending(h => h.looksReceipt).Select(h => h.host).ToList();
        Decide(progress, report, spoolerCandidates, orderedHits);

        return report;
    }

    // ---------- USB / спулер ----------
    private static List<(string name, PrintTarget target)> InspectSpooler(
        IProgress<DiagnosticStep> progress, DiagnosticReport report)
    {
        var candidates = new List<(string, PrintTarget)>();
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues();
            int count = 0;

            foreach (var q in queues)
            {
                count++;
                string port = "";
                try { q.Refresh(); port = q.QueuePort?.Name ?? ""; } catch { }

                bool thermal = ThermalHints.Any(h => q.Name.ToLowerInvariant().Contains(h));
                string portKind = ClassifyPort(port);

                var status = new List<string>();
                if (q.IsOffline) status.Add("оффлайн");
                if (q.IsOutOfPaper) status.Add("нет бумаги");
                if (q.IsPaperJammed) status.Add("замятие");
                if (q.IsInError) status.Add("ошибка");
                string statusText = status.Count == 0 ? "готов" : string.Join(", ", status);

                var st = q.IsOffline || q.IsInError ? StepStatus.Warn
                        : thermal ? StepStatus.Ok : StepStatus.Info;

                Report(progress, $"Принтер: {q.Name}",
                    $"порт {port} ({portKind}); статус: {statusText}" + (thermal ? "; похоже на термопринтер" : ""),
                    st);

                // В кандидаты на автоподключение попадает только то, что реально похоже на
                // чековый термопринтер по имени. Обычный офисный принтер на USB/сети — не наш
                // случай, даже если формально доступен: не трогаем чужие настройки печати.
                if (thermal)
                {
                    var tgt = new PrintTarget { Kind = ConnectionKind.WindowsSpooler, PrinterName = q.Name };
                    candidates.Add((q.Name, tgt));
                }
            }

            report.DriverInstalled = ThermalHints.Any(h => queues.Any(x => x.Name.ToLowerInvariant().Contains(h)));

            if (count == 0)
                Report(progress, "В спулере нет ни одного принтера.", "Возможно, драйвер не установлен.", StepStatus.Warn);
        }
        catch (Exception ex)
        {
            Report(progress, "Не удалось опросить спулер печати.", ex.Message, StepStatus.Error);
        }
        return candidates;
    }

    private static string ClassifyPort(string port)
    {
        string p = port.ToUpperInvariant();
        if (p.StartsWith("USB")) return "USB";
        if (p.StartsWith("COM")) return "COM (serial)";
        if (p.StartsWith("LPT")) return "LPT";
        if (p.Contains("IP_") || p.Contains("WSD") || System.Net.IPAddress.TryParse(port, out _)) return "network";
        return "иной";
    }

    /// <summary>
    /// Быстрый повторный поиск сетевого принтера по всем локальным подсетям (без прогресса в UI).
    /// Нужен, когда принтер по DHCP сменил IP и сохранённый адрес перестал отвечать —
    /// чтобы «бесперебойный» сервер сам нашёл принтер заново, а не падал в очереди насовсем.
    /// </summary>
    public static async Task<string?> RescanForHostAsync(int port = RawPort)
    {
        string? fallback = null;
        foreach (var (baseAddr, _) in LocalIpv4Bases())
        {
            var tasks = new List<Task<string?>>();
            for (int host = 1; host <= 254; host++)
            {
                string ip = $"{baseAddr}.{host}";
                tasks.Add(RawNetworkPrinter.ProbeAsync(ip, port, 300).ContinueWith(t => t.Result ? ip : null));
            }
            var open = (await Task.WhenAll(tasks)).Where(h => h is not null).Select(h => h!).ToList();

            // Порт 9100 бывает открыт и у посторонних устройств (сетевые МФУ и т.п.), которые
            // молча принимают байты и вешают печать. Берём того, кто отвечает на ESC/POS-статус.
            foreach (var ip in open)
                if (await RawNetworkPrinter.LooksLikeEscPosAsync(ip, port)) return ip;

            fallback ??= open.FirstOrDefault();
        }
        return fallback;
    }

    // ---------- Сеть ----------
    private static async Task<List<(string host, bool looksReceipt)>> ScanNetworkAsync(IProgress<DiagnosticStep> progress)
    {
        var hits = new List<(string, bool)>();
        var bases = LocalIpv4Bases();
        if (bases.Count == 0)
        {
            Report(progress, "Активных сетевых интерфейсов не найдено.", "", StepStatus.Warn);
            return hits;
        }

        foreach (var (baseAddr, self) in bases)
        {
            Report(progress, $"Сканирую подсеть {baseAddr}.0/24 …", $"мой адрес {self}", StepStatus.Running);

            var tasks = new List<Task<string?>>();
            for (int host = 1; host <= 254; host++)
            {
                string ip = $"{baseAddr}.{host}";
                tasks.Add(ProbeHost(ip));
            }

            foreach (var found in await Task.WhenAll(tasks))
                if (found is not null)
                {
                    // Порт 9100 держат не только чековые принтеры (у офисных МФУ он тоже есть) —
                    // best-effort ESC/POS-запрос статуса помогает отличить своё от чужого.
                    // Не жёсткий фильтр: не все модели отвечают на статус, даже будучи ESC/POS.
                    bool looksReceipt = await RawNetworkPrinter.LooksLikeEscPosAsync(found, RawPort);
                    hits.Add((found, looksReceipt));
                    Report(progress, $"Найден сетевой принтер: {found}:{RawPort}",
                        looksReceipt
                            ? "Открыт RAW-порт 9100, ответил на ESC/POS-статус — похоже на чековый принтер."
                            : "Открыт RAW-порт 9100. На ESC/POS-статус не ответил — возможно, это не чековый " +
                              "принтер (например, обычный сетевой МФУ), проверьте адрес.",
                        looksReceipt ? StepStatus.Ok : StepStatus.Warn);
                }
        }

        if (hits.Count == 0)
            Report(progress, "Сетевых принтеров на порту 9100 не обнаружено.",
                "Проверьте, что принтер включён и в той же подсети.", StepStatus.Info);

        return hits;

        static async Task<string?> ProbeHost(string ip)
            => await RawNetworkPrinter.ProbeAsync(ip, RawPort, 350) ? ip : null;
    }

    /// <summary>Базовые адреса /24 (первые три октета) активных IPv4-интерфейсов.</summary>
    private static List<(string baseAddr, string self)> LocalIpv4Bases()
    {
        var result = new List<(string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var octets = ua.Address.GetAddressBytes();
                // 169.254.x.x — APIPA (адаптер поднят, но DHCP не ответил). Такая /24 не
                // соответствует реальной подсети: сканировать её бессмысленно, а стоит это
                // 254 лишних probe на каждый адаптер без DHCP.
                if (octets[0] == 169 && octets[1] == 254) continue;

                string baseAddr = $"{octets[0]}.{octets[1]}.{octets[2]}";
                if (result.All(r => r.Item1 != baseAddr))
                    result.Add((baseAddr, ua.Address.ToString()));
            }
        }
        return result;
    }

    // ---------- Решение / рекомендация ----------
    private static void Decide(IProgress<DiagnosticStep> progress, DiagnosticReport report,
        List<(string name, PrintTarget target)> spooler, List<string> netHits)
    {
        // Приоритет 1: сетевой принтер (RJ45) — работает без драйвера и стабильнее для сервера.
        if (netHits.Count > 0)
        {
            report.Recommended = new PrintTarget { Kind = ConnectionKind.Network, Host = netHits[0], Port = RawPort };
            report.Summary = $"Рекомендуется прямое сетевое подключение к {netHits[0]}:{RawPort} (RJ45, без драйвера).";
            Report(progress, "РЕШЕНИЕ: подключаюсь по сети (RJ45).", report.Summary, StepStatus.Ok);
            return;
        }

        // Приоритет 2: рабочий принтер в спулере (USB).
        if (spooler.Count > 0)
        {
            var best = spooler[0];
            report.Recommended = best.target;
            report.Summary = $"Рекомендуется печать через спулер: «{best.name}» (USB).";
            Report(progress, "РЕШЕНИЕ: подключаюсь через USB/спулер.", report.Summary, StepStatus.Ok);
            return;
        }

        // Ничего не найдено.
        if (!report.DriverInstalled)
        {
            report.Summary = "Принтер не найден и драйвер не установлен. Установите драйвер (панель «Драйвер принтера»), " +
                             "затем подключите USB-кабель или проверьте RJ45.";
            Report(progress, "РЕШЕНИЕ: требуется установка драйвера.", report.Summary, StepStatus.Warn);
        }
        else
        {
            report.Summary = "Драйвер есть, но активный принтер не обнаружен. Проверьте питание и кабель (USB/RJ45).";
            Report(progress, "РЕШЕНИЕ: проверьте физическое подключение.", report.Summary, StepStatus.Warn);
        }
    }

    // ---------- helpers ----------
    private static void Report(IProgress<DiagnosticStep> p, string title, StepStatus st)
        => p.Report(new DiagnosticStep { Title = title, Status = st });

    private static void Report(IProgress<DiagnosticStep> p, string title, string detail, StepStatus st)
        => p.Report(new DiagnosticStep { Title = title, Detail = detail, Status = st });
}
