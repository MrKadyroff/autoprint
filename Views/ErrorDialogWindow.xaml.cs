using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AutoPrint.Services;

namespace AutoPrint.Views;

/// <summary>
/// Немодальное всплывающее окно с понятной причиной сбоя печати и шагами устранения
/// («нет бумаги», «принтер не подключён» и т.п.) вместо технического текста исключения.
/// </summary>
public partial class ErrorDialogWindow : Window
{
    public bool RetryRequested { get; private set; }

    /// <summary>Уже открытые уведомления — чтобы несколько подряд сбоев печати не сыпались
    /// друг на друга в одной точке экрана, а вставали стопкой снизу вверх.</summary>
    private static readonly List<ErrorDialogWindow> _open = new();
    private const double ScreenMargin = 16;

    public ErrorDialogWindow(PrintErrorAdvice advice, bool showRetry, Action? onRetry = null)
    {
        InitializeComponent();
        TitleText.Text = advice.Title;
        MessageText.Text = advice.Message;

        foreach (string step in advice.Steps)
        {
            StepsPanel.Children.Add(new TextBlock
            {
                Text = "•  " + step,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 13
            });
        }

        if (showRetry)
        {
            RetryButton.Visibility = Visibility.Visible;
            RetryButton.Tag = onRetry;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryRequested = true;
        (RetryButton.Tag as Action)?.Invoke();
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Показывает немодальное уведомление снизу справа экрана (как системный тост) —
    /// не перехватывает фокус кассира у другого приложения (ShowActivated="False") и не
    /// блокирует принт-сервер/UI. При нескольких сбоях подряд уведомления укладываются
    /// стопкой снизу вверх, а не перекрывают друг друга.
    /// </summary>
    public static void ShowFor(string? errorMessage, bool showRetry = false, Action? onRetry = null)
    {
        var advice = PrintErrorClassifier.Classify(errorMessage);
        var win = new ErrorDialogWindow(advice, showRetry, onRetry)
        {
            Owner = Application.Current.MainWindow is { IsVisible: true } mw ? mw : null
        };

        var wa = SystemParameters.WorkArea;
        double stackedHeight = _open.Sum(w => w.Height + ScreenMargin);
        win.Left = wa.Right - win.Width - ScreenMargin;
        win.Top = wa.Bottom - win.Height - ScreenMargin - stackedHeight;

        _open.Add(win);
        win.Closed += (_, _) => _open.Remove(win);
        win.Show();
    }
}
