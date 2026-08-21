using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

namespace PlaycastCompanion;

/// <summary>
/// Dev harness (--lamps): enumerates every Windows Dynamic Lighting (HID
/// LampArray) device the OS exposes to us, with the details needed to reason
/// about overlap with vendor SDKs (Razer etc.) and whether we can actually
/// drive each one. Writes a report file and mirrors it to the log.
/// </summary>
public static class LampDiagnostics
{
    public static async Task RunAsync(string outPath)
    {
        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); Log.Info("lamps: " + s); }

        Line($"LampArray enumeration @ {DateTime.Now:HH:mm:ss} (user {Environment.UserName})");
        try
        {
            var selector = LampArray.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            Line($"selector matched {devices.Count} device(s)");

            int idx = 0;
            foreach (var info in devices)
            {
                idx++;
                Line($"[{idx}] name='{info.Name}' enabled={info.IsEnabled} id={Trunc(info.Id)}");
                try
                {
                    var lamp = await LampArray.FromIdAsync(info.Id);
                    if (lamp == null)
                    {
                        Line("     FromIdAsync returned null (device not openable by us)");
                        continue;
                    }
                    Line($"     kind={lamp.LampArrayKind} lamps={lamp.LampCount} connected={lamp.IsConnected} " +
                         $"vid=0x{lamp.HardwareVendorId:X4} pid=0x{lamp.HardwareProductId:X4}");

                    // Can we actually paint it? Try black, then restore to white so
                    // nothing is left dark by the probe.
                    try
                    {
                        lamp.SetColor(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                        await Task.Delay(400);
                        lamp.SetColor(Windows.UI.Color.FromArgb(255, 255, 255, 255));
                        Line("     SetColor OK — we CAN drive this device");
                    }
                    catch (Exception ex)
                    {
                        Line($"     SetColor FAILED ({ex.GetType().Name}: {ex.Message}) — likely owned by vendor app");
                    }
                }
                catch (Exception ex)
                {
                    Line($"     open failed ({ex.GetType().Name}: {ex.Message})");
                }
            }
            if (devices.Count == 0)
                Line("No LampArray devices — either no LampArray-capable hardware, or Windows Dynamic Lighting is OFF / a vendor app owns every device.");
        }
        catch (Exception ex)
        {
            Line($"enumeration error: {ex.GetType().Name}: {ex.Message}");
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
    }

    private static string Trunc(string id) => id.Length > 60 ? id[..57] + "..." : id;
}
