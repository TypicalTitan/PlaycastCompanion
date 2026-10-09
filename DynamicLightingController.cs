using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

namespace PlaycastCompanion;

/// <summary>
/// Windows Dynamic Lighting blackout via the OS-native HID LampArray API — no
/// vendor software of our own. Covers whatever devices expose LampArray
/// firmware. There is no explicit hand-back in the basic API: on release the
/// devices keep their last state until Windows Dynamic Lighting or the vendor
/// software repaints them (usually immediately if either is active).
///
/// Coexistence: many LampArray devices are the SAME physical hardware a
/// dedicated engine already drives (e.g. Razer peripherals, which Razer's
/// own bridge surfaces to WDL). Double-driving
/// them makes the two engines fight, so by default WDL skips any device whose
/// vendor already has an enabled engine — it becomes "everything the other
/// engines don't own" (motherboard, RAM, other-brand gear).
/// </summary>
public sealed class DynamicLightingController : LightingBackendBase
{
    // USB vendor IDs of the engines that own their own devices.
    private const ushort VidRazer = 0x1532;
    private const ushort VidCorsair = 0x1B1C;
    private const ushort VidLogitech = 0x046D;
    private const ushort VidSteelSeries = 0x1038;

    private readonly AppConfig _cfg;
    private string _holdingStatus = "Holding blackout";
    private string _lastSummary = "";
    private readonly WinRtOperationGate _operations = new();

    public DynamicLightingController(AppConfig cfg) => _cfg = cfg;

    public override string DisplayName => "Windows Dynamic Lighting";
    public override bool Enabled => _cfg.DynamicLighting.Enabled;
    protected override TimeSpan Tick => TimeSpan.FromSeconds(10);
    protected override string HoldingStatus => _holdingStatus;

    protected override string UnavailableText(Exception ex) =>
        ex.Message.Contains("no LampArray", StringComparison.Ordinal)
            ? "No LampArray devices found — retrying"
            : "Dynamic Lighting error — retrying";

    private HashSet<ushort> ExcludedVids()
    {
        var set = new HashSet<ushort>();
        if (!_cfg.DynamicLighting.ExcludeVendorOwnedDevices)
            return set;
        if (_cfg.RazerEnabled) set.Add(VidRazer);
        if (_cfg.Corsair.Enabled) set.Add(VidCorsair);
        if (_cfg.Logitech.Enabled) set.Add(VidLogitech);
        if (_cfg.SteelSeries.Enabled) set.Add(VidSteelSeries);
        return set;
    }

    protected override async Task ApplyTickAsync(CancellationToken ct)
    {
        var excluded = ExcludedVids();
        var devices = await _operations.InvokeAsync(
            () => DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector()),
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (devices.Count == 0)
            throw new InvalidOperationException("no LampArray devices connected");

        int painted = 0, skipped = 0;
        foreach (var info in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lamp = await _operations.InvokeAsync(() => LampArray.FromIdAsync(info.Id),
                    TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (lamp == null || !lamp.IsConnected)
                    continue;
                if (excluded.Contains(lamp.HardwareVendorId))
                {
                    skipped++;
                    continue; // its own engine handles this device
                }
                ct.ThrowIfCancellationRequested();
                lamp.SetColor(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                painted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // a single flaky device shouldn't stop the rest
            }
        }

        if (painted > 0)
        {
            _holdingStatus = skipped > 0
                ? $"Holding {painted} device(s); {skipped} left to their vendor engine"
                : "Holding blackout";
            LogSummary($"painting {painted} device(s), yielding {skipped} to their vendor engine");
            return;
        }

        // Nothing painted. If we deliberately skipped everything, that's the
        // intended coexistence outcome, not an error — report it calmly and
        // keep holding (a no-op) so the toggle stays on without spamming retries.
        if (skipped > 0)
        {
            _holdingStatus = $"Idle — all {skipped} device(s) handled by their vendor engine";
            LogSummary($"yielding all {skipped} device(s) to their vendor engine (nothing else to paint)");
            return;
        }

        // Devices exist but none were controllable (owned elsewhere / access denied).
        throw new InvalidOperationException("no controllable LampArray devices");
    }

    private void LogSummary(string summary)
    {
        if (summary == _lastSummary)
            return;
        _lastSummary = summary;
        Log.Info($"Dynamic Lighting: {summary}");
    }

    protected override Task ReleaseAsync()
    {
        // No explicit restore in the LampArray API — Windows Dynamic Lighting
        // or the vendor software repaints on its next update.
        _lastSummary = "";
        Log.Info("Dynamic Lighting hold ended (devices repaint on the next OS/vendor update)");
        return Task.CompletedTask;
    }
}
