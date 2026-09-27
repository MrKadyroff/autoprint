using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AutoPrint.Services;

namespace AutoPrint;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool minimized = e.Args.Contains(AutostartService.MinimizedArg, StringComparer.OrdinalIgnoreCase);

        // --- Single instance: два сервера на одном порту = гарантированное падение ---
        // Ждём освобождения мьютекса, а не выходим сразу: и автоперезапуск после падения,
        // и апдейтер стартуют новый процесс, пока старый ещё дозавершается. Мгновенный
        // выход в этом случае означал бы, что приложение просто не поднимется обратно.
        _singleInstance = new Mutex(initiallyOwned: false, "AutoPrint.SingleInstance");
        bool acquired;
        try { acquired = _singleInstance.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { acquired = true; }   // прошлый процесс упал, не отпустив
        if (!acquired)
        {
            // Из автозагрузки окно показывать некому — молча уходим, чтобы MessageBox
            // не висел невидимым диалогом на экране логина.
            if (!minimized)
                MessageBox.Show("AutoPrint уже запущен.", "AutoPrint",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            FileLog.Info("Запуск отменён: уже работает другой экземпляр.");
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // --- Глобальные перехватчики: приложение не должно падать от одиночной ошибки ---

        // 1. Исключения в UI-потоке — гасим и продолжаем работать.
        DispatcherUnhandledException += OnDispatcherException;

        // 2. Необработанные исключения в фоновых потоках — логируем, пытаемся перезапуститься.
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        // 3. «Забытые» исключения в Task — помечаем обработанными, чтобы не валили процесс.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        FileLog.CleanupOldLogs();
        FileLog.Info($"=== Запуск AutoPrint {UpdateService.CurrentVersion} ===");

        // Раньше MainWindow создавался неявно через StartupUri, поэтому он всегда
        // появлялся на экране. Автозапуск с Windows (AutostartService) стартует процесс
        // с флагом --minimized — окно в этом случае не должно выскакивать поверх
        // рабочего стола при каждом включении компьютера, а сразу уходить в трей.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (minimized) window.Hide();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.Error("DispatcherUnhandledException", e.Exception);

        // Если ни одного окна ещё не открыто (например, упал XAML/DataContext при старте),
        // молчаливое "Handled = true" оставит процесс висеть без единой видимой формы —
        // пользователь увидит только запись в диспетчере задач. В этом случае показываем
        // ошибку и завершаемся, а не притворяемся, что всё работает.
        if (Windows.Count == 0)
        {
            MessageBox.Show(
                $"AutoPrint не смог запуститься:\n{e.Exception.Message}\n\nПодробности в логе: {FileLog.LogDir}",
                "AutoPrint — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
            return;
        }

        // Иначе — уже работающее окно, печать/сервер продолжают жить, ошибку просто гасим.
        e.Handled = true;
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception("Неизвестная фатальная ошибка");
        string dump = FileLog.WriteCrash("AppDomain", ex);
        FileLog.Error($"Фатальная ошибка (terminating={e.IsTerminating}). Дамп: {dump}", ex);

        // Если процесс всё равно завершается — пробуем перезапуститься (с защитой от цикла).
        if (e.IsTerminating)
            TryAutoRestart();
    }

    /// <summary>Перезапуск с защитой от лавины: не чаще 3 раз за 60 секунд.</summary>
    private static void TryAutoRestart()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPrint");
            Directory.CreateDirectory(dir);
            string marker = Path.Combine(dir, "restart.log");

            var now = DateTime.UtcNow;
            var recent = new List<DateTime>();
            if (File.Exists(marker))
                foreach (var line in File.ReadAllLines(marker))
                    if (DateTime.TryParse(line, out var dt) && (now - dt).TotalSeconds < 60)
                        recent.Add(dt);

            if (recent.Count >= 3)
            {
                FileLog.Error("Слишком много перезапусков за минуту — автоперезапуск остановлен.");
                return;
            }

            recent.Add(now);
            File.WriteAllLines(marker, recent.Select(d => d.ToString("o")));

            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                FileLog.Info("Автоперезапуск приложения…");
                // Сохраняем исходные аргументы (в частности --minimized) — иначе перезапуск
                // после падения посреди автозагрузки внезапно вытащит окно на экран.
                string args = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Не удалось выполнить автоперезапуск", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLog.Info($"=== Выход AutoPrint (код {e.ApplicationExitCode}) ===");
        try { _singleInstance?.ReleaseMutex(); }
        catch (ApplicationException) { /* не владели — например, вышли из-за второго экземпляра */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
