using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AutoPrint.Services;

namespace AutoPrint.ViewModels;

/// <summary>Цвет иконки шага диагностики по его статусу.</summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is StepStatus s
            ? new SolidColorBrush(s switch
            {
                StepStatus.Ok => Color.FromRgb(0x10, 0x7C, 0x10),
                StepStatus.Warn => Color.FromRgb(0xB7, 0x6E, 0x00),
                StepStatus.Error => Color.FromRgb(0xC4, 0x2B, 0x1C),
                StepStatus.Running => Color.FromRgb(0x00, 0x67, 0xC0),
                _ => Color.FromRgb(0x5D, 0x5D, 0x5D)
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Инвертирует bool (для парных RadioButton).</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object? p, CultureInfo c) => value is bool b && !b;
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => value is bool b && !b;
}
