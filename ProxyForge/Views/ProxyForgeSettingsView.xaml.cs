using ProxyForge.Plugin;
using ProxyForge.Services;
using ProxyForge.ViewModels;
using System.Reflection;
using System.Windows.Controls;

namespace ProxyForge.Views;

public partial class ProxyForgeSettingsView : UserControl
{
    public ProxyForgeSettingsView()
    {
        InitializeComponent();
        var host = PluginHost.Instance;
        DataContext = new SettingsViewModel(host?.CacheManager, host?.Budget, host?.VideoCache, new WpfDialogService());
        VersionText.Text = string.Concat("v", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
    }
}