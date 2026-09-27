using System.Collections.ObjectModel;
using System.Threading;
using AutoPrint.Models;
using AutoPrint.Services;

namespace AutoPrint.ViewModels;

public enum JobStatus { Queued, Printing, Completed, Failed, Canceled }

/// <summary>Задание в очереди печати (INotifyPropertyChanged для живого UI).</summary>
public class PrintQueueItem : ViewModelBase
{
    private static int _seq;
    private static int NextId() => Interlocked.Increment(ref _seq);
    private static void EnsureSeqAtLeast(int id)
    {
        int cur;
        while ((cur = _seq) < id && Interlocked.CompareExchange(ref _seq, id, cur) != cur) { }
    }

    public int Id { get; }
    public PrintJob Job { get; }
    public ReceiptGeometry Geometry { get; }
    public DateTime QueuedAt { get; }

    public PrintQueueItem(PrintJob job, ReceiptGeometry geometry)
        : this(NextId(), job, geometry, DateTime.Now) { }

    private PrintQueueItem(int id, PrintJob job, ReceiptGeometry geometry, DateTime queuedAt)
    {
        Id = id;
        EnsureSeqAtLeast(id);
        Job = job;
        Geometry = geometry;
        QueuedAt = queuedAt;
    }

    public ReceiptKind Kind => Job.Kind;
    public bool IsSpecial => Kind != ReceiptKind.Normal;
    public string KindLabel => Kind switch
    {
        ReceiptKind.ShiftOpen => "СМЕНА ▲ открытие",
        ReceiptKind.ShiftClose => "СМЕНА ▼ закрытие",
        ReceiptKind.Deposit => "КАССА ＋ внесение",
        ReceiptKind.Withdrawal => "КАССА − изъятие",
        ReceiptKind.Report => "ОТЧЁТ",
        _ => "Операционный"
    };

    /// <summary>Организация чека — из запроса ("orgName"), иначе ECASH по умолчанию.</summary>
    public string OrgLabel => string.IsNullOrWhiteSpace(Job.OrgName) ? "ECASH" : Job.OrgName;

    private JobStatus _status = JobStatus.Queued;
    public JobStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanReprint));
            }
        }
    }

    private int _attempts;
    public int Attempts { get => _attempts; set { SetField(ref _attempts, value); OnPropertyChanged(nameof(Title)); } }

    /// <summary>
    /// Не раньше этого момента задание снова доступно к печати. Пока идёт «остывание» после
    /// сбоя (или задание было отложено как зависшее), очередь его пропускает и берёт следующее —
    /// так один проблемный чек не блокирует остальные.
    /// </summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.MinValue;

    private string _lastError = "";
    public string LastError { get => _lastError; set { SetField(ref _lastError, value); OnPropertyChanged(nameof(Title)); } }

    public string Title => $"#{Id} · {KindLabel} · {OrgLabel} · {QueuedAt:dd.MM HH:mm:ss}"
                           + (Attempts > 1 ? $" · попыток: {Attempts}" : "")
                           + (string.IsNullOrEmpty(LastError) ? "" : $"  ⚠ {LastError}");

    public string StatusLabel => Status switch
    {
        JobStatus.Queued => "в очереди",
        JobStatus.Printing => "печать…",
        JobStatus.Completed => "готово",
        JobStatus.Failed => "ошибка",
        JobStatus.Canceled => "отменено",
        _ => ""
    };

    public string StatusIcon => Status switch
    {
        JobStatus.Queued => "🕓",
        JobStatus.Printing => "🖨",
        JobStatus.Completed => "✓",
        JobStatus.Failed => "✕",
        JobStatus.Canceled => "⊘",
        _ => "•"
    };

    public bool CanCancel => Status is JobStatus.Queued or JobStatus.Failed;
    public bool CanReprint => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

    // ---- Персистентность ----
    public QueueRecord ToRecord() => new()
    {
        Id = Id, Job = Job, Geometry = Geometry,
        Status = Status.ToString(), Attempts = Attempts, LastError = LastError, QueuedAt = QueuedAt
    };

    public static PrintQueueItem FromRecord(QueueRecord r)
    {
        var item = new PrintQueueItem(r.Id, r.Job, r.Geometry, r.QueuedAt)
        {
            Attempts = r.Attempts,
            LastError = r.LastError
        };
        // Прерванные при падении задания (Printing) возвращаем в очередь.
        item._status = Enum.TryParse<JobStatus>(r.Status, out var st)
            ? (st == JobStatus.Printing ? JobStatus.Queued : st)
            : JobStatus.Queued;
        return item;
    }
}

