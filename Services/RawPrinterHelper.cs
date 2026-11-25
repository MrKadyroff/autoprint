using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoPrint.Services;

/// <summary>
/// Отправка сырых байтов напрямую в спулер принтера (datatype "RAW"),
/// минуя GDI. Так термопринтер получает ESC/POS команды без интерпретации
/// драйвером Windows. Реализовано через winspool.drv (Win32 Spooler API).
/// </summary>
public static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName = "";
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType = "RAW";
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);   // возвращает id задания, 0 = ошибка

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int count, out int written);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool SetJob(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int command);

    private const int JOB_CONTROL_DELETE = 5;

    /// <summary>Размер одной записи WritePrinter: чтобы проверять отмену и учёт байт по ходу, а не одним куском.</summary>
    private const int ChunkSize = 4096;

    /// <summary>
    /// Отправляет массив байтов на указанный принтер как один RAW-документ.
    /// При отмене <paramref name="ct"/> (тайм-аут очереди) задание удаляется из спулера —
    /// это прерывает зависший WritePrinter, и следующий чек не встанет поверх недописанного.
    /// </summary>
    public static bool SendBytes(string printerName, byte[] bytes, string docName = "AutoPrint Receipt",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            throw new IOException($"Не удалось открыть принтер '{printerName}'. Код: {Marshal.GetLastWin32Error()}");

        IntPtr pUnmanaged = IntPtr.Zero;
        bool docOpen = false;
        CancellationTokenRegistration reg = default;
        try
        {
            var di = new DOCINFOA { pDocName = docName, pDataType = "RAW" };
            int jobId = StartDocPrinter(hPrinter, 1, di);
            if (jobId == 0)
                throw new IOException($"StartDocPrinter failed. Код: {Marshal.GetLastWin32Error()}");
            docOpen = true;

            // Удаляем через отдельный дескриптор: основной в этот момент занят WritePrinter.
            reg = ct.Register(() => DeleteJob(printerName, jobId));

            if (!StartPagePrinter(hPrinter))
                throw new IOException($"StartPagePrinter failed. Код: {Marshal.GetLastWin32Error()}");

            pUnmanaged = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, pUnmanaged, bytes.Length);

            int total = 0;
            while (total < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(ChunkSize, bytes.Length - total);
                if (!WritePrinter(hPrinter, pUnmanaged + total, n, out int written) || written <= 0)
                    break;
                total += written;
            }
            ct.ThrowIfCancellationRequested();

            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
            docOpen = false;
            return total == bytes.Length;
        }
        finally
        {
            reg.Dispose();   // дожидается DeleteJob, если он уже выполняется
            if (docOpen) EndDocPrinter(hPrinter);
            if (pUnmanaged != IntPtr.Zero) Marshal.FreeCoTaskMem(pUnmanaged);
            ClosePrinter(hPrinter);
        }
    }

    private static void DeleteJob(string printerName, int jobId)
    {
        try
        {
            if (!OpenPrinter(printerName, out IntPtr h, IntPtr.Zero)) return;
            try
            {
                bool ok = SetJob(h, jobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE);
                FileLog.Info($"[PRINT] Отмена: задание спулера #{jobId} удалено={ok}");
            }
            finally { ClosePrinter(h); }
        }
        catch (Exception ex) { FileLog.Error("RawPrinterHelper.DeleteJob", ex); }
    }

    /// <summary>Удобная перегрузка для отправки строки в указанной кодировке (по умолчанию CP866 для кириллицы).</summary>
    public static bool SendText(string printerName, string text, Encoding? encoding = null)
    {
        encoding ??= GetCyrillicEncoding();
        return SendBytes(printerName, encoding.GetBytes(text));
    }

    public static Encoding GetCyrillicEncoding()
    {
        // CP866 — типовая кодовая страница для кириллицы на ESC/POS принтерах.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(866);
    }

    /// <summary>Список установленных принтеров (через System.Drawing.Printing).</summary>
    public static IEnumerable<string> InstalledPrinters()
    {
        foreach (string name in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            yield return name;
    }

    /// <summary>
    /// Проверяет, что принтер в спулере реально готов печатать. Без этой проверки WritePrinter
    /// успешно кладёт задание в спулер, спулер не может отдать его устройству (неверный порт,
    /// нет связи), задание виснет в состоянии Error — а приложение уже отрапортовало «напечатано».
    /// Бросает исключение с причиной, чтобы очередь считала задание неудачным и повторила.
    /// </summary>
    public static void EnsureReady(string printerName)
    {
        using var server = new System.Printing.LocalPrintServer();
        using var queue = server.GetPrintQueue(printerName);
        queue.Refresh();

        var problems = new List<string>();
        if (queue.IsOffline) problems.Add("принтер оффлайн");
        if (queue.IsInError) problems.Add("состояние ошибки");
        if (queue.IsOutOfPaper) problems.Add("нет бумаги");
        if (queue.IsPaperJammed) problems.Add("замятие бумаги");
        if (queue.IsDoorOpened) problems.Add("открыта крышка");

        if (problems.Count > 0)
            throw new IOException(
                $"Принтер «{printerName}» не готов: {string.Join(", ", problems)}. " +
                $"Порт: {SafePortName(queue)}.");
    }

    private static string SafePortName(System.Printing.PrintQueue queue)
    {
        try { return queue.QueuePort?.Name ?? "неизвестен"; } catch { return "неизвестен"; }
    }
}
