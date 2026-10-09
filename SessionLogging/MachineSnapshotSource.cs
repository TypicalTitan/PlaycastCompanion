using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PlaycastCompanion;

/// <summary>Combines passive, independently testable session and machine observations.</summary>
public sealed class MachineSnapshotSource : ISessionSnapshotSource
{
    private readonly string _targetUsername;
    private readonly bool _includeDisconnected;
    private readonly IGameInventorySource _games;
    private readonly ISessionInventorySource _sessions;
    private readonly IProcessSnapshotSource _processes;
    private readonly INonsoleRegistrySource _registry;
    private readonly TimeProvider _clock;

    public MachineSnapshotSource(string targetUsername, bool includeDisconnected = false)
        : this(targetUsername, includeDisconnected, new GameInventorySource(targetUsername),
            new WindowsSessionInventorySource(), new WindowsProcessSnapshotSource(),
            new WindowsNonsoleRegistrySource(), TimeProvider.System) { }

    public MachineSnapshotSource(string targetUsername, bool includeDisconnected,
        IGameInventorySource games, ISessionInventorySource sessions,
        IProcessSnapshotSource processes, INonsoleRegistrySource registry, TimeProvider clock)
    {
        _targetUsername = targetUsername;
        _includeDisconnected = includeDisconnected;
        _games = games;
        _sessions = sessions;
        _processes = processes;
        _registry = registry;
        _clock = clock;
    }

    public ValueTask<object> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = _games.Capture(cancellationToken);
        var sessions = _sessions.Capture(_targetUsername, _includeDisconnected);
        var targetIds = sessions.Sessions.Where(session => session.IsTarget)
            .Select(session => session.SessionId).ToHashSet();
        var processes = _processes.Capture(targetIds, inventory.Games, cancellationToken);
        var registry = _registry.Capture();
        var now = _clock.GetUtcNow();
        return ValueTask.FromResult<object>(new
        {
            capturedAtUtc = now,
            targetUsername = _targetUsername,
            targetSessionIds = targetIds.Order().ToArray(),
            sessions,
            installedGames = inventory.Games,
            installedGameSources = inventory.Sources,
            inventoryLimitations = inventory.Limitations,
            runningGames = processes.RunningGames,
            runningGameDetectionStatus = sessions.Status == "available" ? processes.Status : "unknown_session_inventory",
            playcastProcesses = processes.PlaycastProcesses,
            processCoverage = new { processes.Status, processes.Errors },
            nonsoleRegistry = registry,
            heartbeatAgeSeconds = registry.LastHeartbeatUtc.Value is DateTimeOffset heartbeat
                ? (now - heartbeat).TotalSeconds : (double?)null,
            machine = CaptureMachine(),
            companion = CaptureCompanion(),
            disks = CaptureDisks()
        });
    }

    internal static object CaptureTerminalFallback(string error) => new
    {
        capturedAtUtc = DateTimeOffset.UtcNow,
        snapshotFailed = error,
        machine = CaptureMachine(),
        companion = CaptureCompanion(),
        installedGames = (object?)null,
        runningGames = (object?)null,
        installedGameSources = (object?)null,
        runningGameDetectionStatus = "unavailable",
        inventoryLimitations = new[] { "Terminal inventory capture failed; game state is unknown." },
        processCoverage = new { Status = "unavailable", Errors = new[] { new SnapshotError("terminal_snapshot", "unavailable", error) } }
    };

    private static object CaptureMachine() => new
    {
        operatingSystem = RuntimeInformation.OSDescription,
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        logicalProcessorCount = Environment.ProcessorCount,
        uptimeSeconds = Environment.TickCount64 / 1000d,
        managedRuntime = RuntimeInformation.FrameworkDescription
    };

    private static object CaptureCompanion()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            pid = process.Id,
            sessionId = Observe<int?>(() => process.SessionId),
            cpuSeconds = Observe<double?>(() => process.TotalProcessorTime.TotalSeconds),
            workingSetBytes = Observe<long?>(() => process.WorkingSet64),
            privateMemoryBytes = Observe<long?>(() => process.PrivateMemorySize64),
            startedAtUtc = Observe<DateTimeOffset?>(() => new DateTimeOffset(process.StartTime.ToUniversalTime())),
            managedHeapBytes = GC.GetTotalMemory(false),
            threadCount = Observe<int?>(() => process.Threads.Count),
            handleCount = Observe<int?>(() => process.HandleCount)
        };
    }

    private static object CaptureDisks()
    {
        var volumes = new List<object>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                volumes.Add(new
                {
                    drive = drive.Name,
                    freeBytes = Observe<long?>(() => drive.AvailableFreeSpace),
                    totalBytes = Observe<long?>(() => drive.TotalSize)
                });
            }
            return new { status = "available", volumes, error = (string?)null };
        }
        catch (Exception error)
        { return new { status = SnapshotFailures.Status(error), volumes, error = error.Message }; }
    }

    private static ObservedMetric<T> Observe<T>(Func<T?> read)
    {
        try { return new(read(), "available"); }
        catch (Exception error) { return new(default, SnapshotFailures.Status(error), error.Message); }
    }
}
