using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquadMonitor;

/// <summary>
/// Generates daily and weekly cost/usage summary reports from tracked token data.
/// Reports can be rendered as plain text or Spectre.Console markup for dashboard display.
/// </summary>
public sealed class TokenReport
{
    private readonly TokenTracker _tracker;

    public TokenReport(TokenTracker tracker)
    {
        _tracker = tracker;
    }

    /// <summary>
    /// Generates a daily summary report for the specified date (defaults to today).
    /// </summary>
    public ReportSummary GenerateDailyReport(DateTime? date = null)
    {
        var targetDate = (date ?? DateTime.UtcNow).Date;
        var events = _tracker.Events
            .Where(e => e.Timestamp.Date == targetDate)
            .ToList();

        return BuildReport($"Daily Report — {targetDate:yyyy-MM-dd}", events, targetDate, targetDate.AddDays(1));
    }

    /// <summary>
    /// Generates a weekly summary report for the week containing the specified date.
    /// </summary>
    public ReportSummary GenerateWeeklyReport(DateTime? date = null)
    {
        var targetDate = (date ?? DateTime.UtcNow).Date;
        var weekStart = targetDate.AddDays(-(int)targetDate.DayOfWeek);
        var weekEnd = weekStart.AddDays(7);

        var events = _tracker.Events
            .Where(e => e.Timestamp.Date >= weekStart && e.Timestamp.Date < weekEnd)
            .ToList();

        return BuildReport($"Weekly Report — {weekStart:yyyy-MM-dd} to {weekEnd.AddDays(-1):yyyy-MM-dd}", events, weekStart, weekEnd);
    }

    private static ReportSummary BuildReport(string title, List<TokenUsageEvent> events, DateTime periodStart, DateTime periodEnd)
    {
        var report = new ReportSummary
        {
            Title = title,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            TotalCalls = events.Count,
            TotalInputTokens = events.Sum(e => e.InputTokens),
            TotalOutputTokens = events.Sum(e => e.OutputTokens),
            TotalCachedTokens = events.Sum(e => e.CachedTokens),
            TotalEstimatedCost = events.Sum(e => e.EstimatedCost),
        };

        // Per-model breakdown
        report.ModelBreakdown = events
            .GroupBy(e => e.Model)
            .Select(g => new ModelBreakdown
            {
                Model = g.Key,
                Calls = g.Count(),
                InputTokens = g.Sum(e => e.InputTokens),
                OutputTokens = g.Sum(e => e.OutputTokens),
                EstimatedCost = g.Sum(e => e.EstimatedCost)
            })
            .OrderByDescending(m => m.EstimatedCost)
            .ToList();

        // Per-agent breakdown
        report.AgentBreakdown = events
            .GroupBy(e => e.AgentType)
            .Select(g => new AgentBreakdown
            {
                Agent = g.Key,
                Calls = g.Count(),
                InputTokens = g.Sum(e => e.InputTokens),
                OutputTokens = g.Sum(e => e.OutputTokens),
                EstimatedCost = g.Sum(e => e.EstimatedCost)
            })
            .OrderByDescending(a => a.EstimatedCost)
            .ToList();

        // Per-session breakdown
        report.SessionBreakdown = events
            .GroupBy(e => e.SessionId)
            .Select(g => new SessionBreakdown
            {
                SessionId = g.Key,
                Calls = g.Count(),
                TotalTokens = g.Sum(e => e.InputTokens + e.OutputTokens),
                EstimatedCost = g.Sum(e => e.EstimatedCost),
                FirstActivity = g.Min(e => e.Timestamp),
                LastActivity = g.Max(e => e.Timestamp)
            })
            .OrderByDescending(s => s.EstimatedCost)
            .ToList();

        // Daily cost breakdown (for weekly reports)
        report.DailyCosts = events
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new DailyCost
            {
                Date = g.Key,
                Calls = g.Count(),
                TotalTokens = g.Sum(e => e.InputTokens + e.OutputTokens),
                EstimatedCost = g.Sum(e => e.EstimatedCost)
            })
            .OrderBy(d => d.Date)
            .ToList();

