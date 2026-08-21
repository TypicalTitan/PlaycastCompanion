using System.Runtime.InteropServices;

namespace PlaycastCompanion;

/// <summary>
/// Corsair blackout via the CUE SDK v3 native DLL (CUESDK.x64_2017.dll,
/// resolved by NativeResolver — dropped next to the app or found in iCUE's
/// folder). Exclusive lighting control makes iCUE stop animating while we
/// hold; CorsairReleaseControl hands the user's iCUE profile straight back.
/// </summary>
public sealed class CorsairController : LightingBackendBase
{
    private const int ExclusiveLightingControl = 1; // CAM_ExclusiveLightingControl

    private readonly CorsairConfig _cfg;
    private bool _connected;

    public CorsairController(CorsairConfig cfg) => _cfg = cfg;

    public override string DisplayName => "Corsair iCUE";
    public override bool Enabled => _cfg.Enabled;

    protected override string UnavailableText(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException
            ? "CUE SDK DLL not found — see the note on this card"
            : "iCUE not reachable — retrying";

    protected override Task ApplyTickAsync(CancellationToken ct)
    {
        if (!_connected)
        {
            var details = CorsairPerformProtocolHandshake();
            if (details.ServerProtocolVersion == 0)
                throw new InvalidOperationException($"handshake failed (is iCUE running? error {CorsairGetLastError()})");
            if (!CorsairRequestControl(ExclusiveLightingControl))
                throw new InvalidOperationException("iCUE refused exclusive lighting control");
            _connected = true;
        }

        int devices = CorsairGetDeviceCount();
        for (int i = 0; i < devices; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pPositions = CorsairGetLedPositionsByDeviceIndex(i);
            if (pPositions == IntPtr.Zero)
                continue;
            var header = Marshal.PtrToStructure<CorsairLedPositions>(pPositions);
            if (header.NumberOfLeds <= 0 || header.LedPosition == IntPtr.Zero)
                continue;
            var colors = new CorsairLedColor[header.NumberOfLeds];
            int stride = Marshal.SizeOf<CorsairLedPosition>();
            for (int led = 0; led < header.NumberOfLeds; led++)
            {
                var pos = Marshal.PtrToStructure<CorsairLedPosition>(header.LedPosition + led * stride);
                colors[led] = new CorsairLedColor { LedId = pos.LedId };
            }
            CorsairSetLedsColorsBufferByDeviceIndex(i, colors.Length, colors);
        }
        CorsairSetLedsColorsFlushBuffer();
        return Task.CompletedTask;
    }

    protected override Task ReleaseAsync()
    {
        if (_connected)
        {
            try
            {
                CorsairReleaseControl(ExclusiveLightingControl);
                Log.Info("Corsair lighting control released; iCUE profile restored");
            }
            catch { }
            _connected = false;
        }
        return Task.CompletedTask;
    }

    protected override void OnConnectionLost() => _connected = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairProtocolDetails
    {
        public IntPtr SdkVersion;
        public IntPtr ServerVersion;
        public int SdkProtocolVersion;
        public int ServerProtocolVersion;
        [MarshalAs(UnmanagedType.I1)] public bool BreakingChanges;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairLedPositions
    {
        public int NumberOfLeds;
        public IntPtr LedPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairLedPosition
    {
        public int LedId;
        public double Top;
        public double Left;
        public double Height;
        public double Width;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CorsairLedColor
    {
        public int LedId;
        public int R;
        public int G;
        public int B;
    }

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    private static extern CorsairProtocolDetails CorsairPerformProtocolHandshake();

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CorsairGetLastError();

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CorsairGetDeviceCount();

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CorsairGetLedPositionsByDeviceIndex(int deviceIndex);

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CorsairSetLedsColorsBufferByDeviceIndex(int deviceIndex, int size, CorsairLedColor[] ledsColors);

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CorsairSetLedsColorsFlushBuffer();

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CorsairRequestControl(int accessMode);

    [DllImport("CUESDK", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CorsairReleaseControl(int accessMode);
}
