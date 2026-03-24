using System.Reflection;
using System.Windows.Controls;
using ZeroDiskProxy.Settings;
using ZeroDiskProxy.ViewModels;

namespace ZeroDiskProxy.Views;

public partial class ZeroDiskProxySettingsView : UserControl
{
    public ZeroDiskProxySettingsView()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
        CpuCoreSlider.Max = ZeroDiskProxySettings.MaxCpuCoreCount;
        VersionText.Text = string.Concat("v", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
    }
}