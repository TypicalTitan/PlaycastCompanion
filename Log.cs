namespace PlaycastCompanion;

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaycastCompanion");
    private static readonly string LogPath = Path.Combine(Dir, "playcast-companion.log");

    public static string LogFilePath => LogPath;
    public static event Action<string>? MessageLogged;

    public static void Info(string message)
    {
        // Subscribers are isolated from the ordinary log and from one another.
        foreach (var subscriber in MessageLogged?.GetInvocationList() ?? [])
        {
            try { ((Action<string>)subscriber)(message); } catch { }
        }
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                    fi.MoveTo(Path.Combine(Dir, "playcast-companion.old.log"), overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // logging must never take the app down; try one fallback location
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "playcast-companion.log"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
                }
                catch { }
            }
        }
    }
}
