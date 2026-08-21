using System.Reflection;
using System.Runtime.InteropServices;

namespace PlaycastCompanion;

/// <summary>
/// Maps the logical DllImport names used by the Logitech and Corsair backends
/// to whatever copy of the vendor SDK DLL is actually installed: a copy
/// dropped next to the app first, then the vendor software's own install.
/// Only one DllImportResolver may be registered per assembly, so both brands
/// share this one.
/// </summary>
public static class NativeResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = name switch
        {
            "LOGI_LED" =>
            [
                Path.Combine(AppContext.BaseDirectory, "LogitechLedEnginesWrapper.dll"),
                @"C:\Program Files\LGHUB\sdk_legacy_led_x64.dll",
                @"C:\Program Files\Logitech Gaming Software\SDK\LED\x64\LogitechLedEnginesWrapper.dll",
            ],
            "CUESDK" =>
            [
                Path.Combine(AppContext.BaseDirectory, "CUESDK.x64_2017.dll"),
                Path.Combine(AppContext.BaseDirectory, "CUESDK_2017.dll"),
                @"C:\Program Files\Corsair\CORSAIR iCUE 5 Software\CUESDK.x64_2017.dll",
                @"C:\Program Files\Corsair\CORSAIR iCUE 4 Software\CUESDK.x64_2017.dll",
            ],
            _ => [],
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return IntPtr.Zero; // fall through to default probing
    }
}
