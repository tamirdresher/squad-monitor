using Spectre.Console;
using Spectre.Console.Rendering;

namespace SquadMonitor;

/// <summary>
/// Renders a token usage dashboard panel for the Spectre.Console terminal UI.
/// Shows per-agent costs, session summaries, daily trends, and active alerts.
/// </summary>
public static class TokenDashboardPanel
{
    /// <summary>
    /// Builds a complete Spectre.Console renderable section showing token tracking data.
    /// </summary>
    public static IRenderable Build(string userProfile)
    {
        var items = new List<IRenderable>();
        var header = new Rule("[magenta bold]Token Tracking & Cost Analysis[/]") { Justification = Justify.Left };
        items.Add(header);

        try
        {
            var tracker = new TokenTracker();
            tracker.ScanLogs(userProfile);

            if (tracker.ModelStats.Count == 0)
            {
                items.Add(new Markup("[dim]  No token usage data found in recent logs[/]"));
                items.Add(Text.Empty);
                return new Rows(items);
            }

            // ── Per-agent cost table ──
            items.Add(new Markup("[yellow bold]  Agent Token Usage[/]"));
            var agentTable = new Table()
                .Border(TableBorder.Simple)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[dim]Agent[/]").LeftAligned())
                .AddColumn(new TableColumn("[dim]Calls[/]").RightAligned())
                .AddColumn(new TableColumn("[dim]Input[/]").RightAligned())
                .AddColumn(new TableColumn("[dim]Output[/]").RightAligned())
                .AddColumn(new TableColumn("[dim]Est. Cost[/]").RightAligned());

            foreach (var agent in tracker.AgentStats.Values.OrderByDescending(a => a.EstimatedCost))
            {
                var costColor = agent.EstimatedCost < 5 ? "green" : agent.EstimatedCost < 20 ? "yellow" : "red";
                agentTable.AddRow(
                    $"[cyan]{Markup.Escape(agent.AgentName)}[/]",
                    $"[white]{agent.Calls}[/]",
                    $"[blue]{TokenTracker.FormatTokenCount(agent.InputTokens)}[/]",
                    $"[green]{TokenTracker.FormatTokenCount(agent.OutputTokens)}[/]",
                    $"[{costColor}]${agent.EstimatedCost:F2}[/]"
                );
            }
            items.Add(agentTable);

            // ── Session summary (top 5 by cost) ──
            if (tracker.SessionStats.Count > 0)
            {
                items.Add(new Markup($"[yellow bold]  Top Sessions by Cost[/] [dim]({tracker.SessionStats.Count} total)[/]"));
                var sessionTable = new Table()
                    .Border(TableBorder.Simple)
                    .BorderColor(Color.Grey)
                    .AddColumn(new TableColumn("[dim]Session[/]").LeftAligned())
                    .AddColumn(new TableColumn("[dim]Calls[/]").RightAligned())
                    .AddColumn(new TableColumn("[dim]Tokens[/]").RightAligned())
                    .AddColumn(new TableColumn("[dim]Cost[/]").RightAligned())
                    .AddColumn(new TableColumn("[dim]Duration[/]").RightAligned());

                foreach (var session in tracker.SessionStats.Values.OrderByDescending(s => s.EstimatedCost).Take(5))
                {
                    var shortId = session.SessionId.Length > 16 ? session.SessionId[..8] + "..." : session.SessionId;
                    var duration = session.Duration;
                    var durStr = duration.TotalHours >= 1 ? $"{duration.TotalHours:F1}h" : $"{duration.TotalMinutes:F0}m";
                    var costColor = session.EstimatedCost < 5 ? "green" : session.EstimatedCost < 20 ? "yellow" : "red";

                    sessionTable.AddRow(
                        $"[white]{Markup.Escape(shortId)}[/]",
                        $"[white]{session.Calls}[/]",
                        $"[blue]{TokenTracker.FormatTokenCount(session.TotalTokens)}[/]",
                        $"[{costColor}]${session.EstimatedCost:F2}[/]",
                        $"[dim]{durStr}[/]"
                    );
                }
                items.Add(sessionTable);
            }

            // ── Daily report summary ──
            var report = new TokenReport(tracker);
            var dailyReport = report.GenerateDailyReport();

            items.Add(new Markup($"[yellow bold]  Today's Summary[/] [dim]({DateTime.Now:yyyy-MM-dd})[/]"));
            var costColor2 = dailyReport.TotalEstimatedCost < 10 ? "green" : dailyReport.TotalEstimatedCost < 30 ? "yellow" : "red";
            items.Add(new Markup(
                $"  Calls: [white]{dailyReport.TotalCalls:N0}[/]  |  " +
                $"Input: [blue]{TokenTracker.FormatTokenCount(dailyReport.TotalInputTokens)}[/]  |  " +
                $"Output: [green]{TokenTracker.FormatTokenCount(dailyReport.TotalOutputTokens)}[/]  |  " +
                $"Cost: [{costColor2}]${dailyReport.TotalEstimatedCost:F2}[/]"
            ));

            // ── Alerts ──
            var alertService = new TokenAlertService(TokenAlertConfig.FromEnvironment());
            var alerts = alertService.Evaluate(tracker);

            if (alertService.ActiveAlerts.Count > 0)
            {
                items.Add(Text.Empty);
                items.Add(new Rule("[red bold]⚠ Alerts[/]") { Justification = Justify.Left });
                foreach (var alert in alertService.ActiveAlerts.OrderByDescending(a => a.Level))
                {
                    var icon = alert.Level == AlertLevel.Critical ? "🔴" : "🟡";
                    var color = alert.Level == AlertLevel.Critical ? "red" : "yellow";
                    items.Add(new Markup($"  {icon} [{color}]{Markup.Escape(alert.Message)}[/]"));
                }
            }
            else
            {
                items.Add(new Markup("  [green]✓ All usage within configured thresholds[/]"));
            }

            // ── Total summary line ──
            items.Add(Text.Empty);
            items.Add(new Markup(
                $"  [dim]Total:[/] [white]${tracker.TotalEstimatedCost:F2}[/] across " +
                $"[white]{tracker.SessionStats.Count}[/] sessions, " +
                $"[white]{tracker.AgentStats.Count}[/] agents, " +
                $"[white]{tracker.ModelStats.Count}[/] models"
            ));
        }
        catch (Exception ex)
        {
            items.Add(new Markup($"[red]  Error: {Markup.Escape(ex.Message)}[/]"));
        }

        items.Add(Text.Empty);
        return new Rows(items);
    }