/// <summary>
/// Очередь печати: последовательная обработка, автоповтор, пауза, персистентность на диск
/// и повторная печать из истории. «Бесперебойность» — очередь копится и переживает
/// краш/перезапуск; завершённые задания хранятся для повторной печати (важно для смен).
/// </summary>
public class PrintQueueManager
{
    public ObservableCollection<PrintQueueItem> Items { get; } = new();

    public Func<PrintQueueItem, Task>? PrintCallback { get; set; }
    /// <summary>Готов ли принтер. Пока false — задания ждут, не расходуя попытки.</summary>
    public Func<bool>? CanPrint { get; set; }

    /// <summary>
    /// Приёмник сообщений. Назначается уже после конструктора, поэтому всё, что накопилось
    /// при восстановлении очереди с диска, выдаётся сразу при подписке.
    /// </summary>
    public Action<string>? Notify
    {
        get => _notify;
        set
        {
            _notify = value;
            if (value is null) return;
            foreach (var msg in _pending) value(msg);
            _pending.Clear();
        }
    }
    private Action<string>? _notify;
    private readonly List<string> _pending = new();

    /// <summary>Задание окончательно провалилось (исчерпаны попытки) — UI может показать модалку с причиной.</summary>
    public event Action<PrintQueueItem>? JobFailedPermanently;

    /// <summary>Принтер не готов, а задание ждёт в очереди — UI может подсказать пользователю подключить принтер.</summary>
    public event Action? PrinterNotReadyWhileQueued;

    private void Say(string message)
    {
        if (_notify is null) _pending.Add(message);
        else _notify(message);
    }

