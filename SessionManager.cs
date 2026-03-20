using System.Globalization;
using System.Text.Json;

namespace SquadMonitor;

/// <summary>
/// Represents the lifecycle status of a monitored session.
/// </summary>
public enum SessionStatus
{
    Active,
    Stale,
    Completed
}

/// <summary>
/// Represents a single Copilot CLI / Agency session being monitored.
/// </summary>
public sealed class Session
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime LastActivity { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public string MachineName { get; init; } = Environment.MachineName;
    public string FullPath { get; init; } = "";
    public string SessionType { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public int AgentCount { get; set; }
    public int McpServerCount { get; set; }

    /// <summary>Short display ID (first 8 chars of the full session ID).</summary>
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;

    /// <summary>How long the session has been running.</summary>
    public TimeSpan Duration => LastActivity > StartTime
        ? LastActivity - StartTime
        : DateTime.Now - StartTime;
}

/// <summary>
/// Discovers and manages Copilot CLI / Agency sessions by scanning
/// well-known session-state and log directories on disk.
/// Emits <see cref="SessionLifecycleEvent"/> records as sessions are discovered, stall, or end.
/// </summary>
public sealed class SessionManager
{
    private readonly string _userProfile;
    private readonly TimeSpan _staleTimeout;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionStatus> _previousStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SessionLifecycleEvent> _lifecycleEvents = new();
    private readonly object _lock = new();

    /// <summary>
    /// Chronological list of all lifecycle events emitted since this manager was created.
    /// Access is thread-safe — the list is only appended inside <c>_lock</c>.
    /// </summary>
    public IReadOnlyList<SessionLifecycleEvent> LifecycleEvents
    {
        get { lock (_lock) { return _lifecycleEvents.ToList().AsReadOnly(); } }
    }

    /// <summary>
    /// Raised synchronously (inside the lock) whenever a lifecycle event is emitted.
    /// Subscribers should be fast; do not call back into <see cref="SessionManager"/> from this handler.
    /// </summary>
    public event Action<SessionLifecycleEvent>? LifecycleEventEmitted;

    /// <summary>
    /// Creates a new <see cref="SessionManager"/>.
    /// </summary>
    /// <param name="userProfile">User profile directory (typically <c>~</c>).</param>
    /// <param name="staleTimeoutMinutes">
    /// Minutes of inactivity after which a session is marked <see cref="SessionStatus.Stale"/>.
    /// Defaults to 30 minutes.
    /// </param>
    public SessionManager(string userProfile, int staleTimeoutMinutes = 30)
    {
        _userProfile = userProfile;
        _staleTimeout = TimeSpan.FromMinutes(staleTimeoutMinutes);
    }

    /// <summary>Current stale-timeout threshold.</summary>
    public TimeSpan StaleTimeout => _staleTimeout;

    /// <summary>
    /// Scans the file system for active Copilot CLI and Agency sessions,
    /// updates internal state, and returns all known sessions.
    /// </summary>
    public IReadOnlyList<Session> RefreshSessions()
    {
        var now = DateTime.Now;
        var discovered = new List<Session>();

        // 1. Scan Agency sessions (~/.agency/logs)
        discovered.AddRange(ScanAgencySessions(now));

        // 2. Scan Copilot CLI session-state dirs (~/.copilot/session-state/)
        discovered.AddRange(ScanCopilotSessionStateDirs(now));

        // 3. Scan Copilot CLI log dirs (~/.copilot/logs/)
        discovered.AddRange(ScanCopilotLogDirs(now));

        lock (_lock)
        {
            // Merge discovered sessions into the tracked set
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in discovered)
            {
                seenIds.Add(session.Id);

                if (_sessions.TryGetValue(session.Id, out var existing))
                {
                    var prevStatus = existing.Status;
                    // Update mutable fields
                    existing.LastActivity = session.LastActivity;
                    existing.AgentCount = session.AgentCount;
                    existing.McpServerCount = session.McpServerCount;
                    existing.Status = ClassifyStatus(existing, now);

                    // Emit lifecycle events on status transitions
                    EmitTransitionEvents(existing, prevStatus, existing.Status);
                }
                else
                {
                    session.Status = ClassifyStatus(session, now);
                    _sessions[session.Id] = session;
                    _previousStatuses[session.Id] = session.Status;
                    EmitEvent(new SessionLifecycleEvent
                    {
                        EventType = SessionLifecycleEventType.Started,
                        SessionId = session.Id,
                        SessionName = session.Name,
                        Detail = $"Type={session.SessionType} CWD={session.WorkingDirectory}",
                    });
                }
            }

            // Mark sessions that were not rediscovered as completed
            foreach (var kvp in _sessions)
            {
                if (!seenIds.Contains(kvp.Key) && kvp.Value.Status != SessionStatus.Completed)
                {
                    var prevStatus = kvp.Value.Status;
                    kvp.Value.Status = SessionStatus.Completed;
                    EmitTransitionEvents(kvp.Value, prevStatus, SessionStatus.Completed);
                }
            }

            return _sessions.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>Returns all currently tracked sessions without rescanning.</summary>
    public IReadOnlyList<Session> GetSessions()
    {
        lock (_lock)
        {
            return _sessions.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>Returns a single session by ID, or <c>null</c>.</summary>
    public Session? GetSession(string id)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(id);
        }
    }

    /// <summary>Returns only sessions matching the given status.</summary>
    public IReadOnlyList<Session> GetSessionsByStatus(SessionStatus status)
    {
        lock (_lock)
        {
            return _sessions.Values.Where(s => s.Status == status).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Marks a session as <see cref="SessionStatus.Completed"/> and optionally
    /// removes it from the tracked set.
    /// </summary>
    public void CompleteSession(string id, bool remove = false)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var session))
            {
                session.Status = SessionStatus.Completed;
                if (remove)
                    _sessions.Remove(id);
            }
        }
    }

    /// <summary>Removes all completed sessions from the tracked set.</summary>
    public int CleanupCompletedSessions()
    {
        lock (_lock)
        {
            var completed = _sessions
                .Where(kvp => kvp.Value.Status == SessionStatus.Completed)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in completed)
                _sessions.Remove(id);

            return completed.Count;
        }
    }

    // ── Private scanning helpers ─────────────────────────────────────────

    private void EmitTransitionEvents(Session session, SessionStatus prev, SessionStatus next)
    {
        if (prev == next) return;

        SessionLifecycleEventType? eventType = (prev, next) switch
        {
            (SessionStatus.Active, SessionStatus.Stale) => SessionLifecycleEventType.BecameStale,
            (SessionStatus.Stale, SessionStatus.Active) => SessionLifecycleEventType.Resumed,
            (_, SessionStatus.Completed)                => SessionLifecycleEventType.Ended,
            _                                           => null,
        };

        if (eventType is null) return;

        var detail = next == SessionStatus.Completed
            ? $"Duration={FormatDuration(session.Duration)}"
            : null;

        EmitEvent(new SessionLifecycleEvent
        {
            EventType = eventType.Value,
            SessionId = session.Id,
            SessionName = session.Name,
            Detail = detail,
        });
    }

    private void EmitEvent(SessionLifecycleEvent evt)
    {
        _lifecycleEvents.Add(evt);
        _previousStatuses[evt.SessionId] = evt.EventType switch
        {
            SessionLifecycleEventType.Started  => SessionStatus.Active,
            SessionLifecycleEventType.Resumed  => SessionStatus.Active,
            SessionLifecycleEventType.BecameStale => SessionStatus.Stale,
            SessionLifecycleEventType.Ended    => SessionStatus.Completed,
            _                                  => SessionStatus.Active,
        };
        // Raise event outside try/catch intentionally — callers should not throw.
        LifecycleEventEmitted?.Invoke(evt);
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:D2}m" :
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m" : $"{(int)d.TotalSeconds}s";

    private SessionStatus ClassifyStatus(Session session, DateTime now)
    {
        if (session.Status == SessionStatus.Completed)
            return SessionStatus.Completed;

        var idle = now - session.LastActivity;
        return idle > _staleTimeout ? SessionStatus.Stale : SessionStatus.Active;
    }

    private List<Session> ScanAgencySessions(DateTime now)
    {
        var results = new List<Session>();
        var agencyLogDir = Path.Combine(_userProfile, ".agency", "logs");
        if (!Directory.Exists(agencyLogDir))
            return results;

        try
        {
            var dirs = new DirectoryInfo(agencyLogDir)
                .GetDirectories()
                .Where(d => (now - d.LastWriteTime) <= _staleTimeout * 3) // scan 3x window
                .ToList();

            foreach (var dir in dirs)
            {
                var logFiles = dir.GetFiles("*.log").Where(f => f.Length > 0).ToList();
                if (logFiles.Count == 0) continue;

                var eventsFile = Path.Combine(dir.FullName, "events.jsonl");
                var (cwd, _, startTime) = File.Exists(eventsFile)
                    ? ExtractSessionMetadata(eventsFile)
                    : ("", "", (DateTime?)null);

                var creationTime = startTime ?? dir.CreationTime;
                var lastWrite = logFiles.Max(f => f.LastWriteTime);

                results.Add(new Session
                {
                    Id = dir.Name,
                    Name = $"Agency-{SafeShortId(dir.Name)}",
                    StartTime = creationTime,
                    LastActivity = lastWrite,
                    MachineName = Environment.MachineName,
                    FullPath = dir.FullName,
                    SessionType = "Agency",
                    WorkingDirectory = cwd,
                    AgentCount = logFiles.Count(f => f.Name.StartsWith("process-")),
                    McpServerCount = logFiles.Count(f => f.Name.Contains("mcp")),
                });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    private List<Session> ScanCopilotSessionStateDirs(DateTime now)
    {
        var results = new List<Session>();
        var sessionStateDir = Path.Combine(_userProfile, ".copilot", "session-state");
        if (!Directory.Exists(sessionStateDir))
            return results;

        try
        {
            var dirs = new DirectoryInfo(sessionStateDir)
                .GetDirectories()
                .Where(d => (now - d.LastWriteTime) <= _staleTimeout * 3)
                .ToList();

            foreach (var dir in dirs)
            {
                var planFile = Path.Combine(dir.FullName, "plan.md");
                var lastActivity = File.Exists(planFile)
                    ? File.GetLastWriteTime(planFile)
                    : dir.LastWriteTime;

                results.Add(new Session
                {
                    Id = $"ss-{dir.Name}",
                    Name = $"Copilot-{SafeShortId(dir.Name)}",
                    StartTime = dir.CreationTime,
                    LastActivity = lastActivity,
                    MachineName = Environment.MachineName,
                    FullPath = dir.FullName,
                    SessionType = "Copilot",
                    WorkingDirectory = TryReadCwd(dir.FullName),
                    AgentCount = CountAgentFiles(dir),
                });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    private List<Session> ScanCopilotLogDirs(DateTime now)
    {
        var results = new List<Session>();
        var copilotLogDir = Path.Combine(_userProfile, ".copilot", "logs");
        if (!Directory.Exists(copilotLogDir))
            return results;

        try
        {
            var sessionDirs = new DirectoryInfo(copilotLogDir)
                .GetDirectories()
                .Where(d => (now - d.LastWriteTime) <= _staleTimeout * 3)
                .ToList();

            foreach (var dir in sessionDirs)
            {
                // Skip if already tracked via session-state scan (dedup by dir name)
                var logFiles = dir.GetFiles("*.log").Where(f => f.Length > 0).ToList();
                var eventsFile = Path.Combine(dir.FullName, "events.jsonl");
                if (logFiles.Count == 0 && !File.Exists(eventsFile)) continue;

                var (cwd, _, startTime) = File.Exists(eventsFile)
                    ? ExtractSessionMetadata(eventsFile)
                    : ("", "", (DateTime?)null);

                var creationTime = startTime ?? dir.CreationTime;
                var lastWrite = logFiles.Count > 0 ? logFiles.Max(f => f.LastWriteTime) : dir.LastWriteTime;

                results.Add(new Session
                {
                    Id = $"log-{dir.Name}",
                    Name = $"Copilot-{SafeShortId(dir.Name)}",
                    StartTime = creationTime,
                    LastActivity = lastWrite,
                    MachineName = Environment.MachineName,
                    FullPath = dir.FullName,
                    SessionType = "Copilot",
                    WorkingDirectory = cwd,
                    AgentCount = logFiles.Count(f => f.Name.StartsWith("process-")),
                    McpServerCount = logFiles.Count(f => f.Name.Contains("mcp")),
                });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    // ── Utility helpers ──────────────────────────────────────────────────

    private static string SafeShortId(string dirName)
    {
        if (dirName.Length > 8) return dirName[..8];
        return dirName;
    }

    private static int CountAgentFiles(DirectoryInfo dir)
    {
        try
        {
            return dir.GetFiles("*.md").Length + dir.GetFiles("*.jsonl").Length;
        }
        catch { return 0; }
    }

    private static string TryReadCwd(string sessionDir)
    {
        try
        {
            var contextFile = Path.Combine(sessionDir, "context.json");
            if (!File.Exists(contextFile)) return "";
            var json = File.ReadAllText(contextFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("cwd", out var cwd))
                return cwd.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static (string cwd, string resumeId, DateTime? startTime) ExtractSessionMetadata(string eventsFilePath)
    {
        var cwd = "";
        var resumeId = "";
        DateTime? startTime = null;

        try
        {
            // Read only the first few lines for metadata (avoid reading huge files)
            using var reader = new StreamReader(eventsFilePath);
            var linesRead = 0;
            while (!reader.EndOfStream && linesRead < 20)
            {
                var line = reader.ReadLine();
                linesRead++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("cwd", out var cwdProp) && string.IsNullOrEmpty(cwd))
                        cwd = cwdProp.GetString() ?? "";

                    if (root.TryGetProperty("resumeId", out var ridProp) && string.IsNullOrEmpty(resumeId))
                        resumeId = ridProp.GetString() ?? "";

                    if (root.TryGetProperty("timestamp", out var tsProp) && startTime == null)
                    {
                        var tsStr = tsProp.GetString();
                        if (tsStr != null && DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                            startTime = dt;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }

        return (cwd, resumeId, startTime);
    }
}
