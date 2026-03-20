using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquadMonitor;

/// <summary>
/// Exports token usage reports to <c>~/.squad/token-reports/</c> as JSON files.
/// Generates timestamped snapshots for multi-session data and daily summaries.
/// </summary>
public sealed class TokenReportExporter
{
    private readonly string _reportDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string ReportDirectory => _reportDir;

    public TokenReportExporter(string userProfile)
    {
        _reportDir = Path.Combine(userProfile, ".squad", "token-reports");
        Directory.CreateDirectory(_reportDir);
    }

    /// <summary>
    /// Exports a multi-session snapshot to a timestamped JSON file.
    /// Returns the full path of the written file, or <c>null</c> on failure.
    /// </summary>
    public string? ExportSessionSnapshot(
        IReadOnlyList<TokenEnrichedSummary> sessions,
        SessionAggregateMetrics metrics,
        TokenTracker tracker)
    {
        try
        {
            var now = DateTime.UtcNow;
            var fileName = $"session-snapshot-{now:yyyy-MM-dd_HH-mm-ss}.json";
            var filePath = Path.Combine(_reportDir, fileName);

            var snapshot = new MultiSessionSnapshotReport
            {
                GeneratedAt = now,
                TotalSessions = metrics.TotalSessions,
                ActiveSessions = metrics.ActiveSessions,
                StaleSessions = metrics.StaleSessions,
                TotalTokensAllSessions = metrics.TotalTokensAllSessions,
                TotalCostAllSessions = metrics.TotalCostAllSessions,
                AverageBurnRateTokensPerHour = metrics.AverageBurnRateTokensPerHour,
                TotalBurnRateTokensPerHour = metrics.TotalBurnRateTokensPerHour,
                RunawaySessions = metrics.RunawaySessions,
                Sessions = sessions.Select(s => new SessionReportEntry
                {
                    SessionId = s.SessionId,
                    ShortId = s.ShortId,
                    Status = s.Status.ToString(),
                    SessionType = s.SessionType,
                    DurationHours = s.Duration.TotalHours,
                    TotalTokens = s.TotalTokens,
                    InputTokens = s.InputTokens,
                    OutputTokens = s.OutputTokens,
                    EstimatedCostUsd = s.EstimatedCost,
                    BurnRateTokensPerHour = s.BurnRateTokensPerHour,
                    IsRunaway = s.IsRunaway,
                }).ToList(),
                TopAgents = tracker.AgentStats.Values
                    .OrderByDescending(a => a.EstimatedCost)
                    .Take(10)
                    .Select(a => new AgentReportEntry
                    {
                        AgentName = a.AgentName,
                        Calls = a.Calls,
                        TotalTokens = a.TotalTokens,
                        InputTokens = a.InputTokens,
                        OutputTokens = a.OutputTokens,
                        EstimatedCostUsd = a.EstimatedCost,
                    })
                    .ToList(),
                TopModels = tracker.ModelStats.Values
                    .OrderByDescending(m => m.EstimatedCost)
                    .Take(10)
                    .Select(m => new ModelReportEntry
                    {
                        ModelName = m.ModelName,
                        Calls = m.Calls,
                        TotalTokens = m.TotalTokens,
                        InputTokens = m.InputTokens,
                        OutputTokens = m.OutputTokens,
                        EstimatedCostUsd = m.EstimatedCost,
                    })
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(filePath, json);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Exports a daily summary report to a stable, date-named JSON file
    /// (overwrites if the file for that date already exists).
    /// Returns the full path of the written file, or <c>null</c> on failure.
    /// </summary>
    public string? ExportDailyReport(ReportSummary report)
    {
        try
        {
            var fileName = $"daily-{report.PeriodStart:yyyy-MM-dd}.json";
            var filePath = Path.Combine(_reportDir, fileName);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(filePath, json);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists recent report files in the export directory, newest first.
    /// </summary>
    public IReadOnlyList<string> GetRecentReports(int count = 10)
    {
        try
        {
            return new DirectoryInfo(_reportDir)
                .GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(count)
                .Select(f => f.FullName)
                .ToList()
                .AsReadOnly();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Purges snapshot files older than <paramref name="maxAgeDays"/> days,
    /// preserving all daily report files (prefix "daily-").
    /// </summary>
    public int PurgeOldSnapshots(int maxAgeDays = 7)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        int removed = 0;
        try
        {
            foreach (var file in new DirectoryInfo(_reportDir)
                .GetFiles("session-snapshot-*.json")
                .Where(f => f.LastWriteTime < cutoff))
            {
                file.Delete();
                removed++;
            }
        }
        catch { /* best-effort */ }
        return removed;
    }
}

// ── JSON-serializable report models ────────────────────────────────────

/// <summary>
/// Root object for a multi-session snapshot exported to JSON.
/// </summary>
public sealed class MultiSessionSnapshotReport
{
    public DateTime GeneratedAt { get; set; }
    public int TotalSessions { get; set; }
    public int ActiveSessions { get; set; }
    public int StaleSessions { get; set; }
    public long TotalTokensAllSessions { get; set; }
    public double TotalCostAllSessions { get; set; }
    public double AverageBurnRateTokensPerHour { get; set; }
    public double TotalBurnRateTokensPerHour { get; set; }
    public int RunawaySessions { get; set; }
    public List<SessionReportEntry> Sessions { get; set; } = [];
    public List<AgentReportEntry> TopAgents { get; set; } = [];
    public List<ModelReportEntry> TopModels { get; set; } = [];
}

public sealed class SessionReportEntry
{
    public required string SessionId { get; set; }
    public required string ShortId { get; set; }
    public required string Status { get; set; }
    public required string SessionType { get; set; }
    public double DurationHours { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedCostUsd { get; set; }
    public double BurnRateTokensPerHour { get; set; }
    public bool IsRunaway { get; set; }
}

public sealed class AgentReportEntry
{
    public required string AgentName { get; set; }
    public int Calls { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedCostUsd { get; set; }
}

public sealed class ModelReportEntry
{
    public required string ModelName { get; set; }
    public int Calls { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedCostUsd { get; set; }
}