    public int MaxAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Окно дедупликации по Job.RequestId: если фронт не дождался ответа (например, порт
    /// ещё поднимался при старте компьютера) и повторил тот же запрос, второй экземпляр
    /// с тем же RequestId в пределах этого окна не встаёт в очередь заново — вместо этого
    /// возвращается уже существующее задание. Пустой RequestId дедупликации не подлежит.
    /// </summary>
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Максимальное время на печать одного задания. Если принтер «завис» (спулер не отвечает,
    /// сокет молчит) — по истечении таймаута задание откладывается, а очередь печатает следующее,
    /// не дожидаясь зависшего. Само зависшее задание вернётся к печати после остывания.
    /// </summary>
    public TimeSpan PrintTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько дней хранить завершённые обычные чеки (внутри — base64 картинок, диск не резиновый).</summary>
    public int HistoryDaysNormal { get; set; } = 7;
    /// <summary>Чеки смен/отчёты храним дольше — их переезд и нужен для повторной печати.</summary>
    public int HistoryDaysSpecial { get; set; } = 60;

    private readonly QueueStore _store = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private bool _paused;

    public bool IsPaused
    {
        get => _paused;
        private set { _paused = value; PausedChanged?.Invoke(value); }
    }
    public event Action<bool>? PausedChanged;

    public PrintQueueManager()
    {
        LoadPersisted();
        _ = RunLoopAsync();
    }

    /// <summary>Восстанавливает очередь и историю с диска при старте.</summary>
    private void LoadPersisted()
    {
        try
        {
            int queued = 0;
            foreach (var rec in _store.LoadAll())
            {
                var item = PrintQueueItem.FromRecord(rec);

                // Ретенция: просроченную завершённую историю удаляем прямо при загрузке.
                bool finished = item.Status is JobStatus.Completed or JobStatus.Canceled or JobStatus.Failed;
                int keepDays = item.IsSpecial ? HistoryDaysSpecial : HistoryDaysNormal;
                if (finished && item.QueuedAt < DateTime.Now.AddDays(-keepDays))
                {
                    _store.Delete(item.Id);
                    continue;
                }

                Items.Add(item);
                if (item.Status == JobStatus.Queued) { queued++; _store.Save(item.ToRecord()); }
            }
            if (queued > 0) { for (int i = 0; i < queued; i++) _signal.Release(); }
            if (Items.Count > 0)
                Say($"Восстановлено из очереди: {Items.Count} записей ({queued} к печати).");
        }
        catch (Exception ex) { FileLog.Error("LoadPersisted", ex); }
    }

    private void Persist(PrintQueueItem item)
    {
        try { _store.Save(item.ToRecord()); } catch (Exception ex) { FileLog.Error("Persist", ex); }
    }

    public PrintQueueItem Enqueue(PrintJob job, ReceiptGeometry geometry)
    {
        if (!string.IsNullOrWhiteSpace(job.RequestId))
        {
            var cutoff = DateTime.Now - DedupWindow;
            var dup = Items.FirstOrDefault(i =>
                i.Status != JobStatus.Canceled
                && string.Equals(i.Job.RequestId, job.RequestId, StringComparison.Ordinal)
                && i.QueuedAt >= cutoff);
            if (dup is not null)
            {
                Say($"Повторный запрос (requestId «{job.RequestId}») — уже в очереди как #{dup.Id}, не дублирую.");
                return dup;
            }
        }

        var item = new PrintQueueItem(job, geometry);
        Items.Add(item);
        Persist(item);
        _signal.Release();
        return item;
    }

    private const string ReprintSuffix = " (повтор)";

    /// <summary>Повторная печать: создаёт новую копию задания в очереди, оригинал остаётся историей.</summary>
    public PrintQueueItem Reprint(PrintQueueItem source)
    {
        var job = source.Job;   // тот же payload/картинка
        var copy = new PrintJob
        {
            Format = job.Format, Payload = job.Payload, RawImage = job.RawImage,
            // Не наращиваем «(повтор) (повтор) …» при повторе повтора.
            Source = job.Source.EndsWith(ReprintSuffix, StringComparison.Ordinal)
                ? job.Source
                : job.Source + ReprintSuffix,
            Kind = job.Kind, ReceivedAt = DateTime.Now,
            // Раньше терялись: повтор уезжал без имени принтера и показывался в истории
            // как «ECASH» вместо реальной организации. RequestId намеренно НЕ копируем —
            // иначе дедупликация отбросит повтор как дубль оригинала.
            PrinterName = job.PrinterName, OrgName = job.OrgName
        };
        var item = Enqueue(copy, source.Geometry);
        Say($"Задание #{source.Id} поставлено на повторную печать как #{item.Id}.");
        return item;
    }

    public void Pause() { IsPaused = true; Say("Очередь на паузе."); }

    public void Resume()
    {
        IsPaused = false;
        Say("Очередь возобновлена.");
        _signal.Release();
    }

    public void Cancel(PrintQueueItem item)
    {
        if (item.CanCancel) { item.Status = JobStatus.Canceled; Persist(item); }
    }

    /// <summary>
    /// Кнопка «↻» на задании. Успешно напечатанное печатаем новой копией (историю не трогаем),
    /// а сбойное/отменённое возвращаем в очередь на месте, обнулив счётчик попыток.
    /// </summary>
    public void Retry(PrintQueueItem item)
    {
        if (!item.CanReprint) return;

        if (item.Status == JobStatus.Completed)
        {
            Reprint(item);
            return;
        }

        item.LastError = "";
        item.Attempts = 0;
        item.NextAttemptAt = DateTime.MinValue;
        item.Status = JobStatus.Queued;
        Persist(item);
        _signal.Release();
    }

    /// <summary>Удаляет из истории завершённые задания, КРОМЕ чеков смен/отчётов (их бережём).</summary>
    public void ClearFinished(bool keepSpecial = true)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var it = Items[i];
            bool finished = it.Status is JobStatus.Completed or JobStatus.Canceled or JobStatus.Failed;
            if (finished && !(keepSpecial && it.IsSpecial))
            {
                _store.Delete(it.Id);
                Items.RemoveAt(i);
            }
        }
    }

