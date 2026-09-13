using System.Windows.Controls;
using ProxyForge.ViewModels;

namespace ProxyForge.Views;

public partial class ProxyForgeSettingsView : UserControl
{
    public ProxyForgeSettingsView()
    {
        InitializeComponent();
        DataContext = new ProxyForgeSettingsViewModel();
    }
}
