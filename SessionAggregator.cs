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

    // ── Token aggregation (populated when a TokenTracker is provided) ──

    /// <summary>Sum of all tokens consumed across every active session.</summary>
    public long TotalTokensAllSessions { get; init; }

    /// <summary>Sum of estimated costs across every active session (USD).</summary>
    public double TotalCostAllSessions { get; init; }

    /// <summary>Average token burn rate across all sessions that have token data (tokens/hour).</summary>
    public double AverageBurnRateTokensPerHour { get; init; }

    /// <summary>Combined burn rate across all active sessions (tokens/hour).</summary>
    public double TotalBurnRateTokensPerHour { get; init; }

    /// <summary>Number of sessions flagged as runaway (burn rate ≥ 3× average).</summary>
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
}

/// <summary>
/// Per-session summary enriched with token-tracking data (burn rate, cost, runaway flag).
/// </summary>
public sealed record TokenEnrichedSummary
{
    public required string SessionId { get; init; }
    public required string ShortId { get; init; }
    public required string SessionType { get; init; }
    public required SessionStatus Status { get; init; }
    public required TimeSpan Duration { get; init; }

    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public double EstimatedCost { get; init; }

    /// <summary>Tokens consumed per hour, derived from log-event timestamps.</summary>
    public double BurnRateTokensPerHour { get; init; }

    /// <summary>True when this session's burn rate is ≥ 3× the average across all sessions.</summary>
    public bool IsRunaway { get; init; }
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
    /// Refreshes sessions and computes aggregate metrics including cross-session token data.
    /// </summary>
    public SessionAggregateMetrics GetAggregateMetrics(TokenTracker tracker)
    {
        var base_ = GetAggregateMetrics();
        var enriched = GetTokenEnrichedSummaries(tracker);

        var totalTokens = enriched.Sum(s => s.TotalTokens);
        var totalCost = enriched.Sum(s => s.EstimatedCost);

        // Burn rates only for sessions with meaningful data (>0 tokens and >1 min duration)
        var withBurnRate = enriched
            .Where(s => s.BurnRateTokensPerHour > 0)
            .ToList();

        var avgBurnRate = withBurnRate.Count > 0
            ? withBurnRate.Average(s => s.BurnRateTokensPerHour)
            : 0;

        var totalBurnRate = withBurnRate.Sum(s => s.BurnRateTokensPerHour);
        var runaway = enriched.Count(s => s.IsRunaway);

        return base_ with
        {
            TotalTokensAllSessions = totalTokens,
            TotalCostAllSessions = totalCost,
            AverageBurnRateTokensPerHour = avgBurnRate,
            TotalBurnRateTokensPerHour = totalBurnRate,
            RunawaySessions = runaway,
        };
    }

    /// <summary>
    /// Returns token-enriched per-session summaries with burn rates and runaway flags.
    /// Sessions with no token log data are still included with zero token fields.
    /// </summary>
    public IReadOnlyList<TokenEnrichedSummary> GetTokenEnrichedSummaries(TokenTracker tracker)
    {
        var sessions = _sessionManager.GetSessions();

        // Build per-session burn rates from token events
        var perSession = tracker.SessionStats;

        // Compute burn rates per session: tokens / hours of activity
        static double ComputeBurnRate(SessionTokenStats stats)
        {
            var durationHours = stats.Duration.TotalHours;
            if (durationHours < 1.0 / 60) // less than 1 minute — skip (insufficient data)
                return 0;
            return stats.TotalTokens / durationHours;
        }

        var burnRates = perSession.Values
            .Where(s => s.TotalTokens > 0)
            .Select(s => (s.SessionId, BurnRate: ComputeBurnRate(s)))
            .Where(x => x.BurnRate > 0)
            .ToDictionary(x => x.SessionId, x => x.BurnRate, StringComparer.OrdinalIgnoreCase);

        double avgBurnRate = burnRates.Count > 0 ? burnRates.Values.Average() : 0;
        double runawaythreshold = avgBurnRate * 3.0;

        return sessions
            .OrderByDescending(s => s.Status == SessionStatus.Active ? 2 :
                                    s.Status == SessionStatus.Stale ? 1 : 0)
            .ThenByDescending(s => s.LastActivity)
            .Select(s =>
            {
                var tokenKey = s.Id;
                // Try matching by session ID variants (full id, or prefixed)
                if (!perSession.TryGetValue(tokenKey, out var stats))
                    perSession.TryGetValue(s.ShortId, out stats);

                var burnRate = burnRates.TryGetValue(tokenKey, out var br) ? br : 0;
                var isRunaway = avgBurnRate > 0 && burnRate >= runawaythreshold;

                return new TokenEnrichedSummary
                {
                    SessionId = s.Id,
                    ShortId = s.ShortId,
                    SessionType = s.SessionType,
                    Status = s.Status,
                    Duration = s.Duration,
                    TotalTokens = stats?.TotalTokens ?? 0,
                    InputTokens = stats?.InputTokens ?? 0,
                    OutputTokens = stats?.OutputTokens ?? 0,
                    EstimatedCost = stats?.EstimatedCost ?? 0,
                    BurnRateTokensPerHour = burnRate,
                    IsRunaway = isRunaway,
                };
            })
            .ToList()
            .AsReadOnly();
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
