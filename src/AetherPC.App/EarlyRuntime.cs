using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AetherPC.App;

/// <summary>
/// Antes de Main: cultura UI por defecto e índice de carpetas native/runtimes si existen.
/// </summary>
internal static class EarlyRuntime
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);
            foreach (var rel in new[]
                     {
                         "runtimes\\win-x64\\native",
                         "runtimes\\win\\native",
                         "native"
                     })
            {
                var dir = Path.Combine(baseDir, rel);
                if (Directory.Exists(dir))
                    AddDllDirectory(dir);
            }
        }
        catch { /* */ }

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        }
        catch { /* */ }
    }
}
