namespace MedicalMonitorServer.Services;

/// <summary>Remembers when the last vitals reading arrived, from any source (uMec feed or REST ingestion).</summary>
public sealed class FeedStatus
{
    private long _lastTicks;

    public DateTimeOffset? LastDataAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void MarkReceived() => Volatile.Write(ref _lastTicks, DateTimeOffset.UtcNow.UtcTicks);
}
