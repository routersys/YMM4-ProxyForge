using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ProxyForge.Tests;

internal static class Ymm4AssemblyResolver
{
    private static readonly string Ymm4Directory = ReadYmm4Directory();

    internal static bool IsConfigured => Ymm4Directory.Length != 0 && Directory.Exists(Ymm4Directory);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!IsConfigured)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var simpleName = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(simpleName))
            return null;

        var candidate = Path.Combine(Ymm4Directory, simpleName + ".dll");
        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    }

    private static string ReadYmm4Directory()
    {
        foreach (var attribute in typeof(Ymm4AssemblyResolver).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key == "YMM4DirPath")
                return attribute.Value ?? string.Empty;
        }

        return string.Empty;
    }
}
