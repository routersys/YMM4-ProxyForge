using ProxyForge.Interfaces;
using System.Windows;

namespace ProxyForge.Services;

internal static class WindowThemeRegistry
{
    private static IWindowThemeService _service = NullWindowThemeService.Instance;

    internal static void Register(IWindowThemeService service)
        => Interlocked.Exchange(ref _service, service ?? NullWindowThemeService.Instance);

    internal static void Bind(Window window)
        => _service.Bind(window);

    private sealed class NullWindowThemeService : IWindowThemeService
    {
        internal static readonly NullWindowThemeService Instance = new();
        private NullWindowThemeService() { }
        public void Bind(Window window) { }
    }
}