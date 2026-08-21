namespace PlaycastCompanion;

static class Program
{
    private const string MutexName = @"Local\PlaycastCompanion";
    private const string ShowSettingsEventName = @"Local\PlaycastCompanion.ShowSettings";

    [STAThread]
    static void Main(string[] args)
    {
        NativeResolver.Register(); // vendor SDK DLL lookup for Logitech/Corsair

        // Hidden dev flag: enumerate LampArray (Dynamic Lighting) devices and exit.
        int lampsIdx = Array.FindIndex(args, a => a.Equals("--lamps", StringComparison.OrdinalIgnoreCase));
        if (lampsIdx >= 0)
        {
            var outPath = lampsIdx + 1 < args.Length
                ? args[lampsIdx + 1]
                : Path.Combine(Path.GetTempPath(), "playcast-lamps.txt");
            LampDiagnostics.RunAsync(outPath).GetAwaiter().GetResult();
            return;
        }

        // Hidden dev flag: render each settings tab to PNGs and exit.
        int snapshotIdx = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapshotIdx >= 0)
        {
            ApplicationConfiguration.Initialize();
            var dir = snapshotIdx + 1 < args.Length ? args[snapshotIdx + 1] : Path.GetTempPath();
            using var snapshotEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            var ctx = new TrayAppContext(false, snapshotEvent);
            var form = new MainForm(ctx)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-9000, -9000),
            };
            form.Show();
            Application.DoEvents();
            form.SaveSnapshots(dir);
            Application.Exit(); // hides the tray icon via ApplicationExit
            return;
        }

        // Local\ namespace => one instance per Windows session; instances in the
        // owner session and the guest session may coexist (that is intentional).
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance already runs in this session: ask it to show its
            // settings window instead of starting a duplicate...
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                evt.Set();
            }
            catch { }
            // ...but also wait briefly in case it is actually shutting down
            // (e.g. a settings restart) and we should take over.
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                    return;
            }
            catch (AbandonedMutexException)
            {
                // previous instance died without releasing; we own it now
            }
        }

        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);

        // Log instead of crashing with a dialog — a stray error dialog inside
        // the guest session would be visible to guests.
        Application.ThreadException += (_, e) => Log.Info($"UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"fatal: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Info($"task exception: {e.Exception?.GetBaseException().Message}");
            e.SetObserved();
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();

        // The HKLM Run entry starts us with --autostart (quiet). Any manual
        // launch without it opens the settings window (never in the guest
        // session — the context runs headless there).
        bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayAppContext(!autostart, showSettings));
    }
}
