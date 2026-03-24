using System.Reflection;
using System.Windows.Controls;
using ZeroDiskProxy.Plugin;
using ZeroDiskProxy.Services;
using ZeroDiskProxy.ViewModels;

namespace ZeroDiskProxy.Views;

public partial class ZeroDiskProxySettingsView : UserControl
{
    public ZeroDiskProxySettingsView()
    {
        InitializeComponent();
        var host = PluginHost.Instance;
        DataContext = new SettingsViewModel(host?.CacheManager, host?.Budget, new WpfDialogService());
        VersionText.Text = string.Concat("v", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
    }
}