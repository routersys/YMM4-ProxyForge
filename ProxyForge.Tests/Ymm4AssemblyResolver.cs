using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ProxyForge.Tests;

internal static class Ymm4AssemblyResolver
{
    const string MetadataKey = "Ymm4Directory";
    const string AssemblyExtension = ".dll";

    public static string Directory { get; } = ReadDirectory();

    public static bool IsConfigured => Directory.Length != 0 && System.IO.Directory.Exists(Directory);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!IsConfigured)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += (_, arguments) =>
        {
            var name = new AssemblyName(arguments.Name).Name;
            if (name is null)
                return null;

            var candidate = Path.Combine(Directory, name + AssemblyExtension);
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }

    static string ReadDirectory() => typeof(Ymm4AssemblyResolver).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))
        ?.Value ?? string.Empty;
}
