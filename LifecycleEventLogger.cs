namespace SquadMonitor;

/// <summary>
/// Kinds of lifecycle events that can be raised for a monitored session.
/// </summary>
public enum LifecycleEventKind
{
    /// <summary>A previously unknown session was first discovered.</summary>
    SessionStarted,

    /// <summary>A session transitioned to Completed or disappeared from the scan.</summary>
    SessionEnded,

    /// <summary>A session's token count surpassed its previous recorded peak.</summary>
    PeakUsageDetected,

    /// <summary>A session's burn rate reached ≥ 3× the average across all sessions.</summary>
    RunawayDetected,
}

/// <summary>
/// An immutable record of a single session lifecycle event.
/// </summary>
public sealed record LifecycleEvent
{
    public LifecycleEventKind Kind { get; init; }
    public required string SessionId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Message { get; init; }
    public long? TokenCount { get; init; }
    public double? BurnRate { get; init; }
}

/// <summary>
/// Tracks session state transitions and fires lifecycle events to an in-memory log
/// and an append-only text file under <c>~/.squad/token-reports/lifecycle-events.log</c>.
/// </summary>
public sealed class LifecycleEventLogger
{
    private readonly string _logFilePath;
    private readonly List<LifecycleEvent> _events = new();

    // ── State maps keyed by session ID ──
    private readonly HashSet<string> _knownSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionStatus> _lastStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _peakTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runawayActive = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LifecycleEvent> Events => _events;

    public LifecycleEventLogger(string userProfile)
    {
        var dir = Path.Combine(userProfile, ".squad", "token-reports");
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, "lifecycle-events.log");
    }

    /// <summary>
    /// Processes a fresh session snapshot, writing lifecycle events for any state changes.
    /// Call this on each dashboard refresh cycle.
    /// </summary>
    /// <param name="summaries">Latest plain session summaries from <see cref="SessionAggregator"/>.</param>
    /// <param name="tokenSummaries">Latest token-enriched summaries from <see cref="SessionAggregator"/>.</param>
    public void ProcessSnapshot(
        IReadOnlyList<SessionSummary> summaries,
        IReadOnlyList<TokenEnrichedSummary> tokenSummaries)
    {
        var now = DateTime.UtcNow;
        var currentIds = new HashSet<string>(summaries.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        // ── Detect new sessions ──
        foreach (var s in summaries)
        {
            if (_knownSessions.Add(s.Id))
            {
                Log(new LifecycleEvent
                {
                    Kind = LifecycleEventKind.SessionStarted,
                    SessionId = s.ShortId,
                    Timestamp = now,
                    Message = $"Session started ({s.SessionType} on {s.MachineName})",
                });
            }
        }

        // ── Detect sessions that vanished from the scan (implicit end) ──
        foreach (var id in _lastStatus.Keys.Where(id => !currentIds.Contains(id)).ToList())
        {
            var shortId = id.Length > 8 ? id[..8] : id;
            Log(new LifecycleEvent
            {
                Kind = LifecycleEventKind.SessionEnded,
                SessionId = shortId,
                Timestamp = now,
                Message = "Session no longer detected",
            });
            _lastStatus.Remove(id);
            _runawayActive.Remove(id);
        }

        // ── Detect explicit Completed transitions ──
        foreach (var s in summaries.Where(s => s.Status == SessionStatus.Completed))
        {
            if (_lastStatus.TryGetValue(s.Id, out var prev) && prev != SessionStatus.Completed)
            {
                Log(new LifecycleEvent
                {
                    Kind = LifecycleEventKind.SessionEnded,
                    SessionId = s.ShortId,
                    Timestamp = now,
                    Message = "Session completed",
                });
                _runawayActive.Remove(s.Id);
            }
        }

        // ── Update last-known status ──
        foreach (var s in summaries)
            _lastStatus[s.Id] = s.Status;

        // ── Process token-enriched data ──
        foreach (var ts in tokenSummaries)
        {
            // Peak usage detection: token count grew >10% above the recorded peak
            _peakTokens.TryGetValue(ts.SessionId, out var prevPeak);
            if (ts.TotalTokens > 0 && ts.TotalTokens > prevPeak)
            {
                var growth = prevPeak > 0 ? (double)(ts.TotalTokens - prevPeak) / prevPeak : 1.0;
                if (growth >= 0.10 && prevPeak > 0)
                {
                    Log(new LifecycleEvent
                    {
                        Kind = LifecycleEventKind.PeakUsageDetected,
                        SessionId = ts.ShortId,
                        Timestamp = now,
                        Message = $"New peak: {TokenTracker.FormatTokenCount(ts.TotalTokens)} tokens " +
                                  $"(+{growth:P0} vs previous peak of {TokenTracker.FormatTokenCount(prevPeak)})",
                        TokenCount = ts.TotalTokens,
                        BurnRate = ts.BurnRateTokensPerHour,
                    });
                }
                _peakTokens[ts.SessionId] = ts.TotalTokens;
            }

            // Runaway detection: flag only once until it recovers
            if (ts.IsRunaway && _runawayActive.Add(ts.SessionId))
            {
                Log(new LifecycleEvent
                {
                    Kind = LifecycleEventKind.RunawayDetected,
                    SessionId = ts.ShortId,
                    Timestamp = now,
                    Message = $"Runaway burn rate: {ts.BurnRateTokensPerHour:F0} tok/h " +
                              $"(≥3× average) — {TokenTracker.FormatTokenCount(ts.TotalTokens)} total",
                    TokenCount = ts.TotalTokens,
                    BurnRate = ts.BurnRateTokensPerHour,
                });
            }
            else if (!ts.IsRunaway)
            {
                // Clear runaway flag so it can re-trigger if the rate spikes again
                _runawayActive.Remove(ts.SessionId);
            }
        }
    }

    /// <summary>
    /// Returns the most recent lifecycle events in reverse-chronological order.
    /// </summary>
    public IEnumerable<LifecycleEvent> GetRecent(int count = 20) =>
        _events.AsEnumerable().Reverse().Take(count);

    // ── Private helpers ──────────────────────────────────────────────────

    private void Log(LifecycleEvent evt)
    {
        _events.Add(evt);
        try
        {
            var icon = evt.Kind switch
            {
                LifecycleEventKind.SessionStarted => "▶",
                LifecycleEventKind.SessionEnded   => "■",
                LifecycleEventKind.PeakUsageDetected => "↑",
                LifecycleEventKind.RunawayDetected   => "⚠",
                _ => "•",
            };
            var line = $"[{evt.Timestamp:yyyy-MM-dd HH:mm:ssZ}] {icon} [{evt.Kind}] {evt.SessionId} — {evt.Message}";
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
        catch { /* best-effort — never crash the dashboard */ }
    }
}
