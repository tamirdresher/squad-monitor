using Spectre.Console;
using Spectre.Console.Rendering;

namespace SquadMonitor;

/// <summary>
/// Builds Spectre.Console renderables for the multi-session dashboard panel.
/// Color coding: Green = Active, Yellow = Stale, Gray = Completed.
/// </summary>
public static class MultiSessionPanel
{
    /// <summary>
    /// Builds the complete multi-session panel including the session table
    /// and an aggregate summary row.
    /// </summary>
    public static IRenderable Build(SessionAggregator aggregator)
    {
        var sections = new List<IRenderable>();

        // Header
        var header = new Rule("[green bold]Multi-Session Monitor[/]") { Justification = Justify.Left };
        sections.Add(header);

        // Aggregate metrics bar
        var metrics = aggregator.GetAggregateMetrics();
        sections.Add(BuildMetricsSummary(metrics));
        sections.Add(Text.Empty);

        // Session table
        var summaries = aggregator.GetSessionSummaries();
        if (summaries.Count == 0)
        {
            sections.Add(new Markup("[dim]  No sessions detected. Ensure Copilot CLI or Agency sessions are running.[/]"));
        }
        else
        {
            sections.Add(BuildSessionTable(summaries));
        }

        sections.Add(Text.Empty);
        return new Rows(sections);
    }

    /// <summary>
    /// Builds a single-line aggregate metrics bar.
    /// </summary>
    private static IRenderable BuildMetricsSummary(SessionAggregateMetrics metrics)
    {
        var parts = new List<string>
        {
            $"[bold]Sessions:[/] [cyan]{metrics.TotalSessions}[/]",
            $"[green]Active: {metrics.ActiveSessions}[/]",
            $"[yellow]Stale: {metrics.StaleSessions}[/]",
            $"[grey]Done: {metrics.CompletedSessions}[/]",
            $"[cyan]Agents: {metrics.TotalAgentsSpawned}[/]",
            $"[cyan]MCP: {metrics.TotalMcpServers}[/]",
        };

        if (metrics.MostRecentActivity.HasValue)
        {
            var ago = DateTime.Now - metrics.MostRecentActivity.Value;
            parts.Add($"[dim]Last activity: {FormatAge(ago)}[/]");
        }

        return new Markup("  " + string.Join("  │  ", parts));
    }

    /// <summary>
    /// Builds the session table with color-coded status rows.
    /// </summary>
    private static IRenderable BuildSessionTable(IReadOnlyList<SessionSummary> summaries)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Active Sessions[/]")
            .AddColumn(new TableColumn("[bold]Session ID[/]").Centered())
            .AddColumn(new TableColumn("[bold]Type[/]").Centered())
            .AddColumn(new TableColumn("[bold]Machine[/]"))
            .AddColumn(new TableColumn("[bold]Status[/]").Centered())
            .AddColumn(new TableColumn("[bold]Duration[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Agents[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Last Activity[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Working Dir[/]"));

        int totalAgents = 0;
        int totalSessions = 0;

        foreach (var s in summaries)
        {
            totalSessions++;
            totalAgents += s.AgentCount;

            var statusColor = s.Status switch
            {
                SessionStatus.Active => "green",
                SessionStatus.Stale => "yellow",
                SessionStatus.Completed => "grey",
                _ => "dim",
            };

            var statusIcon = s.Status switch
            {
                SessionStatus.Active => "●",
                SessionStatus.Stale => "◐",
                SessionStatus.Completed => "○",
                _ => "?",
            };

            var age = DateTime.Now - s.LastActivity;
            var cwdDisplay = TruncatePath(s.WorkingDirectory, 30);

            table.AddRow(
                new Markup($"[{statusColor}]{Markup.Escape(s.ShortId)}[/]"),
                new Markup($"[dim]{Markup.Escape(s.SessionType)}[/]"),
                new Markup($"[dim]{Markup.Escape(s.MachineName)}[/]"),
                new Markup($"[{statusColor}]{statusIcon} {s.Status}[/]"),
                new Markup($"[{statusColor}]{FormatDuration(s.Duration)}[/]"),
                new Markup($"[cyan]{s.AgentCount}[/]"),
                new Markup($"[{statusColor}]{FormatAge(age)}[/]"),
                new Markup($"[dim]{Markup.Escape(cwdDisplay)}[/]")
            );
        }

        // Summary footer row
        table.AddRow(
            new Markup("[bold]TOTAL[/]"),
            new Markup(""),
            new Markup(""),
            new Markup($"[bold]{totalSessions} sessions[/]"),
            new Markup(""),
            new Markup($"[bold cyan]{totalAgents}[/]"),
            new Markup(""),
            new Markup("")
        );

        return table;
    }

    // ── Formatting helpers ───────────────────────────────────────────────

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 30) return "just now";
        if (age.TotalMinutes < 1) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1) return $"{(int)duration.TotalSeconds}s";
        if (duration.TotalHours < 1) return $"{(int)duration.TotalMinutes}m";
        if (duration.TotalDays < 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.Length <= maxLength) return path;

        // Show the last segment(s) with ellipsis
        var sep = Path.DirectorySeparatorChar;
        var parts = path.Split(sep);
        var result = parts[^1];
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var candidate = parts[i] + sep + result;
            if (candidate.Length + 3 > maxLength) break;
            result = candidate;
        }
        return "…" + sep + result;
    }
}
