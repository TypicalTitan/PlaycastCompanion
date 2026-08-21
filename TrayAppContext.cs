using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PlaycastCompanion;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly List<ILightingBackend> _lighting;
    private readonly DiscordPresence? _discord;
    private readonly DiscordShimServer? _shim;
    private readonly DiscordChannelListener? _channel;
    private readonly SessionWatcher _sessions;
    private readonly RegistryWatcher? _registry;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly SemaphoreSlim _evalGate = new(1, 1);
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon? _tray;
    private readonly Icon _iconIdle;
    private readonly Icon _iconStealth;
    private MainForm? _form;

    public AppConfig Config => _cfg;
    public IReadOnlyList<ILightingBackend> Lighting => _lighting;
    public DiscordPresence? Discord => _discord;
    public bool GuestActive { get; private set; }
    public Image SquidImage { get; }
    public Icon AppIcon => _iconIdle;

    /// <summary>
    /// True when running inside the target account's own session: fully
    /// functional, but shows no tray icon or window so the guest session stays
    /// visually clean and offers nothing to click.
    /// </summary>
    public bool IsHeadless { get; }

    public TrayAppContext(bool openSettings, EventWaitHandle showSettingsSignal)
    {
        _cfg = AppConfig.Load();
        IsHeadless = string.Equals(Environment.UserName, _cfg.TargetUsername, StringComparison.OrdinalIgnoreCase);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Log.Info($"--- started (pid {Environment.ProcessId}, user {Environment.UserName}, target {_cfg.TargetUsername}{(IsHeadless ? ", headless" : "")}) ---");

        SquidImage = Branding.LoadSquid();
        _iconIdle = Branding.MakeSquidIcon(SquidImage, 1f);
        _iconStealth = Branding.MakeSquidIcon(SquidImage, 0.35f);

        _lighting =
        [
            new ChromaController(_cfg),
            new SteelSeriesController(_cfg.SteelSeries),
            new LogitechController(_cfg.Logitech),
            new CorsairController(_cfg.Corsair),
            new OpenRgbController(_cfg.OpenRgb),
            new DynamicLightingController(_cfg),
        ];
        // Discord runs in the owner's session only; the guest session has no
        // Discord client (and its pipe wouldn't be ours to talk to anyway).
        _discord = IsHeadless ? null : new DiscordPresence(_cfg.Discord);

        // Game pass-through (default off): the shim runs ONLY in the guest
        // session; the relay listener + name resolver run ONLY in the owner
        // session and feed captured game names into the presence above.
        if (_cfg.Discord.GamePassthroughEnabled)
        {
            if (IsHeadless)
            {
                _shim = new DiscordShimServer(_cfg);
                _shim.Start();
            }
            else if (_discord != null && _cfg.Discord.Enabled)
            {
                _channel = new DiscordChannelListener(_cfg, _discord);
                _channel.Start();
            }
        }
        _sessions = new SessionWatcher(_cfg);
        _sessions.SessionsChanged += () => _ = EvaluateAsync("session change");

        if (_cfg.RegistryWatch.Enabled)
        {
            _registry = new RegistryWatcher(_cfg.RegistryWatch);
            _registry.Changed += () => _ = EvaluateAsync("registry change");
        }

        if (!IsHeadless)
        {
            var menu = new ContextMenuStrip();
            var open = menu.Items.Add("Open Playcast Companion", null, (_, _) => OpenSettings());
            open.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add("Preview guest mode (10 s)", null, (_, _) => _ = TestGuestModeAsync());
            menu.Items.Add("Check again now", null, (_, _) => _ = EvaluateAsync("manual"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());

            _tray = new NotifyIcon
            {
                Icon = _iconIdle,
                Text = "Playcast Companion",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => OpenSettings();
        }

        // Safety net: periodic re-check in case a WTS notification was missed.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _pollTimer.Tick += (_, _) => _ = EvaluateAsync("poll");
        _pollTimer.Start();

        // Release promptly when this session itself logs off or the machine
        // shuts down; if the process is killed harder than this, the Chroma
        // session times out on its own ~15 s later.
        SystemEvents.SessionEnded += OnSessionEnded;
        Application.ApplicationExit += OnApplicationExit;

        // A second launch of the exe signals this event instead of starting a
        // duplicate; respond by bringing up the settings window.
        var signalThread = new Thread(() =>
        {
            try
            {
                while (showSettingsSignal.WaitOne())
                {
                    if (!IsHeadless)
                        _ui.Post(_ => OpenSettings(), null);
                }
            }
            catch
            {
                // handle disposed during shutdown
            }
        })
        { IsBackground = true, Name = "ShowSettingsSignal" };
        signalThread.Start();

        _ = EvaluateAsync("startup");
        if (openSettings && !IsHeadless)
            OpenSettings();
    }

    public void OpenSettings()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(this);
            _form.Show();
        }
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    /// <summary>Ten-second preview of the full guest-mode look: lights off and
    /// (if configured) the Discord presence live.</summary>
    public async Task TestGuestModeAsync()
    {
        Log.Info("guest-mode preview started");
        foreach (var backend in _lighting)
            backend.StartBlackout(); // no-ops when the backend is disabled
        _discord?.SetActive();
        await Task.Delay(10_000);
        await EvaluateAsync("preview end");
    }

    public void OpenLog()
    {
        try
        {
            Log.Info("log opened from UI");
            Process.Start(new ProcessStartInfo(Log.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Info($"open log failed: {ex.Message}");
        }
    }

    public void RestartApp()
    {
        Log.Info("restart requested (settings changed)");
        Application.Restart(); // the mutex wait in Main handles the handoff
    }

    private async Task EvaluateAsync(string reason)
    {
        await _evalGate.WaitAsync();
        try
        {
            bool guestActive = _sessions.IsTargetUserLoggedOn() || (_registry?.IsActive() ?? false);
            if (guestActive)
            {
                if (!GuestActive)
                    Log.Info($"guest session active ({reason}) -> stealth on");
                foreach (var backend in _lighting)
                {
                    if (backend.Enabled && !backend.IsHolding)
                        backend.StartBlackout();
                    else if (!backend.Enabled && backend.IsHolding)
                        await backend.StopBlackoutAsync(); // toggled off mid-hold
                }
            }
            else
            {
                var holding = _lighting.Where(b => b.IsHolding).ToList();
                if (holding.Count > 0)
                {
                    Log.Info($"guest session gone ({reason}) -> restoring {holding.Count} lighting system(s)");
                    await Task.WhenAll(holding.Select(b => b.StopBlackoutAsync()));
                }
            }
            if (_discord != null)
            {
                if (guestActive)
                    _discord.SetActive();
                else
                    await _discord.ClearAsync();
            }
            GuestActive = guestActive;
            _ui.Post(_ =>
            {
                if (_tray == null)
                    return;
                _tray.Icon = guestActive ? _iconStealth : _iconIdle;
                _tray.Text = guestActive
                    ? "Playcast Companion — guest hosting, stealth on"
                    : "Playcast Companion — all quiet";
            }, null);
        }
        catch (Exception ex)
        {
            Log.Info($"evaluate failed: {ex.Message}");
        }
        finally
        {
            _evalGate.Release();
        }
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        try { Task.Run(StopAllLightingAsync).Wait(3000); } catch { }
    }

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        try { if (_tray != null) _tray.Visible = false; } catch { }
        try { Task.Run(StopAllLightingAsync).Wait(3000); } catch { }
        try { Task.Run(() => _discord?.ClearAsync() ?? Task.CompletedTask).Wait(2000); } catch { }
        try { Task.Run(() => _shim?.StopAsync() ?? Task.CompletedTask).Wait(2000); } catch { }
        try { Task.Run(() => _channel?.StopAsync() ?? Task.CompletedTask).Wait(2000); } catch { }
    }

    private Task StopAllLightingAsync() =>
        Task.WhenAll(_lighting.Select(b => b.StopBlackoutAsync()));

    private void ExitApp()
    {
        SystemEvents.SessionEnded -= OnSessionEnded;
        _pollTimer.Stop();
        _sessions.Dispose();
        _registry?.Dispose();
        _form?.Close();
        if (_tray != null)
            _tray.Visible = false;
        try { Task.Run(StopAllLightingAsync).Wait(5000); } catch { }
        try { Task.Run(() => _discord?.ClearAsync() ?? Task.CompletedTask).Wait(3000); } catch { }
        try { Task.Run(() => _shim?.StopAsync() ?? Task.CompletedTask).Wait(2000); } catch { }
        try { Task.Run(() => _channel?.StopAsync() ?? Task.CompletedTask).Wait(2000); } catch { }
        _tray?.Dispose();
        Log.Info("--- exited ---");
        ExitThread();
    }
}
