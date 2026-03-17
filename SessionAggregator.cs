namespace SquadMonitor;

/// <summary>
/// Immutable snapshot of aggregate metrics across all monitored sessions.
/// </summary>
public sealed record SessionAggregateMetrics
{
    public int TotalSessions { get; init; }
    public int ActiveSessions { get; init; }
    public int StaleSessions { get; init; }
    public int CompletedSessions { get; init; }
    public int TotalAgentsSpawned { get; init; }
    public int TotalMcpServers { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public TimeSpan AverageSessionDuration { get; init; }
    public DateTime? MostRecentActivity { get; init; }
}

/// <summary>
/// Per-session summary used in the aggregate view.
/// </summary>
public sealed record SessionSummary
{
    public required string Id { get; init; }
    public required string ShortId { get; init; }
    public required string Name { get; init; }
    public required string MachineName { get; init; }
    public required SessionStatus Status { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int AgentCount { get; init; }
    public required DateTime LastActivity { get; init; }
    public required string SessionType { get; init; }
    public required string WorkingDirectory { get; init; }
}

/// <summary>
/// Aggregates data from a <see cref="SessionManager"/> to produce
/// cross-session summaries and metrics.
/// </summary>
public sealed class SessionAggregator
{
    private readonly SessionManager _sessionManager;

    public SessionAggregator(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Refreshes sessions and computes aggregate metrics.
    /// </summary>
    public SessionAggregateMetrics GetAggregateMetrics()
    {
        var sessions = _sessionManager.RefreshSessions();

        var active = sessions.Count(s => s.Status == SessionStatus.Active);
        var stale = sessions.Count(s => s.Status == SessionStatus.Stale);
        var completed = sessions.Count(s => s.Status == SessionStatus.Completed);
        var totalAgents = sessions.Sum(s => s.AgentCount);
        var totalMcp = sessions.Sum(s => s.McpServerCount);
        var totalDuration = TimeSpan.FromTicks(sessions.Sum(s => s.Duration.Ticks));
        var avgDuration = sessions.Count > 0
            ? TimeSpan.FromTicks(totalDuration.Ticks / sessions.Count)
            : TimeSpan.Zero;
        var mostRecent = sessions.Count > 0
            ? sessions.Max(s => s.LastActivity)
            : (DateTime?)null;

        return new SessionAggregateMetrics
        {
            TotalSessions = sessions.Count,
            ActiveSessions = active,
            StaleSessions = stale,
            CompletedSessions = completed,
            TotalAgentsSpawned = totalAgents,
            TotalMcpServers = totalMcp,
            TotalDuration = totalDuration,
            AverageSessionDuration = avgDuration,
            MostRecentActivity = mostRecent,
        };
    }

    /// <summary>
    /// Returns a per-session summary list, suitable for rendering in a table.
    /// </summary>
    public IReadOnlyList<SessionSummary> GetSessionSummaries()
    {
        var sessions = _sessionManager.GetSessions();

        return sessions
            .OrderByDescending(s => s.Status == SessionStatus.Active ? 2 :
                                    s.Status == SessionStatus.Stale ? 1 : 0)
            .ThenByDescending(s => s.LastActivity)
            .Select(s => new SessionSummary
            {
                Id = s.Id,
                ShortId = s.ShortId,
                Name = s.Name,
                MachineName = s.MachineName,
                Status = s.Status,
                Duration = s.Duration,
                AgentCount = s.AgentCount,
                LastActivity = s.LastActivity,
                SessionType = s.SessionType,
                WorkingDirectory = s.WorkingDirectory,
            })
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Returns summaries filtered to a specific status.
    /// </summary>
    public IReadOnlyList<SessionSummary> GetSessionSummaries(SessionStatus status)
    {
        return GetSessionSummaries()
            .Where(s => s.Status == status)
            .ToList()
            .AsReadOnly();
    }
}