    public void Kick() => _signal.Release();

    private int _wakeScheduled;
    /// <summary>
    /// Если в очереди есть только «остывающие» задания, планирует единичное пробуждение цикла
    /// к моменту, когда ближайшее из них снова станет доступным. Без этого отложенный чек
    /// печатался бы лишь после следующего входящего задания.
    /// </summary>
    private void ScheduleWakeForCooldown()
    {
        DateTime? soonest = Items
            .Where(i => i.Status == JobStatus.Queued && i.NextAttemptAt > DateTime.Now)
            .Select(i => (DateTime?)i.NextAttemptAt)
            .Min();
        if (soonest is null) return;
        if (Interlocked.Exchange(ref _wakeScheduled, 1) == 1) return;   // уже запланировано

        var delay = soonest.Value - DateTime.Now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _wakeScheduled, 0);
            _signal.Release();
        });
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync();
                if (_paused) continue;

                while (!_paused)
                {
                    var now = DateTime.Now;

                    // Берём первое задание, готовое к печати ИМЕННО сейчас (не «остывающее»
                    // после сбоя). Зависшие/сбойные с активным cooldown пропускаем — следующий
                    // чек печатается в обход них.
                    var item = Items.FirstOrDefault(i => i.Status == JobStatus.Queued && i.NextAttemptAt <= now);
                    if (item is null)
                    {
                        // Ничего готового сейчас. Если есть отложенные — проснёмся к их сроку.
                        ScheduleWakeForCooldown();
                        break;
                    }

                    // Принтер не готов / печать не настроена — ждём, попытки не тратим.
                    if (PrintCallback is null || CanPrint?.Invoke() == false)
                    {
                        PrinterNotReadyWhileQueued?.Invoke();
                        break;
                    }

                    item.Status = JobStatus.Printing;
                    Persist(item);
                    try
                    {
                        item.Attempts++;
                        if (PrintCallback is null) throw new InvalidOperationException("Печать не сконфигурирована.");

                        // Таймаут: зависший принтер не должен держать весь цикл. По истечении
                        // бросаем — задание уйдёт в cooldown, а цикл возьмёт следующее.
                        var printTask = PrintCallback(item);
                        if (await Task.WhenAny(printTask, Task.Delay(PrintTimeout)) != printTask)
                        {
                            // Не наблюдаем зависшую задачу синхронно, но гасим её будущее исключение.
                            _ = printTask.ContinueWith(t => { _ = t.Exception; },
                                TaskContinuationOptions.OnlyOnFaulted);
                            throw new TimeoutException(
                                $"печать не завершилась за {PrintTimeout.TotalSeconds:0} с");
                        }
                        await printTask;   // проброс исключения печати, если было

                        item.Status = JobStatus.Completed;
                        Persist(item);
                        Say($"Задание #{item.Id} напечатано.");
                    }
                    catch (Exception ex)
                    {
                        item.LastError = ex.Message;
                        if (item.Attempts >= MaxAttempts)
                        {
                            item.Status = JobStatus.Failed;
                            Persist(item);
                            Say($"Задание #{item.Id} — ошибка после {item.Attempts} попыток: {ex.Message}");
                            JobFailedPermanently?.Invoke(item);
                        }
                        else
                        {
                            // Откладываем это задание (cooldown) и НЕ блокируем цикл — сразу
                            // пробуем следующее готовое задание; отложенное вернётся позже.
                            item.NextAttemptAt = DateTime.Now + RetryDelay;
                            item.Status = JobStatus.Queued;
                            Persist(item);
                            Say($"Задание #{item.Id} — сбой ({ex.Message}), отложено на {RetryDelay.TotalSeconds:0} с, печатаю следующее…");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Цикл очереди НИКОГДА не должен умирать целиком.
                FileLog.Error("PrintQueue.RunLoop", ex);
                await Task.Delay(1000);
            }
        }
    }
}
