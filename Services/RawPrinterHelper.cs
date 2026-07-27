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
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int count, out int written);

    /// <summary>Отправляет массив байтов на указанный принтер как один RAW-документ.</summary>
    public static bool SendBytes(string printerName, byte[] bytes, string docName = "AutoPrint Receipt")
    {
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            throw new IOException($"Не удалось открыть принтер '{printerName}'. Код: {Marshal.GetLastWin32Error()}");

        IntPtr pUnmanaged = IntPtr.Zero;
        try
        {
            var di = new DOCINFOA { pDocName = docName, pDataType = "RAW" };
            if (!StartDocPrinter(hPrinter, 1, di))
                throw new IOException($"StartDocPrinter failed. Код: {Marshal.GetLastWin32Error()}");

            if (!StartPagePrinter(hPrinter))
                throw new IOException($"StartPagePrinter failed. Код: {Marshal.GetLastWin32Error()}");

            pUnmanaged = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, pUnmanaged, bytes.Length);

            bool ok = WritePrinter(hPrinter, pUnmanaged, bytes.Length, out int written);

            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
            return ok && written == bytes.Length;
        }
        finally
        {
            if (pUnmanaged != IntPtr.Zero) Marshal.FreeCoTaskMem(pUnmanaged);
            ClosePrinter(hPrinter);
        }
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
