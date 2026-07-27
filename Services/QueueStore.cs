using System.IO;
using System.Text.Json;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>Сериализуемый снимок задания очереди (файл на диске).</summary>
public class QueueRecord
{
    public int Id { get; set; }
    public PrintJob Job { get; set; } = new();          // RawImage сериализуется как base64
    public ReceiptGeometry Geometry { get; set; } = new();
    public string Status { get; set; } = "Queued";
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
    public DateTime QueuedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Персистентная очередь: каждое задание — отдельный JSON-файл в %AppData%\AutoPrint\queue.
/// Благодаря этому очередь «копится» и переживает падение/перезапуск — ничего не теряется,
/// а завершённые задания остаются историей для повторной печати.
/// </summary>
public class QueueStore
{
    public string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPrint", "queue");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public QueueStore() => Directory.CreateDirectory(Dir);

    private string PathFor(int id) => Path.Combine(Dir, $"job-{id:D8}.json");

    public void Save(QueueRecord rec)
    {
        try { File.WriteAllText(PathFor(rec.Id), JsonSerializer.Serialize(rec, Opts)); }
        catch (Exception ex) { FileLog.Error($"QueueStore.Save #{rec.Id}", ex); }
    }

    public void Delete(int id)
    {
        try { var p = PathFor(id); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { FileLog.Error($"QueueStore.Delete #{id}", ex); }
    }

    /// <summary>Загружает все сохранённые задания (по возрастанию Id).</summary>
    public List<QueueRecord> LoadAll()
    {
        var list = new List<QueueRecord>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, "job-*.json"))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<QueueRecord>(File.ReadAllText(file));
                    if (rec is not null) list.Add(rec);
                }
                catch (Exception ex) { FileLog.Error($"QueueStore.Load {file}", ex); }
            }
        }
        catch (Exception ex) { FileLog.Error("QueueStore.LoadAll", ex); }
        return list.OrderBy(r => r.Id).ToList();
    }
}