        return report;
    }

    /// <summary>
    /// Renders a report as plain text suitable for console output or file export.
    /// </summary>
    public static string RenderAsText(ReportSummary report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('═', 60));
        sb.AppendLine($"  {report.Title}");
        sb.AppendLine(new string('═', 60));
        sb.AppendLine();

        // Overall summary
        sb.AppendLine("  SUMMARY");
        sb.AppendLine($"  Total Calls:      {report.TotalCalls:N0}");
        sb.AppendLine($"  Input Tokens:     {TokenTracker.FormatTokenCount(report.TotalInputTokens)}");
        sb.AppendLine($"  Output Tokens:    {TokenTracker.FormatTokenCount(report.TotalOutputTokens)}");
        sb.AppendLine($"  Cached Tokens:    {TokenTracker.FormatTokenCount(report.TotalCachedTokens)}");
        sb.AppendLine($"  Estimated Cost:   ${report.TotalEstimatedCost:F2}");
        sb.AppendLine();

        // Model breakdown
        if (report.ModelBreakdown.Count > 0)
        {
            sb.AppendLine("  MODEL BREAKDOWN");
            sb.AppendLine($"  {"Model",-28} {"Calls",6} {"Input",10} {"Output",10} {"Cost",10}");
            sb.AppendLine("  " + new string('─', 66));
            foreach (var m in report.ModelBreakdown)
            {
                var model = m.Model.Length > 26 ? m.Model[..23] + "..." : m.Model;
                sb.AppendLine($"  {model,-28} {m.Calls,6} {TokenTracker.FormatTokenCount(m.InputTokens),10} {TokenTracker.FormatTokenCount(m.OutputTokens),10} {"$" + m.EstimatedCost.ToString("F2"),10}");
            }
            sb.AppendLine();
        }

        // Agent breakdown
        if (report.AgentBreakdown.Count > 0)
        {
            sb.AppendLine("  AGENT BREAKDOWN");
            sb.AppendLine($"  {"Agent",-20} {"Calls",6} {"Input",10} {"Output",10} {"Cost",10}");
            sb.AppendLine("  " + new string('─', 58));
            foreach (var a in report.AgentBreakdown)
            {
                sb.AppendLine($"  {a.Agent,-20} {a.Calls,6} {TokenTracker.FormatTokenCount(a.InputTokens),10} {TokenTracker.FormatTokenCount(a.OutputTokens),10} {"$" + a.EstimatedCost.ToString("F2"),10}");
            }
            sb.AppendLine();
        }

        // Session breakdown (top 10)
        if (report.SessionBreakdown.Count > 0)
        {
            sb.AppendLine($"  TOP SESSIONS (by cost, {Math.Min(report.SessionBreakdown.Count, 10)} of {report.SessionBreakdown.Count})");
            sb.AppendLine($"  {"Session",-20} {"Calls",6} {"Tokens",10} {"Cost",10} {"Duration",10}");
            sb.AppendLine("  " + new string('─', 58));
            foreach (var s in report.SessionBreakdown.Take(10))
            {
                var shortId = s.SessionId.Length > 18 ? s.SessionId[..8] + "..." : s.SessionId;
                var duration = s.LastActivity - s.FirstActivity;
                var durStr = duration.TotalHours >= 1 ? $"{duration.TotalHours:F1}h" : $"{duration.TotalMinutes:F0}m";
                sb.AppendLine($"  {shortId,-20} {s.Calls,6} {TokenTracker.FormatTokenCount(s.TotalTokens),10} {"$" + s.EstimatedCost.ToString("F2"),10} {durStr,10}");
            }
            sb.AppendLine();
        }

        // Daily costs (for weekly reports)
        if (report.DailyCosts.Count > 1)
        {
            sb.AppendLine("  DAILY BREAKDOWN");
            sb.AppendLine($"  {"Date",-12} {"Calls",6} {"Tokens",10} {"Cost",10}");
            sb.AppendLine("  " + new string('─', 40));
            foreach (var d in report.DailyCosts)
            {
                sb.AppendLine($"  {d.Date:yyyy-MM-dd}   {d.Calls,6} {TokenTracker.FormatTokenCount(d.TotalTokens),10} {"$" + d.EstimatedCost.ToString("F2"),10}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a report as Spectre.Console markup lines for display in the TUI dashboard.
    /// </summary>
    public static List<string> RenderAsMarkupLines(ReportSummary report)
    {
        var lines = new List<string>();
        lines.Add($"[magenta bold] ── {Esc(report.Title)} ──[/]");
        lines.Add("");

        lines.Add($" [dim]Calls:[/] [white]{report.TotalCalls:N0}[/]  |  " +
                  $"[dim]Input:[/] [blue]{TokenTracker.FormatTokenCount(report.TotalInputTokens)}[/]  |  " +
                  $"[dim]Output:[/] [green]{TokenTracker.FormatTokenCount(report.TotalOutputTokens)}[/]  |  " +
                  $"[dim]Cost:[/] [white]${report.TotalEstimatedCost:F2}[/]");
        lines.Add("");

        // Top models
        if (report.ModelBreakdown.Count > 0)
        {
            lines.Add(" [yellow bold]Models:[/]");
            foreach (var m in report.ModelBreakdown.Take(5))
            {
                var model = m.Model.Replace("claude-", "").Replace("gpt-", "");
                if (model.Length > 20) model = model[..17] + "...";
                var costClr = m.EstimatedCost < 5 ? "green" : m.EstimatedCost < 20 ? "yellow" : "red";
                lines.Add($"   [cyan]{Esc(model),-20}[/] {m.Calls,5} calls  [{costClr}]${m.EstimatedCost:F2}[/]");
            }
            lines.Add("");
        }

        // Top agents
        if (report.AgentBreakdown.Count > 0)
        {
            lines.Add(" [yellow bold]Agents:[/]");
            foreach (var a in report.AgentBreakdown.Take(5))
            {
                var costClr = a.EstimatedCost < 5 ? "green" : a.EstimatedCost < 20 ? "yellow" : "red";
                lines.Add($"   [cyan]{Esc(a.Agent),-20}[/] {a.Calls,5} calls  [{costClr}]${a.EstimatedCost:F2}[/]");
            }
        }

        return lines;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s);

    /// <summary>
    /// Exports the daily report for today as JSON to <c>~/.squad/token-reports/{date}.json</c>.
    /// Creates the directory if it does not exist.
    /// Returns the path the report was written to, or <c>null</c> on failure.
    /// </summary>
    public string? ExportDailyReport(string? userProfile = null, DateTime? date = null)
    {
        var report = GenerateDailyReport(date);
        return TokenReportExporter.ExportToJson(report, userProfile);
    }
}

/// <summary>
/// Handles serialization and export of <see cref="ReportSummary"/> objects
/// to <c>~/.squad/token-reports/</c>.
/// </summary>
public static class TokenReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Writes a report as indented JSON to <c>~/.squad/token-reports/{date}.json</c>.
    /// </summary>
    /// <param name="report">The report to export.</param>
    /// <param name="userProfile">
    /// User home directory. Defaults to <see cref="Environment.SpecialFolder.UserProfile"/>.
    /// </param>
    /// <returns>The full path written, or <c>null</c> if the export failed.</returns>
    public static string? ExportToJson(ReportSummary report, string? userProfile = null)
    {
        try
        {
            var home = userProfile
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".squad", "token-reports");
            Directory.CreateDirectory(dir);

            var filename = $"{report.PeriodStart:yyyy-MM-dd}.json";
            var path = Path.Combine(dir, filename);

            // Merge with existing file if present (accumulate calls during the day)
            ReportSummary merged = report;
            if (File.Exists(path))
            {
                try
                {
                    var existingEnvelope = JsonSerializer.Deserialize<TokenReportExportEnvelope>(
                        File.ReadAllText(path), JsonOptions);
                    if (existingEnvelope?.Report is not null)
                        merged = MergeReports(existingEnvelope.Report, report);
                }
                catch (JsonException) { /* corrupt file — overwrite */ }
            }

            var json = JsonSerializer.Serialize(new TokenReportExportEnvelope
            {
                SchemaVersion = "1.0",
                ExportedAt = DateTime.UtcNow,
                Report = merged,
            }, JsonOptions);

            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all exported report files sorted by date descending.
    /// </summary>
    public static IReadOnlyList<string> ListExports(string? userProfile = null)
    {
        var home = userProfile
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".squad", "token-reports");
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        return new DirectoryInfo(dir)
            .GetFiles("*.json")
            .OrderByDescending(f => f.Name)
            .Select(f => f.FullName)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Loads and deserializes a previously exported report from disk.
    /// Returns <c>null</c> if the file doesn't exist or can't be parsed.
    /// </summary>
    public static ReportSummary? LoadExport(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize<TokenReportExportEnvelope>(json, JsonOptions);
            return envelope?.Report;
        }
        catch
        {
            return null;
        }
    }

    // Merges two reports covering the same period (summing counters, de-duplicating breakdowns)
    private static ReportSummary MergeReports(ReportSummary a, ReportSummary b)
    {
        return new ReportSummary
        {
            Title = b.Title,
            PeriodStart = a.PeriodStart < b.PeriodStart ? a.PeriodStart : b.PeriodStart,
            PeriodEnd = a.PeriodEnd > b.PeriodEnd ? a.PeriodEnd : b.PeriodEnd,
            TotalCalls = a.TotalCalls + b.TotalCalls,
            TotalInputTokens = a.TotalInputTokens + b.TotalInputTokens,
            TotalOutputTokens = a.TotalOutputTokens + b.TotalOutputTokens,
            TotalCachedTokens = a.TotalCachedTokens + b.TotalCachedTokens,
            TotalEstimatedCost = a.TotalEstimatedCost + b.TotalEstimatedCost,
            ModelBreakdown = MergeModelBreakdowns(a.ModelBreakdown, b.ModelBreakdown),
            AgentBreakdown = MergeAgentBreakdowns(a.AgentBreakdown, b.AgentBreakdown),
            SessionBreakdown = MergeSessionBreakdowns(a.SessionBreakdown, b.SessionBreakdown),
            DailyCosts = b.DailyCosts, // latest wins for daily costs
        };
    }

    private static List<ModelBreakdown> MergeModelBreakdowns(
        List<ModelBreakdown> a, List<ModelBreakdown> b)
    {
        return a.Concat(b)
            .GroupBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelBreakdown
            {
                Model = g.Key,
                Calls = g.Sum(m => m.Calls),
                InputTokens = g.Sum(m => m.InputTokens),
                OutputTokens = g.Sum(m => m.OutputTokens),
                EstimatedCost = g.Sum(m => m.EstimatedCost),
            })
            .OrderByDescending(m => m.EstimatedCost)
            .ToList();
    }

    private static List<AgentBreakdown> MergeAgentBreakdowns(
        List<AgentBreakdown> a, List<AgentBreakdown> b)
    {
        return a.Concat(b)
            .GroupBy(ag => ag.Agent, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AgentBreakdown
            {
                Agent = g.Key,
                Calls = g.Sum(ag => ag.Calls),
                InputTokens = g.Sum(ag => ag.InputTokens),
                OutputTokens = g.Sum(ag => ag.OutputTokens),
                EstimatedCost = g.Sum(ag => ag.EstimatedCost),
            })
            .OrderByDescending(ag => ag.EstimatedCost)
            .ToList();
    }

    private static List<SessionBreakdown> MergeSessionBreakdowns(
        List<SessionBreakdown> a, List<SessionBreakdown> b)
    {
        return a.Concat(b)
            .GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SessionBreakdown
            {
                SessionId = g.Key,
                Calls = g.Sum(s => s.Calls),
                TotalTokens = g.Sum(s => s.TotalTokens),
                EstimatedCost = g.Sum(s => s.EstimatedCost),
                FirstActivity = g.Min(s => s.FirstActivity),
                LastActivity = g.Max(s => s.LastActivity),
            })
            .OrderByDescending(s => s.EstimatedCost)
            .ToList();
    }
}

