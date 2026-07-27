using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoPrint.ViewModels;

namespace AutoPrint;

public partial class MainWindow : Window
{
    private bool _exiting;              // true — реальный выход, а не сворачивание в трей
    private bool _trayHintShown;        // балун-подсказку про трей показываем один раз

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Tray.Icon = BuildTrayIcon();
    }

    // ===== Значок в системном трее =====

    /// <summary>Двойной клик по значку / пункт «Открыть» — показать и активировать окно.</summary>
    private void Tray_ShowWindow(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // вытащить поверх, но не залипать
    }

    /// <summary>Пункт «Выход» — настоящий выход (останавливает принт-сервер).</summary>
    private void Tray_Exit(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Tray.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Закрытие окна (крестик) не завершает приложение — это принт-сервер, он должен
    /// продолжать принимать чеки. Прячем окно в трей; выйти можно из меню значка.
    /// </summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) { Tray.Dispose(); return; }

        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Tray.ShowBalloonTip("AutoPrint работает в фоне",
                "Принт-сервер продолжает печатать. Открыть окно или выйти — через значок в трее.",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
    }

    /// <summary>Рисует значок трея во время выполнения (без .ico-файла в проекте).</summary>
    private static System.Drawing.Icon BuildTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2D, 0x7D, 0xF6));
            g.FillRectangle(bg, 2, 2, 28, 28);
            using var f = new System.Drawing.Font("Segoe UI", 15, System.Drawing.FontStyle.Bold,
                                                  System.Drawing.GraphicsUnit.Pixel);
            var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            g.DrawString("AP", f, System.Drawing.Brushes.White,
                         new System.Drawing.RectangleF(0, 0, 32, 32), sf);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ===== Умная прокрутка колонок =====
    // Проблема WPF: вложенные ListBox/TextBox/ScrollViewer перехватывают колесо мыши,
    // и внешняя колонка не прокручивается («не удобно скролить»). Ловим колесо на этапе
    // туннелирования (Preview) у внешней колонки: если внутренний скролл-контейнер под
    // курсором ещё может прокрутиться в эту сторону — отдаём событие ему; иначе крутим колонку.
    private void Column_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer outer) return;

        var inner = FindInnerScrollViewer(e.OriginalSource as DependencyObject, outer);
        if (inner is not null && CanScroll(inner, e.Delta)) return;   // пусть крутит внутренний

        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Ищет ближайший вложенный ScrollViewer между источником события и внешней колонкой.</summary>
    private static ScrollViewer? FindInnerScrollViewer(DependencyObject? node, ScrollViewer outer)
    {
        while (node is not null && node != outer)
        {
            if (node is ScrollViewer sv && sv != outer) return sv;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Может ли контейнер прокрутиться в направлении вращения колеса.</summary>
    private static bool CanScroll(ScrollViewer sv, int delta)
    {
        if (sv.ScrollableHeight <= 0) return false;
        return delta > 0 ? sv.VerticalOffset > 0.5
                         : sv.VerticalOffset < sv.ScrollableHeight - 0.5;
    }

    // ===== Перетаскивание строк конструктора чека =====

    /// <summary>Начало перетаскивания строки за «грип» ⠿.</summary>
    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TemplateLineVM line)
        {
            DragDrop.DoDragDrop(fe, line, DragDropEffects.Move);
            e.Handled = true;
        }
    }

    private void LinesList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TemplateLineVM))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Бросили строку — переставляем перед/на элемент под курсором.</summary>
    private void LinesList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TemplateLineVM)) is not TemplateLineVM src) return;
        if (DataContext is not MainViewModel vm) return;

        var target = FindLineUnder(e.OriginalSource as DependencyObject);
        int index = target is null ? vm.TemplateLines.Count - 1 : vm.TemplateLines.IndexOf(target);
        vm.MoveLineTo(src, index);
        e.Handled = true;
    }

    /// <summary>Поднимается по визуальному дереву до ListBoxItem и возвращает его строку.</summary>
    private static TemplateLineVM? FindLineUnder(DependencyObject? node)
    {
        while (node is not null and not ListBoxItem)
            node = VisualTreeHelper.GetParent(node);
        return (node as ListBoxItem)?.DataContext as TemplateLineVM;
    }
}
