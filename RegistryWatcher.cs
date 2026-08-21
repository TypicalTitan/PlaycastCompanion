using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PlaycastCompanion;

/// <summary>
/// Optional fallback signal: watches a registry key (default
/// HKLM\SOFTWARE\Playcast\GuestMode) via RegNotifyChangeKeyValue and reports
/// whether it currently indicates guest mode. If the key doesn't exist yet it
/// polls every 5 s until it appears. Changed fires only when the computed
/// active/inactive answer actually flips.
/// </summary>
public sealed class RegistryWatcher : IDisposable
{
    public event Action? Changed;

    private readonly RegistryWatchConfig _cfg;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;

    public RegistryWatcher(RegistryWatchConfig cfg)
    {
        _cfg = cfg;
        _thread = new Thread(WatchLoop) { IsBackground = true, Name = "RegistryWatcher" };
        _thread.Start();
    }

    public bool IsActive()
    {
        try
        {
            using var key = BaseKey().OpenSubKey(_cfg.SubKey, writable: false);
            if (key == null)
                return false;
            if (string.IsNullOrEmpty(_cfg.ValueName))
                return true; // subkey existing at all counts as active
            var value = key.GetValue(_cfg.ValueName);
            if (value == null)
                return false;
            var text = value.ToString() ?? "";
            return string.IsNullOrEmpty(_cfg.ActiveValue)
                ? text is not ("" or "0")
                : string.Equals(text, _cfg.ActiveValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void WatchLoop()
    {
        using var notify = new AutoResetEvent(false);
        bool lastActive = IsActive();
        while (!_stop.WaitOne(0))
        {
            if (RegOpenKeyExW(HiveHandle(), _cfg.SubKey, 0, KEY_NOTIFY, out var hKey) != 0)
            {
                // Key doesn't exist yet — poll until it appears.
                if (_stop.WaitOne(5000))
                    return;
                RaiseIfChanged(ref lastActive);
                continue;
            }
            try
            {
                while (!_stop.WaitOne(0))
                {
                    if (RegNotifyChangeKeyValue(hKey, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                            notify.SafeWaitHandle.DangerousGetHandle(), true) != 0)
                        break; // key was likely deleted; reopen
                    if (WaitHandle.WaitAny(new WaitHandle[] { notify, _stop }) == 1)
                        return;
                    RaiseIfChanged(ref lastActive);
                }
            }
            finally
            {
                RegCloseKey(hKey);
            }
        }
    }

    private void RaiseIfChanged(ref bool lastActive)
    {
        bool active = IsActive();
        if (active != lastActive)
        {
            lastActive = active;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(2000);
        _stop.Dispose();
    }

    private RegistryKey BaseKey() => _cfg.Hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        _ => Registry.LocalMachine,
    };

    private UIntPtr HiveHandle() => _cfg.Hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => (UIntPtr)0x80000001,
        _ => (UIntPtr)0x80000002, // HKLM
    };

    private const int KEY_NOTIFY = 0x0010;
    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyExW(UIntPtr hKey, string subKey, int options, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, int notifyFilter, IntPtr hEvent, bool asynchronous);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