// ── Report data models ──

public sealed class ReportSummary
{
    public required string Title { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TotalCalls { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCachedTokens { get; set; }
    public double TotalEstimatedCost { get; set; }
    public List<ModelBreakdown> ModelBreakdown { get; set; } = new();
    public List<AgentBreakdown> AgentBreakdown { get; set; } = new();
    public List<SessionBreakdown> SessionBreakdown { get; set; } = new();
    public List<DailyCost> DailyCosts { get; set; } = new();
}

public sealed class ModelBreakdown
{
    public required string Model { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedCost { get; set; }
}

public sealed class AgentBreakdown
{
    public required string Agent { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EstimatedCost { get; set; }
}

public sealed class SessionBreakdown
{
    public required string SessionId { get; set; }
    public int Calls { get; set; }
    public long TotalTokens { get; set; }
    public double EstimatedCost { get; set; }
    public DateTime FirstActivity { get; set; }
    public DateTime LastActivity { get; set; }
}

public sealed class DailyCost
{
    public DateTime Date { get; set; }
    public int Calls { get; set; }
    public long TotalTokens { get; set; }
    public double EstimatedCost { get; set; }
}

/// <summary>
/// Wraps a <see cref="ReportSummary"/> with versioning metadata for file exports.
/// </summary>
public sealed class TokenReportExportEnvelope
{
    /// <summary>Schema version for forward-compatibility. Currently "1.0".</summary>
    public required string SchemaVersion { get; set; }

    /// <summary>UTC timestamp when this file was last written.</summary>
    public DateTime ExportedAt { get; set; }

    /// <summary>The actual report data.</summary>
    public required ReportSummary Report { get; set; }
}
