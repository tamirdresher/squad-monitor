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

    // ── Cross-session token aggregation ──────────────────────────────────
    /// <summary>Total tokens consumed across all tracked sessions.</summary>
    public long TotalTokensAcrossSessions { get; init; }
    /// <summary>Total estimated cost across all tracked sessions (USD).</summary>
    public double TotalCostAcrossSessions { get; init; }
    /// <summary>Mean tokens-per-minute burn rate across active sessions.</summary>
    public double AverageBurnRatePerMinute { get; init; }
    /// <summary>Number of sessions flagged as runaway (≥ 3× average burn rate).</summary>
    public int RunawaySessions { get; init; }
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

    // ── Per-session token metrics ─────────────────────────────────────────
    /// <summary>Total tokens (input + output) consumed in this session.</summary>
    public long TotalTokens { get; init; }
    /// <summary>Estimated cost for this session (USD).</summary>
    public double EstimatedCost { get; init; }
    /// <summary>Tokens consumed per minute over the session's active duration.</summary>
    public double BurnRatePerMinute { get; init; }
    /// <summary>True when this session's burn rate exceeds 3× the average of all active sessions.</summary>
    public bool IsRunaway { get; init; }
}

/// <summary>
/// Aggregates data from a <see cref="SessionManager"/> to produce
/// cross-session summaries and metrics.
/// Optionally accepts a <see cref="TokenTracker"/> for token-level aggregation,
/// burn-rate calculation, and runaway detection.
/// </summary>
public sealed class SessionAggregator
{
    private readonly SessionManager _sessionManager;

    /// <summary>
    /// Optional token tracker. When set, the aggregator includes per-session
    /// token counts, burn rates, and runaway detection in its output.
    /// </summary>
    public TokenTracker? TokenTracker { get; set; }

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

        // Token aggregation
        long totalTokens = 0;
        double totalCost = 0;
        double avgBurnRate = 0;
        int runaways = 0;

        if (TokenTracker is not null)
        {
            var summaries = ComputeSessionSummaries(sessions);
            totalTokens = summaries.Sum(s => s.TotalTokens);
            totalCost = summaries.Sum(s => s.EstimatedCost);

            var activeBurnRates = summaries
                .Where(s => s.Status == SessionStatus.Active && s.BurnRatePerMinute > 0)
                .Select(s => s.BurnRatePerMinute)
                .ToList();

            if (activeBurnRates.Count > 0)
            {
                avgBurnRate = activeBurnRates.Average();
                runaways = activeBurnRates.Count(r => r >= avgBurnRate * 3.0);
            }
        }

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
            TotalTokensAcrossSessions = totalTokens,
            TotalCostAcrossSessions = totalCost,
            AverageBurnRatePerMinute = avgBurnRate,
            RunawaySessions = runaways,
        };
    }

    /// <summary>
    /// Returns a per-session summary list, suitable for rendering in a table.
    /// </summary>
    public IReadOnlyList<SessionSummary> GetSessionSummaries()
    {
        var sessions = _sessionManager.GetSessions();
        return ComputeSessionSummaries(sessions);
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

    // ── Private helpers ──────────────────────────────────────────────────

    private IReadOnlyList<SessionSummary> ComputeSessionSummaries(IReadOnlyList<Session> sessions)
    {
        // Build per-session token stats lookup if tracker is available
        var tokenStatsBySession = TokenTracker?.SessionStats
            ?? new Dictionary<string, SessionTokenStats>();

        // Compute burn rates for each session
        var rawSummaries = sessions
            .OrderByDescending(s => s.Status == SessionStatus.Active ? 2 :
                                    s.Status == SessionStatus.Stale ? 1 : 0)
            .ThenByDescending(s => s.LastActivity)
            .Select(s =>
            {
                // Match token stats: try exact ID and prefix variants
                var tokenStats = FindTokenStats(tokenStatsBySession, s);
                var totalTokens = tokenStats?.TotalTokens ?? 0;
                var cost = tokenStats?.EstimatedCost ?? 0;

                // Burn rate: tokens / minutes of active duration (minimum 1 minute to avoid division noise)
                var durationMinutes = Math.Max(s.Duration.TotalMinutes, 1.0);
                var burnRate = totalTokens > 0 ? totalTokens / durationMinutes : 0;

                return (Summary: new SessionSummary
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
                    TotalTokens = totalTokens,
                    EstimatedCost = cost,
                    BurnRatePerMinute = burnRate,
                    IsRunaway = false, // filled in below
                }, BurnRate: burnRate, Status: s.Status);
            })
            .ToList();

        // Compute avg burn rate of active sessions for runaway detection
        var activeBurnRates = rawSummaries
            .Where(x => x.Status == SessionStatus.Active && x.BurnRate > 0)
            .Select(x => x.BurnRate)
            .ToList();
        var avgBurnRate = activeBurnRates.Count > 0 ? activeBurnRates.Average() : 0;

        // Re-project with IsRunaway flag
        return rawSummaries
            .Select(x =>
            {
                var isRunaway = avgBurnRate > 0
                    && x.Status == SessionStatus.Active
                    && x.BurnRate >= avgBurnRate * 3.0;
                return x.Summary with { IsRunaway = isRunaway };
            })
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Tries to find token stats for a session, matching on full ID, prefix, or partial overlap.
    /// </summary>
    private static SessionTokenStats? FindTokenStats(
        IReadOnlyDictionary<string, SessionTokenStats> lookup, Session session)
    {
        // Exact match
        if (lookup.TryGetValue(session.Id, out var exact)) return exact;

        // Strip well-known prefixes used in SessionManager (ss-, log-)
        var stripped = session.Id.StartsWith("ss-") ? session.Id[3..] :
                       session.Id.StartsWith("log-") ? session.Id[4..] : session.Id;
        if (lookup.TryGetValue(stripped, out var stripped1)) return stripped1;

        // Partial: token session ID starts with session short ID (first 8 chars)
        var shortId = session.ShortId;
        return lookup.Values.FirstOrDefault(ts =>
            ts.SessionId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase) ||
            shortId.StartsWith(ts.SessionId[..Math.Min(8, ts.SessionId.Length)], StringComparison.OrdinalIgnoreCase));
    }
}
