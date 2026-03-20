namespace SquadMonitor;

/// <summary>
/// Describes the type of lifecycle transition that occurred for a session.
/// </summary>
public enum SessionLifecycleEventType
{
    /// <summary>A new session was first detected.</summary>
    Started,

    /// <summary>A session transitioned to <see cref="SessionStatus.Stale"/>.</summary>
    BecameStale,

    /// <summary>A session transitioned to <see cref="SessionStatus.Completed"/> (no longer discoverable).</summary>
    Ended,

    /// <summary>
    /// A previously stale session became active again
    /// (e.g., a handoff resumed an idle session).
    /// </summary>
    Resumed,
}

/// <summary>
/// Records a single lifecycle transition for a monitored session.
/// </summary>
public sealed class SessionLifecycleEvent
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required SessionLifecycleEventType EventType { get; init; }
    public required string SessionId { get; init; }
    public required string SessionName { get; init; }

    /// <summary>Optional detail string (e.g., duration at end, agent performing a handoff).</summary>
    public string? Detail { get; init; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] {EventType} — {SessionName} ({SessionId[..Math.Min(8, SessionId.Length)]}){(Detail is null ? "" : $" · {Detail}")}";
}
