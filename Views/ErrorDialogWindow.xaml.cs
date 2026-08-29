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

    /// <summary>Показывает окно немодально (не блокирует принт-сервер/UI), максимум одно на экране за раз для одной причины — вызывающая сторона решает про дедупликацию.</summary>
    public static void ShowFor(string? errorMessage, bool showRetry = false, Action? onRetry = null)
    {
        var advice = PrintErrorClassifier.Classify(errorMessage);
        var win = new ErrorDialogWindow(advice, showRetry, onRetry)
        {
            Owner = Application.Current.MainWindow is { IsVisible: true } mw ? mw : null
        };
        win.Show();
    }
}
