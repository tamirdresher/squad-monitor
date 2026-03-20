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
