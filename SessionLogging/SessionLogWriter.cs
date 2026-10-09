using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlaycastCompanion;

/// <summary>Bounded, per-session JSONL storage. Failed writes are surfaced without escaping to the app.</summary>
public sealed class SessionLogWriter : IDisposable
{
    private const int LimitMarkerReserve = 2048;
    private static readonly HashSet<string> Categories =
        ["realtime", "ipc", "scripts", "games", "diagnostics", "session"];
    private static readonly Regex GeneratedDirectory = new(
        @"^\d{8}T\d{6}\.\d{3}Z_[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly SessionLoggingConfig _config;
    private readonly SessionLogRedactor _redactor = new();
    private readonly Dictionary<string, CategoryFile> _files = new();
    private long _totalBytes;
    private bool _limitReached;
    private bool _disposed;
    private bool _storageFaulted;

    public string DirectoryPath { get; }
    public string? LastError { get; private set; }
    public long BytesWritten { get { lock (_gate) return _totalBytes; } }

    public SessionLogWriter(SessionLoggingConfig config, string? baseDirectory = null)
    {
        _config = config;
        var root = Path.GetFullPath(baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaycastCompanion", "sessions"));
        DirectoryPath = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss.fff'Z'}_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            RestrictAccess(DirectoryPath);
            DeleteExpiredSessions(root);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            LastError = $"Session storage unavailable: {ex.Message}";
        }
    }

    public bool Write(string category, object? payload, bool includeSecrets = false)
    {
        lock (_gate)
        {
            if (_disposed || _limitReached || _storageFaulted || LastError?.StartsWith("Session storage unavailable:") == true)
                return false;
            try
            {
                var record = new JsonObject
                {
                    ["recordedAtUtc"] = DateTime.UtcNow.ToString("O"),
                    ["includeSecrets"] = includeSecrets,
                    ["payload"] = _redactor.Redact(payload, includeSecrets),
                };
                var bytes = Encoding.UTF8.GetBytes(record.ToJsonString() + "\n");
                if (bytes.Length > _config.MaxFileBytes)
                    return WriteOversize(category, bytes.Length);
                if (_totalBytes + bytes.Length > _config.MaxSessionBytes - LimitMarkerReserve)
                    return ReachStorageLimit();
                Append(Categories.Contains(category) ? category : "diagnostics", bytes);
                return true;
            }
            catch (Exception ex) when (IsStorageException(ex) || ex is JsonException or NotSupportedException)
            {
                LastError = $"Session write failed: {ex.Message}";
                // A partial write/failed flush cannot be counted reliably. Stop this
                // session's disk writes so repeated failures cannot exceed the cap.
                if (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    _storageFaulted = true;
                return false;
            }
        }
    }

    private bool WriteOversize(string category, int bytes)
    {
        var marker = new { @event = "recordRejected", category, bytes, maximum = _config.MaxFileBytes };
        var encoded = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { recordedAtUtc = DateTime.UtcNow, includeSecrets = false, payload = marker }) + "\n");
        if (_totalBytes + encoded.Length > _config.MaxSessionBytes - LimitMarkerReserve)
            return ReachStorageLimit();
        Append("diagnostics", encoded);
        LastError = "A diagnostic record exceeded the per-file limit and was rejected.";
        return false;
    }

    private bool ReachStorageLimit()
    {
        _limitReached = true;
        LastError = "Session storage limit reached; further records are dropped.";
        var marker = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            recordedAtUtc = DateTime.UtcNow,
            includeSecrets = false,
            payload = new { @event = "sessionStorageLimitReached", maximumBytes = _config.MaxSessionBytes },
        }) + "\n");
        if (_totalBytes + marker.Length <= _config.MaxSessionBytes)
            Append("diagnostics", marker);
        return false;
    }

    private void Append(string category, byte[] bytes)
    {
        if (!_files.TryGetValue(category, out var file))
        {
            file = new CategoryFile();
            _files[category] = file;
        }
        if (file.Stream == null || file.Bytes + bytes.Length > _config.MaxFileBytes)
        {
            file.Stream?.Dispose();
            var suffix = file.Index++ == 0 ? "" : $".{file.Index - 1:D3}";
            file.Stream = new FileStream(Path.Combine(DirectoryPath, category + suffix + ".jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
            file.Bytes = 0;
        }
        file.Stream.Write(bytes);
        file.Stream.Flush();
        file.Bytes += bytes.Length;
        _totalBytes += bytes.Length;
    }

    private void DeleteExpiredSessions(string root)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.OrdinalIgnoreCase)
                || !GeneratedDirectory.IsMatch(Path.GetFileName(fullPath)) || fullPath == DirectoryPath)
                continue;
            try
            {
                var directory = new DirectoryInfo(fullPath);
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0 && directory.LastWriteTimeUtc < cutoff
                    && HasSafeDescendants(directory, fullPath))
                    directory.Delete(recursive: true);
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                LastError = $"Session retention cleanup failed: {ex.Message}";
            }
        }
    }

    private static bool HasSafeDescendants(DirectoryInfo directory, string sessionPath)
    {
        var prefix = sessionPath + Path.DirectorySeparatorChar;
        var remaining = new Stack<DirectoryInfo>();
        remaining.Push(directory);
        while (remaining.TryPop(out var current))
        {
            foreach (var child in current.EnumerateFileSystemInfos())
            {
                if (!Path.GetFullPath(child.FullName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || (child.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (child is DirectoryInfo nested) remaining.Push(nested);
            }
        }
        return true;
    }

    private static void RestrictAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User == null) throw new UnauthorizedAccessException("Cannot identify the log owner.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(identity.User);
        foreach (var sid in new[] { identity.User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private static bool IsStorageException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or InvalidOperationException;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var file in _files.Values)
            {
                try { file.Stream?.Dispose(); }
                catch (IOException ex) { LastError = $"Session close failed: {ex.Message}"; }
            }
            _files.Clear();
        }
    }

    private sealed class CategoryFile
    {
        public FileStream? Stream { get; set; }
        public int Index { get; set; }
        public long Bytes { get; set; }
    }
}
