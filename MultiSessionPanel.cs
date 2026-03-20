using Spectre.Console;
using Spectre.Console.Rendering;

namespace SquadMonitor;

/// <summary>
/// Builds Spectre.Console renderables for the multi-session dashboard panel.
/// Color coding: Green = Active, Yellow = Stale, Gray = Completed, Red = Runaway.
/// </summary>
public static class MultiSessionPanel
{
    /// <summary>
    /// Builds the complete multi-session panel including the session table
    /// and an aggregate summary row. Pass a <paramref name="tracker"/> to enable
    /// cross-session token aggregation, side-by-side burn rates, and runaway detection.
    /// Pass a <paramref name="lifecycleLogger"/> to log and surface lifecycle events.
    /// </summary>
    public static IRenderable Build(
        SessionAggregator aggregator,
        TokenTracker? tracker = null,
        LifecycleEventLogger? lifecycleLogger = null)
    {
        var sections = new List<IRenderable>();

        // Header
        var header = new Rule("[green bold]Multi-Session Monitor[/]") { Justification = Justify.Left };
        sections.Add(header);

        // Aggregate metrics (token-enriched when tracker is provided)
        SessionAggregateMetrics metrics;
        IReadOnlyList<TokenEnrichedSummary> tokenSummaries;

        if (tracker is not null)
        {
            metrics = aggregator.GetAggregateMetrics(tracker);
            tokenSummaries = aggregator.GetTokenEnrichedSummaries(tracker);
        }
        else
        {
            metrics = aggregator.GetAggregateMetrics();
            tokenSummaries = [];
        }

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
            sections.Add(tracker is not null
                ? BuildTokenSessionTable(summaries, tokenSummaries)
                : BuildSessionTable(summaries));
        }

        // Runaway warning panel
        var runaways = tokenSummaries.Where(t => t.IsRunaway).ToList();
        if (runaways.Count > 0)
        {
            sections.Add(Text.Empty);
            sections.Add(BuildRunawayWarnings(runaways, metrics.AverageBurnRateTokensPerHour));
        }

        // Recent lifecycle events
        if (lifecycleLogger is not null)
        {
            var recent = lifecycleLogger.GetRecent(6).ToList();
            if (recent.Count > 0)
            {
                sections.Add(Text.Empty);
                sections.Add(BuildLifecyclePanel(recent));
            }
        }

