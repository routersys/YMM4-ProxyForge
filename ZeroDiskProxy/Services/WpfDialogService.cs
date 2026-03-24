using System.Windows;
using ZeroDiskProxy.Interfaces;

namespace ZeroDiskProxy.Services;

internal sealed class WpfDialogService : IDialogService
{
    public void ShowInformation(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}