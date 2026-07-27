using System.Globalization;
using System.Text.Json;
using AutoPrint.Models;

namespace AutoPrint.Services;

/// <summary>Сборка внутреннего макета чека (JSON-путь) в поток ESC/POS команд.</summary>
public static class ReceiptComposer
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Разбор JSON-шаблона во внутреннюю модель конструктора.</summary>
    public static ReceiptModel ParseJson(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ReceiptModel>(json, opts)
               ?? throw new FormatException("Пустой или некорректный JSON чека.");
    }

    /// <summary>Кол-во символьных колонок под ширину ленты (для моноширинного шрифта принтера).</summary>
    public static int ColumnsFor(double tapeWidthMm) => tapeWidthMm <= 62 ? 32 : 42;

    /// <summary>Формирует ESC/POS байты чека из модели с учётом геометрии и авто-отрезки.</summary>
    public static byte[] Compose(ReceiptModel r, ReceiptGeometry geo)
    {
        int cols = ColumnsFor(geo.TapeWidthMm);
        var e = new EscPos().CodePageCyrillic();

        e.AlignCenter().Bold(true).Size(2, 2).Line(r.Header).Size(1, 1).Bold(false);
        if (!string.IsNullOrWhiteSpace(r.SubHeader)) e.Line(r.SubHeader);
        e.Feed(1).Divider(cols).AlignLeft();

        foreach (var it in r.Items)
        {
            e.Line(it.Name);
            string left = $"  {it.Qty} x {it.Price.ToString("N2", Ru)}";
            string right = it.Sum.ToString("N2", Ru);
            e.Line(TwoColumns(left, right, cols));
        }

        e.Divider(cols);
        e.Bold(true).Size(1, 2)
         .Line(TwoColumns("ИТОГО:", r.Total.ToString("N2", Ru) + " р.", cols))
         .Size(1, 1).Bold(false);

        if (!string.IsNullOrWhiteSpace(r.Barcode))
            e.Feed(1).AlignCenter().BarcodeCode128(r.Barcode).Feed(1);

        if (!string.IsNullOrWhiteSpace(r.Footer))
            e.AlignCenter().Line(r.Footer);

        e.AlignLeft().AutoCut();     // <-- Auto-Cut в конце (1D 56 41 00)
        return e.ToArray();
    }

    /// <summary>Раскладка "текст слева / текст справа" по ширине в колонках.</summary>
    private static string TwoColumns(string left, string right, int cols)
    {
        int space = cols - left.Length - right.Length;
        if (space < 1) space = 1;
        return left + new string(' ', space) + right;
    }
}