        sections.Add(Text.Empty);
        return new Rows(sections);
    }

    // ── Metrics summary bar ──────────────────────────────────────────────

    /// <summary>
    /// Builds a single-line aggregate metrics bar, including token totals and burn rate
    /// when the metrics record carries them.
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

        if (metrics.TotalTokensAllSessions > 0)
        {
            parts.Add($"[blue]Tokens: {TokenTracker.FormatTokenCount(metrics.TotalTokensAllSessions)}[/]");
            parts.Add($"[white]Cost: ${metrics.TotalCostAllSessions:F2}[/]");
        }

        if (metrics.TotalBurnRateTokensPerHour > 0)
        {
            parts.Add($"[dim]Burn: {FormatBurnRate(metrics.TotalBurnRateTokensPerHour)}/h[/]");
        }

        if (metrics.RunawaySessions > 0)
        {
            parts.Add($"[red bold]⚠ Runaway: {metrics.RunawaySessions}[/]");
        }

        if (metrics.MostRecentActivity.HasValue)
        {
            var ago = DateTime.Now - metrics.MostRecentActivity.Value;
            parts.Add($"[dim]Last activity: {FormatAge(ago)}[/]");
        }

        return new Markup("  " + string.Join("  │  ", parts));
    }

    // ── Session tables ───────────────────────────────────────────────────

    /// <summary>
    /// Builds the basic session table without token data.
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

            var (statusColor, statusIcon) = GetStatusStyle(s.Status);
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

    /// <summary>
    /// Builds a token-enriched session table showing burn rates, token totals, cost,
    /// and a runaway indicator per session.
    /// </summary>
    private static IRenderable BuildTokenSessionTable(
        IReadOnlyList<SessionSummary> summaries,
        IReadOnlyList<TokenEnrichedSummary> tokenSummaries)
    {
        // Quick-lookup map: session ID → token enrichment data
        var tokenMap = tokenSummaries.ToDictionary(t => t.SessionId, StringComparer.OrdinalIgnoreCase);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Active Sessions — Token Usage[/]")
            .AddColumn(new TableColumn("[bold]Session[/]").Centered())
            .AddColumn(new TableColumn("[bold]Type[/]").Centered())
            .AddColumn(new TableColumn("[bold]Status[/]").Centered())
            .AddColumn(new TableColumn("[bold]Duration[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Agents[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Tokens[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Cost[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Burn/h[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]⚠[/]").Centered());

        long grandTotalTokens = 0;
        double grandTotalCost = 0;
        double grandTotalBurn = 0;
        int totalSessions = 0;

        foreach (var s in summaries)
        {
            totalSessions++;
            tokenMap.TryGetValue(s.Id, out var td);

            var (statusColor, statusIcon) = GetStatusStyle(s.Status);
            var isRunaway = td?.IsRunaway ?? false;
            var rowColor = isRunaway ? "red" : statusColor;

            grandTotalTokens += td?.TotalTokens ?? 0;
            grandTotalCost += td?.EstimatedCost ?? 0;
            grandTotalBurn += td?.BurnRateTokensPerHour ?? 0;

            var tokensMarkup = td?.TotalTokens > 0
                ? $"[blue]{TokenTracker.FormatTokenCount(td.TotalTokens)}[/]"
                : "[dim]—[/]";
            var costMarkup = td?.EstimatedCost > 0
                ? $"[white]${td.EstimatedCost:F2}[/]"
                : "[dim]—[/]";
            var burnMarkup = td?.BurnRateTokensPerHour > 0
                ? $"[{(isRunaway ? "red bold" : "dim")}]{FormatBurnRate(td.BurnRateTokensPerHour)}[/]"
                : "[dim]—[/]";
            var runawayCellMarkup = isRunaway ? "[red bold]⚠[/]" : "[dim]·[/]";

            table.AddRow(
                new Markup($"[{rowColor}]{Markup.Escape(s.ShortId)}[/]"),
                new Markup($"[dim]{Markup.Escape(s.SessionType)}[/]"),
                new Markup($"[{rowColor}]{statusIcon} {s.Status}[/]"),
                new Markup($"[{rowColor}]{FormatDuration(s.Duration)}[/]"),
                new Markup($"[cyan]{s.AgentCount}[/]"),
                new Markup(tokensMarkup),
                new Markup(costMarkup),
                new Markup(burnMarkup),
                new Markup(runawayCellMarkup)
            );
        }

        // Summary footer
        table.AddRow(
            new Markup("[bold]TOTAL[/]"),
            new Markup(""),
            new Markup($"[bold]{totalSessions} sessions[/]"),
            new Markup(""),
            new Markup(""),
            grandTotalTokens > 0
                ? new Markup($"[bold blue]{TokenTracker.FormatTokenCount(grandTotalTokens)}[/]")
                : new Markup("[dim]—[/]"),
            grandTotalCost > 0
                ? new Markup($"[bold white]${grandTotalCost:F2}[/]")
                : new Markup("[dim]—[/]"),
            grandTotalBurn > 0
                ? new Markup($"[dim]{FormatBurnRate(grandTotalBurn)}[/]")
                : new Markup("[dim]—[/]"),
            new Markup("")
        );

        return table;
    }

    // ── Runaway warning panel ────────────────────────────────────────────

    private static IRenderable BuildRunawayWarnings(
        IReadOnlyList<TokenEnrichedSummary> runaways,
        double avgBurnRate)
    {
        var lines = new List<IRenderable>
        {
            new Rule("[red bold]⚠ Runaway Session Detection[/]") { Justification = Justify.Left },
            new Markup($"  [dim]Average burn rate across all sessions: {FormatBurnRate(avgBurnRate)}/h[/]"),
            new Markup($"  [dim]Threshold (3× average): {FormatBurnRate(avgBurnRate * 3)}/h[/]"),
            Text.Empty,
        };

        foreach (var r in runaways)
        {
            var multiple = avgBurnRate > 0 ? r.BurnRateTokensPerHour / avgBurnRate : 0;
            lines.Add(new Markup(
                $"  [red]●[/] [bold]{Markup.Escape(r.ShortId)}[/] ({r.SessionType})  " +
                $"burn [red bold]{FormatBurnRate(r.BurnRateTokensPerHour)}/h[/]  " +
                $"[dim]({multiple:F1}× avg)[/]  " +
                $"total [blue]{TokenTracker.FormatTokenCount(r.TotalTokens)}[/] tokens  " +
                $"[white]${r.EstimatedCost:F2}[/]"
            ));
        }

        return new Rows(lines);
    }

    // ── Lifecycle events panel ───────────────────────────────────────────

    private static IRenderable BuildLifecyclePanel(IReadOnlyList<LifecycleEvent> events)
    {
        var lines = new List<IRenderable>
        {
            new Rule("[green bold]Recent Lifecycle Events[/]") { Justification = Justify.Left },
        };

        foreach (var evt in events)
        {
            var (icon, color) = evt.Kind switch
            {
                LifecycleEventKind.SessionStarted    => ("▶", "green"),
                LifecycleEventKind.SessionEnded      => ("■", "grey"),
                LifecycleEventKind.PeakUsageDetected => ("↑", "cyan"),
                LifecycleEventKind.RunawayDetected   => ("⚠", "red"),
                _ => ("•", "dim"),
            };

            lines.Add(new Markup(
                $"  [{color}]{icon}[/] [dim]{evt.Timestamp:HH:mm:ss}[/]  " +
                $"[bold {color}]{Markup.Escape(evt.SessionId)}[/]  " +
                $"[dim]{Markup.Escape(evt.Message ?? "")}[/]"
            ));
        }

        return new Rows(lines);
    }

    // ── Shared style helper ──────────────────────────────────────────────

    private static (string color, string icon) GetStatusStyle(SessionStatus status) => status switch
    {
        SessionStatus.Active    => ("green",  "●"),
        SessionStatus.Stale     => ("yellow", "◐"),
        SessionStatus.Completed => ("grey",   "○"),
        _                       => ("dim",    "?"),
    };

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

    /// <summary>Formats a tokens-per-hour value for compact dashboard display (e.g. "1.2K").</summary>
    private static string FormatBurnRate(double tokensPerHour) =>
        TokenTracker.FormatTokenCount((long)tokensPerHour);

    private static string TruncatePath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.Length <= maxLength) return path;

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
