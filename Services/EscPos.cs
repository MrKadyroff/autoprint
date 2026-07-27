using System.IO;
using System.Text;

namespace AutoPrint.Services;

/// <summary>
/// Fluent-конструктор ESC/POS команд для термопринтера Mulex P80.
/// Накапливает байты в буфер, готовый к отправке через <see cref="RawPrinterHelper"/>.
/// </summary>
public class EscPos
{
    private readonly MemoryStream _buf = new();
    private readonly Encoding _enc;

    // Управляющие байты
    private const byte ESC = 0x1B;
    private const byte GS = 0x1D;
    private const byte LF = 0x0A;

    public EscPos(Encoding? encoding = null)
    {
        _enc = encoding ?? RawPrinterHelper.GetCyrillicEncoding();
        Init();
    }

    private void Raw(params byte[] b) => _buf.Write(b, 0, b.Length);

    /// <summary>ESC @ — сброс принтера в исходное состояние.</summary>
    public EscPos Init() { Raw(ESC, (byte)'@'); return this; }

    /// <summary>Выбор кодовой страрицы CP866 (кириллица): ESC t 17.</summary>
    public EscPos CodePageCyrillic() { Raw(ESC, (byte)'t', 17); return this; }

    public EscPos Text(string s) { var b = _enc.GetBytes(s); _buf.Write(b, 0, b.Length); return this; }
    public EscPos Line(string s = "") { Text(s); Raw(LF); return this; }
    public EscPos Feed(int lines = 1) { for (int i = 0; i < lines; i++) Raw(LF); return this; }

    /// <summary>ESC a n — выравнивание: 0=лево, 1=центр, 2=право.</summary>
    public EscPos Align(int n) { Raw(ESC, (byte)'a', (byte)n); return this; }
    public EscPos AlignLeft() => Align(0);
    public EscPos AlignCenter() => Align(1);
    public EscPos AlignRight() => Align(2);

    /// <summary>ESC E n — жирный текст вкл/выкл.</summary>
    public EscPos Bold(bool on) { Raw(ESC, (byte)'E', (byte)(on ? 1 : 0)); return this; }

    /// <summary>GS ! n — множитель размера символов (0..7 по каждой оси).</summary>
    public EscPos Size(int w, int h)
    {
        byte n = (byte)(((Math.Clamp(w, 1, 8) - 1) << 4) | (Math.Clamp(h, 1, 8) - 1));
        Raw(GS, (byte)'!', n);
        return this;
    }
    public EscPos NormalSize() => Size(1, 1);

    /// <summary>Разделитель на всю ширину (кол-во колонок зависит от ленты: 42 для 80мм, 32 для 58мм).</summary>
    public EscPos Divider(int cols = 42) => Line(new string('-', cols));

    /// <summary>Печать штрих-кода CODE128: GS k 73 len data.</summary>
    public EscPos BarcodeCode128(string data)
    {
        Raw(GS, (byte)'H', 2);          // GS H 2 — печатать значение под кодом
        Raw(GS, (byte)'h', 80);         // GS h  — высота 80 точек
        Raw(GS, (byte)'w', 2);          // GS w  — ширина модуля
        var payload = _enc.GetBytes(data);
        Raw(GS, (byte)'k', 73, (byte)(payload.Length + 2), (byte)'{', (byte)'B');
        _buf.Write(payload, 0, payload.Length);
        return this;
    }

    /// <summary>
    /// Максимальная высота одной команды растра. Термопринтер имеет ограниченный буфер
    /// изображения; слишком высокая одиночная GS v 0 переполняет его — принтер «зависает»
    /// в середине картинки и трактует байты следующего чека как продолжение изображения
    /// (симптом: «первый чек напечатан, дальше молчит»). Режем ленту на самодостаточные полосы.
    /// </summary>
    private const int MaxBandRows = 128;

    /// <summary>
    /// Растровое изображение: GS v 0 m xL xH yL yH data. Ожидает упакованный 1-битный буфер
    /// (см. <see cref="ImageProcessor"/>). Автоматически разбивается на полосы по
    /// <see cref="MaxBandRows"/> строк — каждая уходит отдельной завершённой командой.
    /// </summary>
    public EscPos RasterImage(byte[] packed1bpp, int widthBytes, int heightPx)
    {
        for (int y0 = 0; y0 < heightPx; y0 += MaxBandRows)
        {
            int rows = Math.Min(MaxBandRows, heightPx - y0);
            Raw(GS, (byte)'v', (byte)'0', 0);
            Raw((byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF));
            Raw((byte)(rows & 0xFF), (byte)((rows >> 8) & 0xFF));
            _buf.Write(packed1bpp, y0 * widthBytes, rows * widthBytes);
        }
        return this;
    }

    /// <summary>
    /// Отрез ленты. Используем базовую форму GS V 1 (1D 56 01 — частичный рез) без
    /// байта-параметра: её поддерживает подавляющее большинство 80мм принтеров, тогда как
    /// форма с протяжкой GS V 65/66 n у части моделей (в т.ч. Mulex) не отрабатывает —
    /// рез не происходит, а лишний байт-параметр десинхронизирует поток и «вешает» печать.
    /// Протяжку до ножа делаем обычным переводом строки заранее.
    /// </summary>
    public EscPos AutoCut(int feedLines = 4)
    {
        if (feedLines > 0) Feed(feedLines);   // протянуть до ножа, чтобы не срезать текст
        Raw(GS, (byte)'V', 1);                // GS V 1 — частичный рез
        return this;
    }

    /// <summary>GS V 0 — полный отрез. feedLines протягивает ленту до ножа перед резом.</summary>
    public EscPos FullCut(int feedLines = 4)
    {
        if (feedLines > 0) Feed(feedLines);
        Raw(GS, (byte)'V', 0);
        return this;
    }

    /// <summary>
    /// Завершение чека согласно настройкам: протяжка вниз, затем (опц.) отрез нужного типа
    /// и (опц.) импульс на денежный ящик.
    /// </summary>
    public EscPos Finish(int bottomFeed, bool cut, bool fullCut, bool openDrawer)
    {
        if (cut)
        {
            if (fullCut) FullCut(bottomFeed); else AutoCut(bottomFeed);
        }
        else if (bottomFeed > 0)
        {
            Feed(bottomFeed);
        }
        if (openDrawer) OpenDrawer();
        return this;
    }

    /// <summary>Открыть денежный ящик (ESC p 0 25 250), если подключён.</summary>
    public EscPos OpenDrawer() { Raw(ESC, (byte)'p', 0, 25, 250); return this; }

    public byte[] ToArray() => _buf.ToArray();
}
