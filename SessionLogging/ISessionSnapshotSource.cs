namespace PlaycastCompanion;

public interface ISessionSnapshotSource
{
    ValueTask<object> CaptureAsync(CancellationToken cancellationToken);
}
