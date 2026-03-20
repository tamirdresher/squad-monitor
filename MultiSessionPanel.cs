using Spectre.Console;
using Spectre.Console.Rendering;

namespace SquadMonitor;

/// <summary>
/// Builds Spectre.Console renderables for the multi-session dashboard panel.
/// Color coding: Green = Active, Yellow = Stale, Gray = Completed.
/// Runaway sessions (≥ 3× average burn rate) are highlighted in red with a ⚠ warning.
/// </summary>
public static class MultiSessionPanel
{
    /// <summary>
    /// Builds the complete multi-session panel including the session table,
    /// an aggregate summary row, and lifecycle event feed.
    /// </summary>
    public static IRenderable Build(SessionAggregator aggregator, SessionManager? sessionManager = null)
    {
        var sections = new List<IRenderable>();

        // Header
        var header = new Rule("[green bold]Multi-Session Monitor[/]") { Justification = Justify.Left };
        sections.Add(header);

        // Aggregate metrics bar
        var metrics = aggregator.GetAggregateMetrics();
        sections.Add(BuildMetricsSummary(metrics));
        sections.Add(Text.Empty);

        // Runaway alert banner
        if (metrics.RunawaySessions > 0)
        {
            sections.Add(new Markup($"  [red bold]⚠  {metrics.RunawaySessions} runaway session(s) detected — burn rate ≥ 3× average[/]"));
            sections.Add(Text.Empty);
        }

        // Session table
        var summaries = aggregator.GetSessionSummaries();
        if (summaries.Count == 0)
        {
            sections.Add(new Markup("[dim]  No sessions detected. Ensure Copilot CLI or Agency sessions are running.[/]"));
        }
        else
        {
            sections.Add(BuildSessionTable(summaries, metrics.AverageBurnRatePerMinute));
        }

        sections.Add(Text.Empty);

        // Lifecycle event feed (last 8 events)
        if (sessionManager is not null)
        {
            var events = sessionManager.LifecycleEvents;
            if (events.Count > 0)
            {
                sections.Add(BuildLifecycleFeed(events));
                sections.Add(Text.Empty);
            }
        }

        return new Rows(sections);
    }

    /// <summary>
    /// Builds a single-line aggregate metrics bar including token totals and cost.
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

        // Token aggregation row (only shown when data is available)
        var tokenParts = new List<string>();
        if (metrics.TotalTokensAcrossSessions > 0)
        {
            tokenParts.Add($"[bold]Tokens:[/] [blue]{TokenTracker.FormatTokenCount(metrics.TotalTokensAcrossSessions)}[/]");
            tokenParts.Add($"[bold]Cost:[/] [white]${metrics.TotalCostAcrossSessions:F2}[/]");
        }
        if (metrics.AverageBurnRatePerMinute > 0)
        {
            tokenParts.Add($"[dim]Avg burn: {metrics.AverageBurnRatePerMinute:F0} tok/min[/]");
        }
        if (metrics.RunawaySessions > 0)
        {
            tokenParts.Add($"[red bold]⚠ Runaway: {metrics.RunawaySessions}[/]");
        }

        var lines = new List<IRenderable>
        {
            new Markup("  " + string.Join("  │  ", parts))
        };
        if (tokenParts.Count > 0)
        {
            lines.Add(new Markup("  " + string.Join("  │  ", tokenParts)));
        }

