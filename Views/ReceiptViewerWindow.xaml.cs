using System.Runtime.Versioning;
using System.Windows;
using AutoPrint.Services;
using AutoPrint.ViewModels;

namespace AutoPrint.Views;

/// <summary>
/// Просмотр чека из истории очереди: показывает ленту ровно так, как она ушла на принтер
/// (собственная геометрия задания, тот же дизеринг), и позволяет отправить чек в печать
/// повторно, не восстанавливая настройки конструктора вручную.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ReceiptViewerWindow : Window
{
    private readonly PrintQueueItem _item;
    private readonly Action<PrintQueueItem> _reprint;

    public ReceiptViewerWindow(PrintQueueItem item, Action<PrintQueueItem> reprint)
    {
        InitializeComponent();
        _item = item;
        _reprint = reprint;

        StatusIconText.Text = item.StatusIcon;
        HeadlineText.Text = $"#{item.Id} · {item.KindLabel}";
        SubtitleText.Text = BuildSubtitle(item);

        if (!string.IsNullOrWhiteSpace(item.LastError))
        {
            ErrorBox.Visibility = Visibility.Visible;
            ErrorText.Text = "⚠ " + item.LastError;
        }

        // Задание, которое ещё ждёт своей очереди, повторять незачем — оно и так напечатается.
        if (!item.CanReprint) ReprintButton.Visibility = Visibility.Collapsed;

        // Лента шириной как у самого задания, а не как в текущих настройках.
        TapeBorder.Width = PrintPipeline.PixelsFor(item.Geometry.TapeWidthMm);
        TapeBorder.Padding = new Thickness(item.Geometry.SideMarginsPx, 8, item.Geometry.SideMarginsPx, 8);
        PreviewTextBlock.FontSize = item.Geometry.BodyFontPt * 96.0 / 72.0;

        Loaded += async (_, _) => await RenderAsync();
    }

    private static string BuildSubtitle(PrintQueueItem item)
    {
        string fmt = item.Job.Format.ToString().ToUpperInvariant();
        string s = $"{item.OrgLabel} · {item.QueuedAt:dd.MM.yyyy HH:mm:ss} · {fmt} · {item.StatusLabel}";
        if (item.Attempts > 1) s += $" · попыток: {item.Attempts}";
        if (!string.IsNullOrWhiteSpace(item.Job.Source)) s += $" · источник: {item.Job.Source}";
        return s;
    }

    private async Task RenderAsync()
    {
        try
        {
            var preview = await ReceiptPreviewRenderer.RenderAsync(_item.Job, _item.Geometry);
            if (preview.Image is not null)
            {
                PreviewImageControl.Source = preview.Image;
                PreviewImageControl.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewTextBlock.Text = preview.Text;
                PreviewTextBlock.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"ReceiptViewer #{_item.Id}", ex);
            PreviewTextBlock.Text = $"⚠ Не удалось построить предпросмотр:\n{ex.Message}";
            PreviewTextBlock.Visibility = Visibility.Visible;
        }
        finally { BusyText.Visibility = Visibility.Collapsed; }
    }

    private void Reprint_Click(object sender, RoutedEventArgs e)
    {
        _reprint(_item);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
