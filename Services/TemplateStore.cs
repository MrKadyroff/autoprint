using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>
/// Хранит редактируемые шаблоны чеков на диске (по одному на тип документа) в
/// %AppData%/AutoPrint/templates/{type}.json. Если файла нет — отдаёт дефолтный
/// шаблон, повторяющий стандартный вид фискального чека ОФД.
/// </summary>
public static class TemplateStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string Dir
    {
        get
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AutoPrint", "templates");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string PathFor(FiscalDocType type)
        => Path.Combine(Dir, ReceiptTemplate.KeyOf(type) + ".json");

    /// <summary>Есть ли на диске сохранённый (пользовательский) шаблон для типа.</summary>
    public static bool Exists(FiscalDocType type)
    {
        try { return File.Exists(PathFor(type)); } catch { return false; }
    }

    /// <summary>Загружает шаблон типа (или дефолтный, если не сохранён/битый).</summary>
    public static ReceiptTemplate Load(FiscalDocType type)
    {
        try
        {
            string path = PathFor(type);
            if (File.Exists(path))
            {
                var t = JsonSerializer.Deserialize<ReceiptTemplate>(File.ReadAllText(path), Opts);
                if (t is { Lines.Count: > 0 }) return t;
            }
        }
        catch (Exception ex) { FileLog.Error("TemplateStore.Load", ex); }
        return ReceiptTemplate.Default(type);
    }

    public static void Save(FiscalDocType type, ReceiptTemplate template)
    {
        try { File.WriteAllText(PathFor(type), JsonSerializer.Serialize(template, Opts)); }
        catch (Exception ex) { FileLog.Error("TemplateStore.Save", ex); }
    }

    /// <summary>Сбрасывает шаблон типа к дефолтному (удаляет файл).</summary>
    public static void Reset(FiscalDocType type)
    {
        try { var p = PathFor(type); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { FileLog.Error("TemplateStore.Reset", ex); }
    }
}
