using AutoPrint.Models;

namespace AutoPrint.ViewModels;

/// <summary>Пункт выбора типа документа (для сегментированного переключателя).</summary>
public record DocTypeItem(string Label, FiscalDocType Type);

/// <summary>
/// Обёртка строки шаблона для конструктора: правки сразу отражаются в модели и
/// поднимают <see cref="Changed"/> → живое обновление превью.
/// </summary>
public class TemplateLineVM : ViewModelBase
{
    public TemplateLine Model { get; }
    private readonly Action _changed;

    public TemplateLineVM(TemplateLine model, Action changed)
    {
        Model = model;
        _changed = changed;
    }

    private void Touch(string prop) { OnPropertyChanged(prop); _changed(); }

    // ---- Тип строки (только чтение — задаётся при добавлении) ----
    public LineKind Kind => Model.Kind;
    public string KindLabel => Model.Kind switch
    {
        LineKind.Text     => "Текст",
        LineKind.TwoCol   => "2 колонки",
        LineKind.Spacer   => "Отступ",
        LineKind.Items    => "Позиции",
        LineKind.Payments => "Оплата / счётчики",
        _                 => "?"
    };

    public bool IsText   => Model.Kind == LineKind.Text;
    public bool IsTwoCol => Model.Kind == LineKind.TwoCol;
    public bool IsItems  => Model.Kind == LineKind.Items;
    /// <summary>Показывать ли настройки выравнивания/шрифта (для Spacer/Payments не нужно).</summary>
    public bool HasFormatting => Model.Kind is LineKind.Text or LineKind.TwoCol or LineKind.Items;

    // ---- Выравнивание (0 лево / 1 центр / 2 право) ----
    public int AlignIndex
    {
        get => (int)Model.Align;
        set { if ((int)Model.Align != value) { Model.Align = (LineAlign)value; Touch(nameof(AlignIndex)); } }
    }

    // ---- Группа шрифта: true = шапка, false = тело ----
    public bool IsHeaderFont
    {
        get => Model.Font == FontGroup.Header;
        set { var g = value ? FontGroup.Header : FontGroup.Body; if (Model.Font != g) { Model.Font = g; Touch(nameof(IsHeaderFont)); } }
    }

    public bool Bold
    {
        get => Model.Bold;
        set { if (Model.Bold != value) { Model.Bold = value; Touch(nameof(Bold)); } }
    }

    public string Content
    {
        get => Model.Content;
        set { if (Model.Content != value) { Model.Content = value; Touch(nameof(Content)); } }
    }

    public string Left
    {
        get => Model.Left;
        set { if (Model.Left != value) { Model.Left = value; Touch(nameof(Left)); } }
    }

    public string Right
    {
        get => Model.Right;
        set { if (Model.Right != value) { Model.Right = value; Touch(nameof(Right)); } }
    }

    public string ItemLine1
    {
        get => Model.ItemLine1;
        set { if (Model.ItemLine1 != value) { Model.ItemLine1 = value; Touch(nameof(ItemLine1)); } }
    }
    public string ItemLine2Left
    {
        get => Model.ItemLine2Left;
        set { if (Model.ItemLine2Left != value) { Model.ItemLine2Left = value; Touch(nameof(ItemLine2Left)); } }
    }
    public string ItemLine2Right
    {
        get => Model.ItemLine2Right;
        set { if (Model.ItemLine2Right != value) { Model.ItemLine2Right = value; Touch(nameof(ItemLine2Right)); } }
    }
}