    /// <summary>
    /// Returns Spectre.Console markup lines for the SharpUI-based TUI (text-based rendering).
    /// </summary>
    public static List<string> BuildMarkupLines(string userProfile)
    {
        var lines = new List<string>();
        lines.Add("");
        lines.Add("[magenta bold] ── Token Tracking & Cost Analysis ──[/]");
        lines.Add("");

        try
        {
            var tracker = new TokenTracker();
            tracker.ScanLogs(userProfile);

            if (tracker.ModelStats.Count == 0)
            {
                lines.Add("[dim]  No token usage data found[/]");
                return lines;
            }

            // Agent breakdown
            lines.Add(" [yellow bold]Per-Agent Usage:[/]");
            lines.Add($" [dim]{"Agent",-18} {"Calls",6} {"Input",9} {"Output",9} {"Cost",8}[/]");
            lines.Add(" [dim]" + new string('─', 54) + "[/]");

            foreach (var agent in tracker.AgentStats.Values.OrderByDescending(a => a.EstimatedCost).Take(8))
            {
                var name = agent.AgentName.Length > 16 ? agent.AgentName[..13] + "..." : agent.AgentName;
                var costClr = agent.EstimatedCost < 5 ? "green" : agent.EstimatedCost < 20 ? "yellow" : "red";
                lines.Add($" [cyan]{Esc(name),-18}[/] [white]{agent.Calls,6}[/] [blue]{TokenTracker.FormatTokenCount(agent.InputTokens),9}[/] [green]{TokenTracker.FormatTokenCount(agent.OutputTokens),9}[/] [{costClr}]{"$" + agent.EstimatedCost.ToString("F2"),8}[/]");
            }
            lines.Add("");

            // Today's summary
            var report = new TokenReport(tracker);
            var daily = report.GenerateDailyReport();
            var costClr2 = daily.TotalEstimatedCost < 10 ? "green" : daily.TotalEstimatedCost < 30 ? "yellow" : "red";
            lines.Add($" [dim]Today:[/] {daily.TotalCalls} calls  |  [{costClr2}]${daily.TotalEstimatedCost:F2}[/]  |  " +
                      $"[blue]{TokenTracker.FormatTokenCount(daily.TotalInputTokens)}[/] in / [green]{TokenTracker.FormatTokenCount(daily.TotalOutputTokens)}[/] out");

            // Alerts
            var alertService = new TokenAlertService(TokenAlertConfig.FromEnvironment());
            alertService.Evaluate(tracker);
            lines.AddRange(alertService.RenderAlertLines());

            // Overall totals
            lines.Add("");
            lines.Add($" [dim]Total:[/] [white]${tracker.TotalEstimatedCost:F2}[/]  |  " +
                      $"{tracker.SessionStats.Count} sessions  |  " +
                      $"{tracker.AgentStats.Count} agents  |  " +
                      $"{tracker.ModelStats.Count} models");
        }
        catch
        {
            lines.Add("[red]  Error reading token tracking data[/]");
        }

        return lines;
    }

    private static string Esc(string s) => Spectre.Console.Markup.Escape(s);
}
