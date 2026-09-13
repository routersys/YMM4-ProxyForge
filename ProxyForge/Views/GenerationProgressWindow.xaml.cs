using System.ComponentModel;
using System.Windows;
using ProxyForge.ViewModels;

namespace ProxyForge.Views;

public partial class GenerationProgressWindow : Window
{
    const double ScreenMargin = 12d;

    bool closingAllowed;

    internal GenerationProgressWindow(GenerationProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SizeChanged += (_, _) => PlaceAtBottomRight();
    }

    internal event EventHandler? HideRequested;

    internal void CloseForShutdown()
    {
        closingAllowed = true;
        Close();
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void PlaceAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        var width = double.IsNaN(ActualWidth) || ActualWidth <= 0d ? Width : ActualWidth;
        var height = double.IsNaN(ActualHeight) || ActualHeight <= 0d ? MinHeight : ActualHeight;
        Left = area.Right - width - ScreenMargin;
        Top = area.Bottom - height - ScreenMargin;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closingAllowed)
        {
            e.Cancel = true;
            Hide();
            HideRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnClosing(e);
    }
}
