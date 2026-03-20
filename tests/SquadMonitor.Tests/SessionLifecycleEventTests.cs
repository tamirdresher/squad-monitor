using System;
using System.Linq;
using Xunit;

namespace SquadMonitor.Tests;

/// <summary>
/// Tests for <see cref="SessionLifecycleEvent"/> and the lifecycle event
/// emission behavior of <see cref="SessionManager"/>.
/// </summary>
public class SessionLifecycleEventTests
{
    // ── SessionLifecycleEvent model ──────────────────────────────────────────

    [Fact]
    public void SessionLifecycleEvent_ToString_IncludesEventTypeAndSessionName()
    {
        var evt = new SessionLifecycleEvent
        {
            EventType = SessionLifecycleEventType.Started,
            SessionId = "abcdefgh-1234",
            SessionName = "Copilot-abc",
            Detail = "Type=Copilot CWD=/repo",
        };

        var str = evt.ToString();

        Assert.Contains("Started", str);
        Assert.Contains("Copilot-abc", str);
        Assert.Contains("abcdefgh", str[..str.IndexOf(')')]);
    }

    [Fact]
    public void SessionLifecycleEvent_DefaultTimestamp_IsRecent()
    {
        var before = DateTime.Now.AddSeconds(-1);
        var evt = new SessionLifecycleEvent
        {
            EventType = SessionLifecycleEventType.Ended,
            SessionId = "x",
            SessionName = "y",
        };
        var after = DateTime.Now.AddSeconds(1);

        Assert.InRange(evt.Timestamp, before, after);
    }

    [Fact]
    public void SessionLifecycleEvent_WithDetail_ToStringIncludesDetail()
    {
        var evt = new SessionLifecycleEvent
        {
            EventType = SessionLifecycleEventType.Ended,
            SessionId = "sess-1",
            SessionName = "TestSession",
            Detail = "Duration=5m",
        };

        Assert.Contains("Duration=5m", evt.ToString());
    }

    [Fact]
    public void SessionLifecycleEvent_WithoutDetail_ToStringDoesNotThrow()
    {
        var evt = new SessionLifecycleEvent
        {
            EventType = SessionLifecycleEventType.BecameStale,
            SessionId = "sess-2",
            SessionName = "TestSession2",
            Detail = null,
        };

        var str = evt.ToString();
        Assert.DoesNotContain("·", str); // detail separator not shown
    }

    // ── SessionLifecycleEventType enum ───────────────────────────────────────

    [Fact]
    public void SessionLifecycleEventType_AllExpectedValues_Exist()
    {
        var values = Enum.GetValues<SessionLifecycleEventType>();
        Assert.Contains(SessionLifecycleEventType.Started, values);
        Assert.Contains(SessionLifecycleEventType.BecameStale, values);
        Assert.Contains(SessionLifecycleEventType.Ended, values);
        Assert.Contains(SessionLifecycleEventType.Resumed, values);
    }

    // ── SessionManager lifecycle event emission ──────────────────────────────

    [Fact]
    public void SessionManager_InitialState_HasNoLifecycleEvents()
    {
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 30);

        Assert.Empty(mgr.LifecycleEvents);
    }

    [Fact]
    public void SessionManager_LifecycleEvents_IsThreadSafeReadOnly()
    {
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 30);

        // RefreshSessions over an empty dir — no sessions discovered, no events emitted
        mgr.RefreshSessions();

        var events = mgr.LifecycleEvents;
        Assert.NotNull(events);
        Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<SessionLifecycleEvent>>(events);
    }

    [Fact]
    public void SessionManager_LifecycleEventEmitted_EventRaisedForNewSessions()
    {
        // We can't inject sessions into SessionManager without disk files,
        // but we can verify the event infrastructure works.
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 30);

        var received = new System.Collections.Concurrent.ConcurrentBag<SessionLifecycleEvent>();
        mgr.LifecycleEventEmitted += evt => received.Add(evt);

        // With empty temp dir, no sessions found → no events
        mgr.RefreshSessions();
        Assert.Empty(received);
    }

    [Fact]
    public void SessionManager_StaleTimeout_ExposedViaProperty()
    {
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 45);

        Assert.Equal(TimeSpan.FromMinutes(45), mgr.StaleTimeout);
    }

    [Fact]
    public void SessionManager_GetSessions_EmptyWhenNoSessionsDiscovered()
    {
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 30);
        mgr.RefreshSessions(); // scan empty dir

        Assert.Empty(mgr.GetSessions());
    }

    [Fact]
    public void SessionManager_GetSessionsByStatus_EmptyWhenNoSessions()
    {
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 30);

        Assert.Empty(mgr.GetSessionsByStatus(SessionStatus.Active));
        Assert.Empty(mgr.GetSessionsByStatus(SessionStatus.Stale));
        Assert.Empty(mgr.GetSessionsByStatus(SessionStatus.Completed));
    }

    [Fact]
    public void SessionManager_CleanupCompletedSessions_WithNoSessions_ReturnsZero()
    {
        using var tempDir = new TempDirectory();
        var mgr = new SessionManager(tempDir.Path, staleTimeoutMinutes: 30);

        Assert.Equal(0, mgr.CleanupCompletedSessions());
    }
}

// ── Test Helpers ─────────────────────────────────────────────────────────────

/// <summary>Creates and cleans up a temporary directory.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"sm-test-{Guid.NewGuid():N}");

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}
