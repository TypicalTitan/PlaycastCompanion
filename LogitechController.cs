using System.Runtime.InteropServices;

namespace PlaycastCompanion;

/// <summary>
/// Logitech blackout via the LED Illumination SDK (native DLL, resolved by
/// NativeResolver: a LogitechLedEnginesWrapper.dll next to the app, or
/// G HUB's bundled sdk_legacy_led_x64.dll). The SDK has real save/restore
/// primitives, so release puts the user's lighting back exactly.
/// </summary>
public sealed class LogitechController : LightingBackendBase
{
    private readonly LogitechConfig _cfg;
    private bool _initialized;
    private bool _saved;

    public LogitechController(LogitechConfig cfg) => _cfg = cfg;

    public override string DisplayName => "Logitech G";
    public override bool Enabled => _cfg.Enabled;

    protected override string UnavailableText(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException
            ? "LED SDK DLL not found — see the note on this card"
            : "G HUB not reachable — retrying";

    protected override Task ApplyTickAsync(CancellationToken ct)
    {
        if (!_initialized)
        {
            if (!LogiLedInit())
                throw new InvalidOperationException("LogiLedInit failed (is G HUB running?)");
            Thread.Sleep(150); // the SDK wants a beat after init
            _initialized = true;
        }
        if (!_saved)
        {
            LogiLedSaveCurrentLighting();
            _saved = true;
        }
        if (!LogiLedSetLighting(0, 0, 0))
            throw new InvalidOperationException("LogiLedSetLighting failed");
        return Task.CompletedTask;
    }

    protected override Task ReleaseAsync()
    {
        try
        {
            if (_initialized)
            {
                if (_saved)
                    LogiLedRestoreLighting();
                LogiLedShutdown();
                Log.Info("Logitech lighting restored");
            }
        }
        catch { }
        _initialized = false;
        _saved = false;
        return Task.CompletedTask;
    }

    protected override void OnConnectionLost()
    {
        try { if (_initialized) LogiLedShutdown(); } catch { }
        _initialized = false;
        _saved = false;
    }

    [DllImport("LOGI_LED", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedInit();

    [DllImport("LOGI_LED", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedSaveCurrentLighting();

    [DllImport("LOGI_LED", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedSetLighting(int redPercentage, int greenPercentage, int bluePercentage);

    [DllImport("LOGI_LED", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LogiLedRestoreLighting();

    [DllImport("LOGI_LED", CallingConvention = CallingConvention.Cdecl)]
    private static extern void LogiLedShutdown();
}