        return new Rows(lines);
    }

    /// <summary>
    /// Builds the session table with color-coded status rows, token stats, and burn rates.
    /// </summary>
    private static IRenderable BuildSessionTable(IReadOnlyList<SessionSummary> summaries, double avgBurnRate)
    {
        var hasTokenData = summaries.Any(s => s.TotalTokens > 0);

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

        if (hasTokenData)
        {
            table.AddColumn(new TableColumn("[bold]Tokens[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Cost[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Burn/min[/]").RightAligned());
        }

        int totalAgents = 0;
        int totalSessions = 0;
        long totalTokens = 0;
        double totalCost = 0;

        foreach (var s in summaries)
        {
            totalSessions++;
            totalAgents += s.AgentCount;
            totalTokens += s.TotalTokens;
            totalCost += s.EstimatedCost;

            var statusColor = s.Status switch
            {
                SessionStatus.Active    => "green",
                SessionStatus.Stale     => "yellow",
                SessionStatus.Completed => "grey",
                _                       => "dim",
            };

            var statusIcon = s.Status switch
            {
                SessionStatus.Active    => "●",
                SessionStatus.Stale     => "◐",
                SessionStatus.Completed => "○",
                _                       => "?",
            };

            // Runaway sessions get a prominent red marker
            var idDisplay = s.IsRunaway
                ? $"[red bold]⚠ {Markup.Escape(s.ShortId)}[/]"
                : $"[{statusColor}]{Markup.Escape(s.ShortId)}[/]";

            var age = DateTime.Now - s.LastActivity;
            var cwdDisplay = TruncatePath(s.WorkingDirectory, 30);

            var baseRow = new List<IRenderable>
            {
                new Markup(idDisplay),
                new Markup($"[dim]{Markup.Escape(s.SessionType)}[/]"),
                new Markup($"[dim]{Markup.Escape(s.MachineName)}[/]"),
                new Markup($"[{statusColor}]{statusIcon} {s.Status}[/]"),
                new Markup($"[{statusColor}]{FormatDuration(s.Duration)}[/]"),
                new Markup($"[cyan]{s.AgentCount}[/]"),
                new Markup($"[{statusColor}]{FormatAge(age)}[/]"),
                new Markup($"[dim]{Markup.Escape(cwdDisplay)}[/]"),
            };

            if (hasTokenData)
            {
                var burnColor = s.IsRunaway ? "red bold" :
                                s.BurnRatePerMinute > avgBurnRate * 1.5 ? "yellow" : "green";
                var burnDisplay = s.BurnRatePerMinute > 0
                    ? $"[{burnColor}]{s.BurnRatePerMinute:F0}[/]"
                    : "[dim]—[/]";

                baseRow.Add(new Markup(s.TotalTokens > 0
                    ? $"[blue]{TokenTracker.FormatTokenCount(s.TotalTokens)}[/]"
                    : "[dim]—[/]"));
                baseRow.Add(new Markup(s.EstimatedCost > 0
                    ? $"[white]${s.EstimatedCost:F2}[/]"
                    : "[dim]—[/]"));
                baseRow.Add(new Markup(burnDisplay));
            }

            table.AddRow(baseRow.ToArray());
        }

        // Summary footer row
        var footerRow = new List<IRenderable>
        {
            new Markup("[bold]TOTAL[/]"),
            new Markup(""),
            new Markup(""),
            new Markup($"[bold]{totalSessions} sessions[/]"),
            new Markup(""),
            new Markup($"[bold cyan]{totalAgents}[/]"),
            new Markup(""),
            new Markup(""),
        };

        if (hasTokenData)
        {
            footerRow.Add(new Markup($"[bold blue]{TokenTracker.FormatTokenCount(totalTokens)}[/]"));
            footerRow.Add(new Markup($"[bold white]${totalCost:F2}[/]"));
            footerRow.Add(new Markup(""));
        }

        table.AddRow(footerRow.ToArray());

        return table;
    }

    /// <summary>
    /// Builds a compact lifecycle event feed showing the most recent events.
    /// </summary>
    private static IRenderable BuildLifecycleFeed(IReadOnlyList<SessionLifecycleEvent> events)
    {
        var sections = new List<IRenderable>
        {
            new Markup("  [dim bold]── Lifecycle Events ──[/]")
        };

        foreach (var evt in events.TakeLast(8))
        {
            var (icon, color) = evt.EventType switch
            {
                SessionLifecycleEventType.Started    => ("▶", "green"),
                SessionLifecycleEventType.Resumed    => ("↺", "cyan"),
                SessionLifecycleEventType.BecameStale => ("◐", "yellow"),
                SessionLifecycleEventType.Ended      => ("■", "grey"),
                _                                    => ("·", "dim"),
            };

            var detail = evt.Detail is not null ? $" [dim]{Markup.Escape(evt.Detail)}[/]" : "";
            sections.Add(new Markup(
                $"  [{color}]{icon}[/] [dim]{evt.Timestamp:HH:mm:ss}[/] [{color}]{Markup.Escape(evt.EventType.ToString())}[/] " +
                $"[dim]{Markup.Escape(evt.SessionName)}[/]{detail}"));
        }

        return new Rows(sections);
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
