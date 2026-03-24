using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ZeroDiskProxy.ViewModels;

namespace ZeroDiskProxy.Views;

public partial class GenerationPopupView : Window
{
    private readonly GenerationPopupViewModel _viewModel;
    private bool _allowClose;

    internal GenerationPopupView(GenerationPopupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.Items.CollectionChanged += OnItemsChanged;
        Loaded += (_, _) => PositionBottomRight();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.Items.Count > 0)
            Dispatcher.BeginInvoke(PositionBottomRight, DispatcherPriority.Loaded);
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        var windowWidth = double.IsNaN(ActualWidth) || ActualWidth <= 0 ? Width : ActualWidth;
        var windowHeight = double.IsNaN(ActualHeight) || ActualHeight <= 0 ? Height : ActualHeight;

        if (double.IsNaN(windowWidth) || windowWidth <= 0)
            windowWidth = MinWidth;
        if (double.IsNaN(windowHeight) || windowHeight <= 0)
            windowHeight = MinHeight;

        Left = workArea.Right - windowWidth - 12;
        Top = workArea.Bottom - windowHeight - 12;
    }

    internal void ForceClose()
    {
        _allowClose = true;
        _viewModel.Items.CollectionChanged -= OnItemsChanged;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}

internal sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}