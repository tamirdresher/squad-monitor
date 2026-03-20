using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Rendering;
using Spectre.Console;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using HorizontalAlignment = SharpConsoleUI.Layout.HorizontalAlignment;
using VerticalAlignment = SharpConsoleUI.Layout.VerticalAlignment;
using TextJustification = SharpConsoleUI.Layout.TextJustification;

namespace SquadMonitor;

/// <summary>
/// SharpConsoleUI-based multi-panel TUI dashboard for Squad Monitor.
///
/// Layout:
///   ┌─────────────────────────────────────────────────────────────────────────┐
///   │  Squad Monitor v2 — 2026-03-19 14:23:45 — Next refresh: 4s     header │
///   ├────────────────────────────────┬────────────────────────────────────────┤
///   │  GitHub Issues    TableControl │  TabControl [Ralph|Tokens|Sessions]   │
///   │  #  Title    Author   Age     │  ┌─ Ralph ─────────────────────────┐  │
///   │  42 Fix auth tamir    2h ago  │  │ ● Running  Round: 12  Fail: 0  │  │
///   │  ...                          │  │ Last run: 2m ago               │  │
///   ├────────────────────────────────┤  │ Heartbeat: 45s ago             │  │
///   │  Pull Requests    TableControl│  └─────────────────────────────────┘  │
///   │  #  Title  Author Branch Rev  │  ┌─ Tokens ────────────────────────┐  │
///   │  ...                          │  │ Model  Calls Prompt Cache% Cost │  │
///   │                               │  │ [table rows...]                 │  │
///   │                               │  │ ▇▆▅▆▇█▇▆  call rate sparkline  │  │
///   │                               │  └─────────────────────────────────┘  │
///   ├───────────────── splitter ────┴────────────────────────────────────────┤
///   │  ▁▂▃▄▅▆▇█▆▅▃▂▁▂▃▄▅▆▇   activity sparkline (2 rows)                  │
///   │  Live Agent Feed                                                       │
///   │  14:23:01 [Ralph-abc] ⚡ bash → git status                             │
///   │  14:23:05 [CLI-1234]  🔍 grep → "SharpConsoleUI"                      │
///   │  14:23:08 [Ralph-abc] ✏️  edit → Program.cs                            │
///   ├───────────────────────────────────────────────────────────────────────-┤
///   │  q Quit  r Refresh  Tab Switch  1-3 Tabs  ↑↓ Scroll  / Filter        │
///   └───────────────────────────────────────────────────────────────────────-┘
/// </summary>
public static class SharpUI
{
    private static ConsoleWindowSystem? _ws;

    // ── Color scheme ────────────────────────────────────────────────────────
    private static readonly SharpConsoleUI.Color BgDark = new(28, 28, 28);
    private static readonly SharpConsoleUI.Color BorderNormal = new(88, 88, 88);
    private static readonly SharpConsoleUI.Color TextMuted = new(128, 128, 128);
    private static readonly SharpConsoleUI.Color AccentCyan = SharpConsoleUI.Color.Cyan1;

    // ── Data records ────────────────────────────────────────────────────────
    private record GitHubIssue(string Number, string Title, string Author, string Assignees, string Age, string? RepoSlug);
    private record GitHubPR(string Number, string Title, string Author, string Branch, string Review, bool IsDraft, string Age, string? RepoSlug);
    private record TokenModelStats(string Model, int Calls, long Prompt, long Completion, long Cached, double CachePct, double AvgLatencyMs, double Cost);
    private record FeedEntry(DateTime Time, string Icon, string Text, string Session, string SessionColor);

    // ── Caching infrastructure to reduce CPU/IO/network pressure ──────
    private static List<GitHubIssue>? _cachedIssues;
    private static List<GitHubPR>? _cachedPRs;
    private static DateTime _cachedGitHubTime = DateTime.MinValue;
    private static readonly TimeSpan GitHubCacheTtl = TimeSpan.FromSeconds(60);

    private static (List<LiveSessionInfo> Sessions, List<FeedEntry> Entries)? _cachedFeedData;
    private static DateTime _cachedFeedTime = DateTime.MinValue;
    private static readonly TimeSpan FeedCacheTtl = TimeSpan.FromSeconds(30);

    private static (List<TokenModelStats> Models, string Summary)? _cachedTokenData;
    private static DateTime _cachedTokenTime = DateTime.MinValue;
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromSeconds(60);

    // ── Activity sparkline rolling history ───────────────────────────────
    private static readonly List<double> _feedActivityHistory = new();
    private static DateTime _lastFeedHistoryBucket = DateTime.MinValue;
    private static int _currentBucketCount;

    public static async Task RunAsync(string? teamRoot, int interval = 30)
    {
        if (teamRoot == null)
        {
            Console.Error.WriteLine("Error: Could not find .squad directory. Run from team root.");
            return;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var disableGitHub = !IsGhCliAvailable();

        try
        {
            // ── Create window system ─────────────────────────────────────
            var baseOpts = ConsoleWindowSystemOptions.Create(
                enableMetrics: false,
                enableFrameRateLimiting: true,
                targetFPS: 12);

            var statusBarOpts = new StatusBarOptions(
                ShowStartButton: false,
                StartButtonLocation: StatusBarLocation.Bottom,
                StartButtonPosition: StartButtonPosition.Left,
                StartButtonText: "",
                StartMenuShortcutKey: ConsoleKey.F1,
                StartMenuShortcutModifiers: 0,
                ShowSystemMenuCategory: false,
                ShowWindowListInMenu: false,
                ShowTopStatus: false,
                ShowBottomStatus: false,
                ShowTaskBar: false);
            var options = baseOpts with { StatusBarOptions = statusBarOpts };

            _ws = new ConsoleWindowSystem(RenderMode.Buffer, null!, options);

            // ── Build controls ───────────────────────────────────────────

            // Header
            var headerCtrl = Controls.Markup($"[yellow bold] Squad Monitor v2[/] [dim]— {DateTime.Now:yyyy-MM-dd HH:mm:ss}[/]")
                .StickyTop()
                .WithName("header")
                .Build();

            var headerRule = Controls.RuleBuilder()
                .WithColor(new SharpConsoleUI.Color(50, 55, 70))
                .StickyTop()
                .Build();

            // Issues table
            var issuesTable = Controls.Table()
                .WithTitle("GitHub Issues")
                .AddColumn("#", TextJustification.Right, 6)
                .AddColumn("Title", TextJustification.Left)
                .AddColumn("Author", TextJustification.Left, 14)
                .AddColumn("Assignees", TextJustification.Left, 14)
                .AddColumn("Age", TextJustification.Right, 8)
                .WithBorderStyle(BorderStyle.Rounded)
                .WithBorderColor(BorderNormal)
                .WithHeaderColors(AccentCyan, BgDark)
                .WithSorting()
                .WithFiltering()
                .WithFuzzyFilter()
                .WithName("issuesTable")
                .StretchHorizontal()
                .WithVerticalAlignment(VerticalAlignment.Top)
                .Build();

            // PRs table
            var prsTable = Controls.Table()
                .WithTitle("Pull Requests")
                .AddColumn("#", TextJustification.Right, 6)
                .AddColumn("Title", TextJustification.Left)
                .AddColumn("Author", TextJustification.Left, 14)
                .AddColumn("Branch", TextJustification.Left, 22)
                .AddColumn("Review", TextJustification.Left, 12)
                .AddColumn("Age", TextJustification.Right, 8)
                .WithBorderStyle(BorderStyle.Rounded)
                .WithBorderColor(BorderNormal)
                .WithHeaderColors(AccentCyan, BgDark)
                .WithSorting()
                .WithFiltering()
                .WithFuzzyFilter()
                .WithName("prsTable")
                .StretchHorizontal()
                .WithVerticalAlignment(VerticalAlignment.Fill)
                .Build();

            // Left column scroll panel
            var leftScrollPanel = Controls.ScrollablePanel()
                .AddControl(issuesTable)
                .AddControl(prsTable)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("leftscroll")
                .Build();

            // Right panel: TabControl with Ralph / Tokens / Sessions

            // Ralph tab content
            var ralphMarkup = Controls.Markup("[dim]  Loading Ralph status...[/]")
                .WithName("ralphMarkup")
                .Build();
            var ralphScroll = Controls.ScrollablePanel()
                .AddControl(ralphMarkup)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("ralphScroll")
                .Build();

            // Tokens tab content
            var tokenTable = Controls.Table()
                .AddColumn("Model", TextJustification.Left)
                .AddColumn("Calls", TextJustification.Right, 7)
                .AddColumn("Prompt", TextJustification.Right, 10)
                .AddColumn("Compl", TextJustification.Right, 10)
                .AddColumn("Cache%", TextJustification.Right, 7)
                .AddColumn("Latency", TextJustification.Right, 8)
                .AddColumn("Cost", TextJustification.Right, 8)
                .WithBorderStyle(BorderStyle.Rounded)
                .WithBorderColor(BorderNormal)
                .WithHeaderColors(SharpConsoleUI.Color.Magenta1, BgDark)
                .NoBorder()
                .WithName("tokenTable")
                .StretchHorizontal()
                .Build();

            var tokenSpark = new SparklineBuilder()
                .WithName("tokenSpark")
                .WithHeight(2)
                .WithAutoFitDataPoints()
                .WithMode(SparklineMode.Block)
                .WithBarColor(SharpConsoleUI.Color.Magenta1)
                .WithGradient("warm")
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 1, 0, 0)
                .Build();

            var tokenSummaryMarkup = Controls.Markup("[dim]  Loading token stats...[/]")
                .WithName("tokenSummary")
                .Build();

            var tokensScroll = Controls.ScrollablePanel()
                .AddControl(tokenTable)
                .AddControl(tokenSpark)
                .AddControl(tokenSummaryMarkup)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("tokensScroll")
                .Build();

            // Sessions tab content
            var sessionMarkup = Controls.Markup("[dim]  Loading sessions...[/]")
                .WithName("sessionMarkup")
                .Build();
            var sessionsScroll = Controls.ScrollablePanel()
                .AddControl(sessionMarkup)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("sessionsScroll")
                .Build();

            // Tab control
            var rightTabs = Controls.TabControl()
                .AddTab("Ralph", ralphScroll)
                .AddTab("Tokens", tokensScroll)
                .AddTab("Sessions", sessionsScroll)
                .WithName("rightTabs")
                .Fill()
                .Build();

            // Main grid — needs VerticalAlignment.Fill + explicit Height for splitter resize
            var grid = Controls.HorizontalGrid()
                .Column(leftCol =>
                {
                    leftCol.Flex(6);
                    leftCol.Add(leftScrollPanel);
                })
                .Column(rightCol =>
                {
                    rightCol.Flex(4);
                    rightCol.Add(rightTabs);
                })
                .WithSplitterAfter(0)
                .WithVerticalAlignment(VerticalAlignment.Fill)
                .Build();

            // Feed section
            var feedSpark = new SparklineBuilder()
                .WithName("feedSpark")
                .WithHeight(3)
                .WithAutoFitDataPoints()
                .WithMode(SparklineMode.Block)
                .WithBarColor(SharpConsoleUI.Color.Green)
                .WithGradient(SharpConsoleUI.Color.DarkGreen, SharpConsoleUI.Color.Green, SharpConsoleUI.Color.Cyan1)
                .WithTitle("Activity", SharpConsoleUI.Color.Green)
                .WithTitlePosition(TitlePosition.Bottom)
                .WithBaseline(true, '┈', new SharpConsoleUI.Color(40, 50, 40), TitlePosition.Bottom)
                .WithInlineTitleBaseline(true)
                .WithBorder(BorderStyle.Rounded, new SharpConsoleUI.Color(50, 70, 50))
                .WithAlignment(HorizontalAlignment.Stretch)
                .Build();

            var feedRule = Controls.RuleBuilder()
                .WithTitle("Live Agent Feed")
                .WithColor(SharpConsoleUI.Color.Green)
                .Build();

            var feedMarkup = Controls.Markup("[dim]  Loading live agent feed...[/]")
                .WithName("feedMarkup")
                .Build();

            var feedPanel = Controls.ScrollablePanel()
                .AddControl(feedSpark)
                .AddControl(feedRule)
                .AddControl(feedMarkup)
                .WithAutoScroll(true)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithVerticalAlignment(VerticalAlignment.Fill)
                .WithName("feedpanel")
                .Build();
            feedPanel.Height = 16;

            // Horizontal splitter between grid and feed — auto-discovers neighbors
            var hSplitter = Controls.HorizontalSplitter()
                .WithMinHeights(8, 6)
                .WithName("hSplitter")
                .Build();

            // Status bar with clickable items
            Action doRefresh = () =>
            {
                _cachedIssues = null;
                _cachedPRs = null;
                _cachedFeedData = null;
                _cachedTokenData = null;
                RefreshAllPanels(headerCtrl, issuesTable, prsTable, ralphMarkup,
                    tokenTable, tokenSpark, tokenSummaryMarkup,
                    feedMarkup, feedSpark, sessionMarkup,
                    teamRoot, userProfile, disableGitHub, interval);
            };

            var statusBar = Controls.StatusBar()
                .AddLeft("q", "Quit", () => _ws?.Shutdown(0))
                .AddLeft("r", "Refresh", doRefresh)
                .AddLeftSeparator()
                .AddLeft("Tab", "Switch Panel")
                .AddLeft("1-3", "Tabs", () => rightTabs.ActiveTabIndex = (rightTabs.ActiveTabIndex + 1) % 3)
                .AddLeftSeparator()
                .AddLeft("/", "Filter")
                .AddRight("^", "Scroll Up")
                .AddRight("v", "Scroll Down")
                .WithAboveLine(true)
                .WithAboveLineColor(new SharpConsoleUI.Color(50, 55, 70))
                .WithBackgroundColor(new SharpConsoleUI.Color(20, 22, 35))
                .WithForegroundColor(new SharpConsoleUI.Color(180, 180, 180))
                .WithShortcutForegroundColor(SharpConsoleUI.Color.Cyan1)
                .StickyBottom()
                .WithName("statusbar")
                .Build();

            // Panel focus tracking for Tab key navigation
            var scrollPanels = new ScrollablePanelControl[] { leftScrollPanel, feedPanel };
            var panelNames = new[] { "Issues/PRs", "Agent Feed" };
            int focusedIdx = 0;

            // Background gradient: dark blue-black
            var bgGradient = ColorGradient.FromColors(
                new SharpConsoleUI.Color(20, 25, 45),
                new SharpConsoleUI.Color(8, 8, 16));

            // ── Build the main window ─────────────────────────────────────
            var window = new WindowBuilder(_ws)
                .Maximized()
                .HideTitle()
                .Resizable(false)
                .Movable(false)
                .HideTitleButtons()
                .WithActiveBorderColor(new SharpConsoleUI.Color(60, 90, 140))
                .WithInactiveBorderColor(new SharpConsoleUI.Color(60, 90, 140))
                .WithBackgroundGradient(bgGradient, GradientDirection.Vertical)
                .AddControl(headerCtrl)
                .AddControl(headerRule)
                .AddControl(grid)
                .AddControl(hSplitter)
                .AddControl(feedPanel)
                .AddControl(statusBar)
                .WithAsyncWindowThread(async (win, ct) =>
                {
                    // Initial data load
                    RefreshAllPanels(headerCtrl, issuesTable, prsTable, ralphMarkup,
                        tokenTable, tokenSpark, tokenSummaryMarkup,
                        feedMarkup, feedSpark, sessionMarkup,
                        teamRoot, userProfile, disableGitHub, interval);

                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            // Update countdown in header each second
                            var refreshAt = DateTime.Now.AddSeconds(interval);
                            while (DateTime.Now < refreshAt && !ct.IsCancellationRequested)
                            {
                                var remaining = (int)(refreshAt - DateTime.Now).TotalSeconds;
                                headerCtrl.SetContent(new List<string>
                                {
                                    $"[yellow bold] Squad Monitor v2[/] [dim]— {DateTime.Now:yyyy-MM-dd HH:mm:ss} — Next refresh: {remaining}s[/]"
                                });
                                await Task.Delay(1000, ct);
                            }
                        }
                        catch (OperationCanceledException) { break; }

                        RefreshAllPanels(headerCtrl, issuesTable, prsTable, ralphMarkup,
                            tokenTable, tokenSpark, tokenSummaryMarkup,
                            feedMarkup, feedSpark, sessionMarkup,
                            teamRoot, userProfile, disableGitHub, interval);
                    }
                })
                .OnKeyPressed((sender, e) =>
                {
                    switch (e.KeyInfo.Key)
                    {
                        case ConsoleKey.Q:
                            _ws?.Shutdown(0);
                            break;
                        case ConsoleKey.R:
                            doRefresh();
                            break;
                        case ConsoleKey.Tab:
                            focusedIdx = (focusedIdx + 1) % scrollPanels.Length;
                            break;
                        case ConsoleKey.UpArrow:
                            scrollPanels[focusedIdx].ScrollVerticalBy(-3);
                            break;
                        case ConsoleKey.DownArrow:
                            scrollPanels[focusedIdx].ScrollVerticalBy(3);
                            break;
                        case ConsoleKey.D1:
                            rightTabs.ActiveTabIndex = 0;
                            break;
                        case ConsoleKey.D2:
                            rightTabs.ActiveTabIndex = 1;
                            break;
                        case ConsoleKey.D3:
                            rightTabs.ActiveTabIndex = 2;
                            break;
                    }
                })
                .Build();

            _ws.AddWindow(window, true);

            await Task.Run(() => _ws.Run());
        }
        catch (Exception ex) when (ex.Message.Contains("console mode") || ex.Message.Contains("console handle"))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("SharpConsoleUI requires a real interactive terminal.");
            Console.Error.WriteLine($"  Technical: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SharpConsoleUI Error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  REFRESH LOGIC
    // ═══════════════════════════════════════════════════════════════════════

    private static void RefreshAllPanels(
        MarkupControl header,
        TableControl issuesTable, TableControl prsTable,
        MarkupControl ralphMarkup,
        TableControl tokenTable, SparklineControl tokenSpark, MarkupControl tokenSummary,
        MarkupControl feedMarkup, SparklineControl feedSpark, MarkupControl sessionMarkup,
        string teamRoot, string userProfile, bool disableGitHub, int interval)
    {
        try
        {
            var now = DateTime.Now;

            header.SetContent(new List<string>
            {
                $"[yellow bold] Squad Monitor v2[/] [dim]— {now:yyyy-MM-dd HH:mm:ss} — Next refresh: {interval}s[/]"
            });

            // GitHub: cache for 60s
            if (_cachedIssues == null || _cachedPRs == null || (now - _cachedGitHubTime) >= GitHubCacheTtl)
            {
                (_cachedIssues, _cachedPRs) = GetGitHubData(teamRoot, disableGitHub);
                _cachedGitHubTime = now;
            }
            UpdateIssuesTable(issuesTable, _cachedIssues);
            UpdatePrsTable(prsTable, _cachedPRs);

            // Ralph heartbeat: always refresh (cheap file read)
            ralphMarkup.SetContent(GetRalphMarkup(userProfile));

            // Token stats: cache for 60s
            if (_cachedTokenData == null || (now - _cachedTokenTime) >= TokenCacheTtl)
            {
                _cachedTokenData = GetTokenData(userProfile);
                _cachedTokenTime = now;
            }
            UpdateTokenTable(tokenTable, tokenSpark, tokenSummary, _cachedTokenData.Value);

            // Feed: cache for 30s
            if (_cachedFeedData == null || (now - _cachedFeedTime) >= FeedCacheTtl)
            {
                _cachedFeedData = GetFeedData(userProfile, 30);
                _cachedFeedTime = now;
            }
            UpdateFeed(feedMarkup, feedSpark, sessionMarkup, _cachedFeedData.Value);
        }
        catch
        {
            // Silently handle refresh errors to keep the dashboard running
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TABLE UPDATE METHODS
    // ═══════════════════════════════════════════════════════════════════════

    private static void UpdateIssuesTable(TableControl table, List<GitHubIssue> issues)
    {
        table.ClearRows();
        if (issues.Count == 0)
        {
            table.AddRow("", "[dim]No open issues with 'squad' label[/]", "", "", "");
            return;
        }
        foreach (var issue in issues)
        {
            table.AddRow(
                $"[cyan]#{Esc(issue.Number)}[/]",
                Esc(issue.Title),
                $"[yellow]{Esc(issue.Author)}[/]",
                issue.Assignees,
                $"[dim]{Esc(issue.Age)}[/]"
            );
        }
    }

    private static void UpdatePrsTable(TableControl table, List<GitHubPR> prs)
    {
        table.ClearRows();
        if (prs.Count == 0)
        {
            table.AddRow("", "[dim]No open pull requests[/]", "", "", "", "");
            return;
        }
        foreach (var pr in prs)
        {
            var (reviewColor, reviewDisplay) = pr.Review switch
            {
                "APPROVED" => ("green", "Approved"),
                "CHANGES_REQUESTED" => ("red", "Changes"),
                "REVIEW_REQUIRED" => ("yellow", "Pending"),
                _ => pr.IsDraft ? ("dim", "Draft") : ("dim", "---")
            };

            table.AddRow(
                $"[cyan]#{Esc(pr.Number)}[/]",
                Esc(pr.Title),
                $"[yellow]{Esc(pr.Author)}[/]",
                $"[dim]{Esc(pr.Branch)}[/]",
                $"[{reviewColor}]{reviewDisplay}[/]",
                $"[dim]{Esc(pr.Age)}[/]"
            );
        }
    }

    private static void UpdateTokenTable(TableControl table, SparklineControl spark, MarkupControl summary,
        (List<TokenModelStats> Models, string Summary) data)
    {
        table.ClearRows();
        if (data.Models.Count == 0)
        {
            table.AddRow("[dim]No usage data[/]", "", "", "", "", "", "");
            summary.SetContent(new List<string> { "[dim]  No token usage data in recent logs[/]" });
            return;
        }

        var callHistory = new List<double>();
        foreach (var m in data.Models.OrderByDescending(x => x.Calls))
        {
            var model = m.Model.Replace("claude-", "").Replace("gpt-", "");
            if (model.Length > 22) model = model[..19] + "...";
            var cacheClr = m.CachePct > 50 ? "green" : m.CachePct > 20 ? "yellow" : "dim";
            var costClr = m.Cost < 5 ? "green" : m.Cost < 20 ? "yellow" : "red";
            var avgLat = m.AvgLatencyMs > 0 ? $"{m.AvgLatencyMs / 1000.0:F1}s" : "---";
            var latClr = m.AvgLatencyMs > 15000 ? "red" : m.AvgLatencyMs > 5000 ? "yellow" : "green";

            table.AddRow(
                $"[cyan]{Esc(model)}[/]",
                $"[white]{m.Calls}[/]",
                $"[blue]{FmtTok(m.Prompt)}[/]",
                $"[green]{FmtTok(m.Completion)}[/]",
                $"[{cacheClr}]{m.CachePct:F0}%[/]",
                $"[{latClr}]{avgLat}[/]",
                $"[{costClr}]${m.Cost:F2}[/]"
            );
            callHistory.Add(m.Calls);
        }

        if (callHistory.Count > 0)
            spark.SetDataPoints(callHistory);

        summary.SetContent(new List<string> { data.Summary });
    }

    private static void UpdateFeed(MarkupControl feedMarkup, SparklineControl feedSpark,
        MarkupControl sessionMarkup, (List<LiveSessionInfo> Sessions, List<FeedEntry> Entries) data)
    {
        // Session tab
        var sessionLines = new List<string>();
        if (data.Sessions.Count > 0)
        {
            var now = DateTime.Now;
            var activeCount = data.Sessions.Count(s => s.IsActive);
            var totalProcesses = data.Sessions.Sum(s => s.ProcessCount);
            var totalMcps = data.Sessions.Sum(s => s.McpCount);
            sessionLines.Add($" [bold]Sessions:[/] {data.Sessions.Count} ({activeCount} active)  " +
                             $"[bold]Agents:[/] {totalProcesses}  [bold]MCPs:[/] {totalMcps}");
            sessionLines.Add("");

            foreach (var session in data.Sessions.OrderByDescending(s => s.LastWrite))
            {
                var indicator = session.IsActive ? "[green]>[/]" : "[dim].[/]";
                var ageStr = FormatAge(session.Age);
                var lastStr = FormatAge(now - session.LastWrite);
                var typeColor = session.Type switch
                {
                    "Ralph" => "cyan",
                    "CLI" => "yellow",
                    "Copilot" => "blue",
                    "Interactive" => "green",
                    "Update" => "magenta",
                    _ => "dim"
                };
                var cwdPart = string.IsNullOrEmpty(session.Cwd) ? "" : $" [dim]({Esc(session.Cwd)})[/]";
                sessionLines.Add($" {indicator} [{typeColor}]{Esc(session.Name)}[/]{cwdPart}  [dim]age:{ageStr}  last:{lastStr}  procs:{session.ProcessCount}[/]");
            }
        }
        else
        {
            sessionLines.Add("[dim]  No active sessions[/]");
        }
        sessionMarkup.SetContent(sessionLines);

        // Feed sparkline: track events per 30-second bucket
        var bucketTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
            DateTime.Now.Hour, DateTime.Now.Minute, DateTime.Now.Second / 30 * 30);
        if (bucketTime != _lastFeedHistoryBucket)
        {
            if (_lastFeedHistoryBucket != DateTime.MinValue)
                _feedActivityHistory.Add(_currentBucketCount);
            _currentBucketCount = data.Entries.Count;
            _lastFeedHistoryBucket = bucketTime;
            while (_feedActivityHistory.Count > 60)
                _feedActivityHistory.RemoveAt(0);
        }
        else
        {
            _currentBucketCount = data.Entries.Count;
        }

        if (_feedActivityHistory.Count > 0)
            feedSpark.SetDataPoints(_feedActivityHistory);

        // Feed markup
        var feedLines = new List<string>();
        if (data.Entries.Count == 0)
        {
            feedLines.Add("[dim]  No recent agent activity[/]");
        }
        else
        {
            var sorted = data.Entries.OrderByDescending(e => e.Time).Take(50).Reverse().ToList();

            // Legend
            var sessionNames = sorted.Select(e => e.Session).Distinct().ToList();
            var palette = new[] { "cyan", "green", "yellow", "magenta", "blue", "red" };
            var sessionColors = new Dictionary<string, string>();
            for (int i = 0; i < sessionNames.Count; i++)
                sessionColors[sessionNames[i]] = palette[i % palette.Length];

            var legendParts = sessionNames.Select(s => $"[{sessionColors[s]}]|[/] {Esc(s)}");
            feedLines.Add($" {string.Join("  ", legendParts)}");
            feedLines.Add(" [dim]" + new string('-', 80) + "[/]");

            foreach (var entry in sorted)
            {
                var clr = sessionColors.GetValueOrDefault(entry.Session, "white");
                var timeStr = entry.Time.ToString("HH:mm:ss");
                var icon = string.IsNullOrEmpty(entry.Icon) ? ">" : entry.Icon;
                var text = entry.Text;
                if (text.Length > 85) text = text[..82] + "...";
                feedLines.Add($" [dim]{timeStr}[/] [{clr}][{Esc(entry.Session)}][/] {icon} {Esc(text)}");
            }

            feedLines.Add("");
            feedLines.Add($" [dim]Showing {sorted.Count} of {data.Entries.Count} entries from {sessionNames.Count} session(s)[/]");
        }
        feedMarkup.SetContent(feedLines);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: GITHUB ISSUES & PRS (structured)
    // ═══════════════════════════════════════════════════════════════════════

    private static (List<GitHubIssue>, List<GitHubPR>) GetGitHubData(string teamRoot, bool disableGitHub)
    {
        var issues = new List<GitHubIssue>();
        var prs = new List<GitHubPR>();

        if (disableGitHub)
            return (issues, prs);

        var repoSlug = GetGitHubRepoSlug(teamRoot);

        // Issues
        var issueOutput = RunCmd("gh", "issue list --label squad --json number,title,author,createdAt,assignees --limit 12", teamRoot);
        if (issueOutput != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(issueOutput);
                foreach (var issue in doc.RootElement.EnumerateArray())
                {
                    var num = issue.TryGetProperty("number", out var n) ? n.GetInt32().ToString() : "?";
                    var title = issue.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var author = issue.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var al) ? al.GetString() ?? "" : "";
                    var age = issue.TryGetProperty("createdAt", out var c) && DateTime.TryParse(c.GetString(), out var created)
                        ? FormatAge(DateTime.Now - created.ToLocalTime()) : "?";

                    var assignees = new List<string>();
                    if (issue.TryGetProperty("assignees", out var asgn))
                        foreach (var a2 in asgn.EnumerateArray())
                            if (a2.TryGetProperty("login", out var login))
                                assignees.Add(login.GetString() ?? "");

                    if (title.Length > 40) title = title[..37] + "...";
                    if (author.Length > 12) author = author[..12];
                    var asgnStr = assignees.Count > 0 ? string.Join(",", assignees.Select(x => x.Length > 12 ? x[..12] : x)) : "[dim]none[/]";

                    issues.Add(new GitHubIssue(num, title, author, asgnStr, age, repoSlug));
                }
            }
            catch { }
        }

        // PRs
        var prOutput = RunCmd("gh", "pr list --json number,title,author,createdAt,headRefName,reviewDecision,isDraft --limit 15", teamRoot);
        if (prOutput != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(prOutput);
                foreach (var pr in doc.RootElement.EnumerateArray())
                {
                    var num = pr.TryGetProperty("number", out var n) ? n.GetInt32().ToString() : "?";
                    var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var author = pr.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var al) ? al.GetString() ?? "" : "";
                    var branch = pr.TryGetProperty("headRefName", out var b) ? b.GetString() ?? "" : "";
                    var review = pr.TryGetProperty("reviewDecision", out var r) ? r.GetString() ?? "" : "";
                    var isDraft = pr.TryGetProperty("isDraft", out var d) && d.GetBoolean();
                    var age = pr.TryGetProperty("createdAt", out var c) && DateTime.TryParse(c.GetString(), out var created)
                        ? FormatAge(DateTime.Now - created.ToLocalTime()) : "?";

                    if (title.Length > 36) title = title[..33] + "...";
                    if (author.Length > 12) author = author[..12];
                    if (branch.Length > 20) branch = branch[..17] + "...";

                    prs.Add(new GitHubPR(num, title, author, branch, review, isDraft, age, repoSlug));
                }
            }
            catch { }
        }

        return (issues, prs);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: RALPH WATCH STATUS (markup)
    // ═══════════════════════════════════════════════════════════════════════

    private static List<string> GetRalphMarkup(string userProfile)
    {
        var lines = new List<string>();
        lines.Add("[cyan bold] Ralph Watch Loop[/]");
        lines.Add("");

        var heartbeatPath = Path.Combine(userProfile, ".squad", "ralph-heartbeat.json");
        if (!File.Exists(heartbeatPath))
        {
            lines.Add("[dim]  No heartbeat -- ralph-watch may not be running[/]");
            lines.Add("");
            return lines;
        }

        try
        {
            var json = File.ReadAllText(heartbeatPath);
            var heartbeatAge = DateTime.Now - File.GetLastWriteTime(heartbeatPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var lastRun = root.TryGetProperty("lastRun", out var lr) ? lr.GetString() : null;
            var round = root.TryGetProperty("round", out var rn) ? rn.ToString() : "?";
            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "unknown" : "unknown";
            var failures = root.TryGetProperty("consecutiveFailures", out var cf) ? cf.GetInt32() : 0;
            var pid = root.TryGetProperty("pid", out var p) ? p.ToString() : "?";

            var statusColor = status == "running" ? "green" : status == "idle" ? "yellow" : "red";
            var failColor = failures == 0 ? "green" : failures < 3 ? "yellow" : "red";

            lines.Add($"  Status: [{statusColor}]{Esc(status)}[/]  |  Round: [white]{Esc(round)}[/]  |  Failures: [{failColor}]{failures}[/]  |  PID: [dim]{Esc(pid)}[/]");

            if (lastRun != null && DateTime.TryParse(lastRun, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var lastRunDt))
            {
                var staleness = FormatAge(DateTime.Now - lastRunDt);
                var staleColor = (DateTime.Now - lastRunDt).TotalMinutes < 10 ? "green" : (DateTime.Now - lastRunDt).TotalMinutes < 30 ? "yellow" : "red";
                lines.Add($"  Last run: [{staleColor}]{Esc(staleness)}[/]");

                if (status == "idle")
                {
                    var nextRound = lastRunDt.AddMinutes(5);
                    var untilNext = nextRound - DateTime.Now;
                    if (untilNext.TotalSeconds > 0)
                    {
                        var countdown = untilNext.TotalMinutes >= 1
                            ? $"{(int)untilNext.TotalMinutes}m {untilNext.Seconds}s"
                            : $"{(int)untilNext.TotalSeconds}s";
                        lines.Add($"  Next round: [cyan]~{nextRound:HH:mm:ss}[/] [dim](in {countdown})[/]");
                    }
                    else
                    {
                        lines.Add($"  Next round: [yellow]overdue[/] [dim](expected {FormatAge(-untilNext)} ago)[/]");
                    }
                }
            }

            // Metrics
            if (root.TryGetProperty("metrics", out var metrics))
            {
                var ic = metrics.TryGetProperty("issuesClosed", out var icv) ? icv.GetInt32() : 0;
                var pm = metrics.TryGetProperty("prsMerged", out var pmv) ? pmv.GetInt32() : 0;
                var aa = metrics.TryGetProperty("agentActions", out var aav) ? aav.GetInt32() : 0;
                if (ic > 0 || pm > 0 || aa > 0)
                    lines.Add($"  Metrics: [cyan]{ic}[/] issues  [cyan]{pm}[/] PRs  [cyan]{aa}[/] actions");
            }

            var hbAge = FormatAge(heartbeatAge);
            var hbColor = heartbeatAge.TotalMinutes < 1 ? "green" : heartbeatAge.TotalMinutes < 6 ? "yellow" : "red";
            lines.Add($"  Heartbeat: [{hbColor}]{hbAge}[/]");
        }
        catch { lines.Add("[red]  Error reading heartbeat[/]"); }

        lines.Add("");

        // Ralph log (recent rounds)
        var logPath = Path.Combine(userProfile, ".squad", "ralph-watch.log");
        if (File.Exists(logPath))
        {
            lines.Add("[cyan bold] Recent Rounds[/]");
            lines.Add("");
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                if (fs.Length > 0)
                {
                    fs.Seek(Math.Max(0, fs.Length - 500), SeekOrigin.Begin);
                    var tail = reader.ReadToEnd();
                    var logLines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5);
                    var re = new Regex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\s*\|\s*Round=(\d+)\s*\|\s*ExitCode=(\d+)\s*\|\s*Duration=([\d.]+)s");
                    foreach (var line in logLines)
                    {
                        var m = re.Match(line.Trim());
                        if (m.Success)
                        {
                            var rd = m.Groups[2].Value;
                            var ec = m.Groups[3].Value;
                            var dur = double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                            var color = ec == "0" ? "green" : "red";
                            var dm = (int)(dur / 60);
                            var ds = (int)(dur % 60);
                            lines.Add($"  [{color}]Round {rd} | {dm}m {ds}s | {(ec == "0" ? "OK" : "FAIL")}[/]");
                        }
                        else if (!string.IsNullOrWhiteSpace(line))
                        {
                            lines.Add($"  [dim]{Esc(line.Trim())}[/]");
                        }
                    }
                }
            }
            catch { lines.Add("[red]  Error reading log[/]"); }
        }

        return lines;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: TOKEN USAGE & MODEL STATS (structured)
    // ═══════════════════════════════════════════════════════════════════════

    private static (List<TokenModelStats> Models, string Summary) GetTokenData(string userProfile)
    {
        var models = new List<TokenModelStats>();
        var summaryText = "[dim]  No token usage data[/]";

        try
        {
            var logDir = Path.Combine(userProfile, ".copilot", "logs");
            if (!Directory.Exists(logDir)) return (models, summaryText);

            var logFiles = new DirectoryInfo(logDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime).Take(5).ToList();
            if (logFiles.Count == 0) return (models, summaryText);

            var modelStats = new Dictionary<string, (int Calls, long Prompt, long Completion, long Cached, double Cost, List<long> Durations)>();
            var seenApiIds = new HashSet<string>();
            long totalPrompt = 0, totalCompletion = 0, totalCached = 0;
            double totalCost = 0;
            int premiumRequests = 0;

            foreach (var f in logFiles)
            {
                try
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Contains("\"kind\": \"assistant_usage\"") || line.Contains("cli.model_call:"))
                        {
                            var block = ReadBlock(reader, line);

                            var apiId = ExtStr(block, "\"api_call_id\":\\s*\"([^\"]+)\"")
                                     ?? ExtStr(block, "\"api_id\":\\s*\"([^\"]+)\"");
                            if (apiId != null && !seenApiIds.Add(apiId)) continue;

                            var model = ExtStr(block, "\"model\":\\s*\"([^\"]+)\"") ?? "unknown";
                            if (model.Contains("opus", StringComparison.OrdinalIgnoreCase)) premiumRequests++;

                            var inp = ExtLong(block, "\"input_tokens\":\\s*(\\d+)");
                            if (inp == 0) inp = ExtLong(block, "\"prompt_tokens_count\":\\s*(\\d+)");
                            var outp = ExtLong(block, "\"output_tokens\":\\s*(\\d+)");
                            if (outp == 0) outp = ExtLong(block, "\"completion_tokens_count\":\\s*(\\d+)");
                            var cache = ExtLong(block, "\"cache_read_tokens\":\\s*(\\d+)");
                            if (cache == 0) cache = ExtLong(block, "\"cached_tokens_count\":\\s*(\\d+)");
                            var cost = ExtDbl(block, "\"cost\":\\s*([\\d.]+)");
                            var dur = ExtLong(block, "\"duration\":\\s*(\\d+)");
                            if (dur == 0) dur = ExtLong(block, "\"duration_ms\":\\s*(\\d+)");

                            totalPrompt += inp; totalCompletion += outp; totalCached += cache; totalCost += cost;

                            if (!modelStats.ContainsKey(model))
                                modelStats[model] = (0, 0, 0, 0, 0, new List<long>());
                            var s = modelStats[model];
                            s.Calls++;
                            s.Prompt += inp; s.Completion += outp; s.Cached += cache; s.Cost += cost;
                            if (dur > 0) s.Durations.Add(dur);
                            modelStats[model] = s;
                        }
                    }
                }
                catch { }
            }

            if (modelStats.Count == 0) return (models, summaryText);

            foreach (var kvp in modelStats.OrderByDescending(x => x.Value.Calls))
            {
                var st = kvp.Value;
                var cacheTotal = st.Prompt + st.Cached;
                var cachePct = cacheTotal > 0 ? (double)st.Cached / cacheTotal * 100 : 0;
                var avgLat = st.Durations.Count > 0 ? st.Durations.Average() : 0;

                models.Add(new TokenModelStats(
                    kvp.Key, st.Calls, st.Prompt, st.Completion, st.Cached, cachePct, avgLat, st.Cost));
            }

            var totalCachePct = (totalPrompt + totalCached) > 0 ? (double)totalCached / (totalPrompt + totalCached) * 100 : 0;
            var totalCalls = modelStats.Values.Sum(s => s.Calls);
            summaryText = $" [dim]Total:[/] {totalCalls} calls  |  Prompt: [blue]{FmtTok(totalPrompt)}[/]  |  Compl: [green]{FmtTok(totalCompletion)}[/]  |  Cached: [yellow]{FmtTok(totalCached)}[/] ([cyan]{totalCachePct:F0}%[/])  |  Premium: [magenta]{premiumRequests}[/]  |  Cost: [white]${totalCost:F2}[/]";
        }
        catch { }

        return (models, summaryText);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: LIVE AGENT FEED (structured)
    // ═══════════════════════════════════════════════════════════════════════

    private static (List<LiveSessionInfo> Sessions, List<FeedEntry> Entries) GetFeedData(string userProfile, int sessionWindowMinutes)
    {
        var activeSessions = new List<LiveSessionInfo>();
        var allEntries = new List<FeedEntry>();

        try
        {
            var now = DateTime.Now;
            var rawEntries = new List<(DateTime Time, string Icon, string Text, string Session)>();

            // ── Scan Agency sessions (~/.agency/logs) ──
            var agencyLogDir = Path.Combine(userProfile, ".agency", "logs");
            if (Directory.Exists(agencyLogDir))
            {
                var agencySessions = new DirectoryInfo(agencyLogDir).GetDirectories()
                    .Where(d => (now - d.LastWriteTime).TotalMinutes <= sessionWindowMinutes)
                    .ToList();

                foreach (var sessionDir in agencySessions)
                {
                    var logFiles = sessionDir.GetFiles("*.log").Where(f => f.Length > 0).ToList();
                    if (logFiles.Count == 0) continue;

                    var sessionType = DeriveAgencySessionType(sessionDir);
                    var eventsFile = Path.Combine(sessionDir.FullName, "events.jsonl");
                    var (cwd, resumeId, startTime) = File.Exists(eventsFile)
                        ? ExtractSessionMetadata(eventsFile)
                        : ("", "", (DateTime?)null);

                    var creationTime = startTime ?? ParseSessionCreationTime(sessionDir.Name) ?? sessionDir.CreationTime;
                    var sessionAge = now - creationTime;
                    var lastWrite = logFiles.Max(f => f.LastWriteTime);
                    var isActive = (now - lastWrite).TotalMinutes <= 2;
                    var shortId = DeriveShortSessionName(sessionDir.Name, creationTime, cwd);
                    var sessionName = $"{sessionType}-{shortId}";

                    activeSessions.Add(new LiveSessionInfo
                    {
                        Name = sessionName, Type = sessionType, Cwd = cwd, ResumeId = resumeId,
                        Age = sessionAge, LastWrite = lastWrite, IsActive = isActive,
                        ProcessCount = logFiles.Count(f => f.Name.StartsWith("process-")) +
                                       logFiles.Count(f => f.Name.StartsWith("agency_copilot_")),
                        McpCount = logFiles.Count(f => f.Name.StartsWith("agency_mcp_"))
                    });

                    if (File.Exists(eventsFile) && new FileInfo(eventsFile).Length > 0)
                    {
                        var entries = ParseEventsFile(eventsFile, sessionName);
                        rawEntries.AddRange(entries);
                        if (entries.Count == 0)
                            foreach (var logFile in logFiles.Where(f => f.Name.StartsWith("process-")))
                                rawEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                    }
                    else
                    {
                        foreach (var logFile in logFiles.Where(f => f.Name.StartsWith("process-")))
                            rawEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                    }
                }
            }

            // ── Scan Copilot CLI sessions (~/.copilot/logs) ──
            var copilotLogDir = Path.Combine(userProfile, ".copilot", "logs");
            if (Directory.Exists(copilotLogDir))
            {
                var copilotLogs = new DirectoryInfo(copilotLogDir).GetFiles("process-*.log")
                    .Where(f => f.Length > 0 && (now - f.LastWriteTime).TotalMinutes <= sessionWindowMinutes)
                    .ToList();

                foreach (var logFile in copilotLogs)
                {
                    var pidMatch = Regex.Match(logFile.Name, @"process-\d+-(\d+)\.log$");
                    var pid = pidMatch.Success ? pidMatch.Groups[1].Value : logFile.Name.Replace(".log", "");
                    var sessionName = $"CLI-{pid}";
                    var isActive = (now - logFile.LastWriteTime).TotalMinutes <= 2;

                    activeSessions.Add(new LiveSessionInfo
                    {
                        Name = sessionName, Type = "CLI", Cwd = "",
                        Age = now - logFile.CreationTime, LastWrite = logFile.LastWriteTime,
                        IsActive = isActive, ProcessCount = 1
                    });

                    rawEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                }

                // ── Scan Copilot session subdirectories ──
                var copilotSessionDirs = new DirectoryInfo(copilotLogDir).GetDirectories()
                    .Where(d => (now - d.LastWriteTime).TotalMinutes <= sessionWindowMinutes)
                    .ToList();

                foreach (var sessionDir in copilotSessionDirs)
                {
                    var logFiles = sessionDir.GetFiles("*.log").Where(f => f.Length > 0).ToList();
                    var eventsFile = Path.Combine(sessionDir.FullName, "events.jsonl");
                    if (logFiles.Count == 0 && !File.Exists(eventsFile)) continue;

                    var (cwd, resumeId, startTime) = File.Exists(eventsFile)
                        ? ExtractSessionMetadata(eventsFile)
                        : ("", "", (DateTime?)null);

                    var creationTime = startTime ?? sessionDir.CreationTime;
                    var shortId = DeriveShortSessionName(sessionDir.Name, creationTime, cwd);
                    var sessionName = $"Copilot-{shortId}";
                    var lastWrite = logFiles.Count > 0 ? logFiles.Max(f => f.LastWriteTime) : sessionDir.LastWriteTime;
                    var isActive = (now - lastWrite).TotalMinutes <= 2;

                    activeSessions.Add(new LiveSessionInfo
                    {
                        Name = sessionName, Type = "Copilot", Cwd = cwd, ResumeId = resumeId,
                        Age = now - creationTime, LastWrite = lastWrite, IsActive = isActive,
                        ProcessCount = logFiles.Count(f => f.Name.StartsWith("process-")),
                        McpCount = logFiles.Count(f => f.Name.Contains("mcp"))
                    });

                    if (File.Exists(eventsFile) && new FileInfo(eventsFile).Length > 0)
                    {
                        rawEntries.AddRange(ParseEventsFile(eventsFile, sessionName));
                    }
                    else
                    {
                        foreach (var logFile in logFiles.Where(f => f.Name.StartsWith("process-")))
                            rawEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                    }
                }
            }

            // Assign colors to sessions and convert to FeedEntry records
            var sessionNames = rawEntries.Select(e => e.Session).Distinct().ToList();
            var palette = new[] { "cyan", "green", "yellow", "magenta", "blue", "red" };
            var sessionColors = new Dictionary<string, string>();
            for (int i = 0; i < sessionNames.Count; i++)
                sessionColors[sessionNames[i]] = palette[i % palette.Length];

            allEntries = rawEntries.Select(e => new FeedEntry(
                e.Time, e.Icon, e.Text, e.Session,
                sessionColors.GetValueOrDefault(e.Session, "white")
            )).ToList();
        }
        catch { }

        return (activeSessions, allEntries);
    }

    // ── Live Session Info ────────────────────────────────────────────────
    private class LiveSessionInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Cwd { get; set; } = "";
        public string ResumeId { get; set; } = "";
        public TimeSpan Age { get; set; }
        public DateTime LastWrite { get; set; }
        public bool IsActive { get; set; }
        public int ProcessCount { get; set; }
        public int McpCount { get; set; }
    }

    // ── Session Metadata Extraction ─────────────────────────────────────
    private static (string Cwd, string ResumeId, DateTime? StartTime) ExtractSessionMetadata(string eventsFilePath)
    {
        try
        {
            if (!File.Exists(eventsFilePath)) return ("", "", null);
            var fileInfo = new FileInfo(eventsFilePath);
            var bytesToRead = (int)Math.Min(16384, fileInfo.Length);
            var bytes = new byte[bytesToRead];
            using (var fs = new FileStream(eventsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.ReadExactly(bytes, 0, bytesToRead);
            }
            var content = Encoding.UTF8.GetString(bytes);

            var resumeId = ExtractPattern(content, "\"sessionId\"\\s*:\\s*\"([a-f0-9-]+)\"") ?? "";
            if (resumeId.Length > 8) resumeId = resumeId[..8];

            DateTime? startTime = null;
            var tsStr = ExtractPattern(content, "\"type\"\\s*:\\s*\"session\\.start\"[^}]*\"timestamp\"\\s*:\\s*\"([^\"]+)\"");
            if (tsStr != null && DateTime.TryParse(tsStr, null, DateTimeStyles.RoundtripKind, out var ts))
                startTime = ts.ToLocalTime();

            var cwd = ExtractPattern(content, "\"cwd\"\\s*:\\s*\"([^\"]+)\"")
                   ?? ExtractPattern(content, "\"working_directory\"\\s*:\\s*\"([^\"]+)\"");
            if (cwd != null)
                cwd = cwd.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() ?? "";
            else
                cwd = "";

            return (cwd, resumeId, startTime);
        }
        catch { return ("", "", null); }
    }

    private static string? ExtractPattern(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── Session Type Detection ──────────────────────────────────────────
    private static string DeriveAgencySessionType(DirectoryInfo sessionDir)
    {
        var chatJsonPath = Path.Combine(sessionDir.FullName, "chat.json");
        if (File.Exists(chatJsonPath))
        {
            try
            {
                using var fs = new FileStream(chatJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[Math.Min(8192, fs.Length)];
                var bytesRead = fs.Read(buffer, 0, buffer.Length);
                var sample = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (sample.Contains("ralph", StringComparison.OrdinalIgnoreCase))
                    return "Ralph";
            }
            catch { }
        }

        var mainLog = sessionDir.GetFiles("process-*.log").OrderByDescending(f => f.Length).FirstOrDefault();
        if (mainLog != null)
        {
            try
            {
                using var fs = new FileStream(mainLog.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[Math.Min(8192, fs.Length)];
                var bytesRead = fs.Read(buffer, 0, buffer.Length);
                var sample = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (sample.Contains("ralph", StringComparison.OrdinalIgnoreCase))
                    return "Ralph";
            }
            catch { }
        }

        if (sessionDir.GetFiles("agency_update_*.log").Length > 0 &&
            sessionDir.GetFiles("agency_copilot_*.log").Length == 0)
            return "Update";

        if (sessionDir.GetFiles("agency_copilot_*.log").Length >= 5)
            return "Ralph";

        return "Interactive";
    }

    private static DateTime? ParseSessionCreationTime(string sessionDirName)
    {
        var match = Regex.Match(sessionDirName, @"^session_(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})_\d+$");
        if (!match.Success) return null;
        try
        {
            return new DateTime(
                int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value), int.Parse(match.Groups[6].Value),
                DateTimeKind.Local);
        }
        catch { return null; }
    }

    private static string DeriveShortSessionName(string dirOrFileName, DateTime? creationTime, string cwd = "")
    {
        if (dirOrFileName.StartsWith("copilot-"))
        {
            var id = dirOrFileName.Replace("copilot-", "");
            var shortId = id[..Math.Min(8, id.Length)];
            return creationTime.HasValue ? $"{creationTime.Value:MMM dd HH:mm} ({shortId})" : shortId;
        }
        if (dirOrFileName.StartsWith("session_"))
        {
            var parts = dirOrFileName.Split('_');
            if (parts.Length >= 4)
            {
                var shortId = parts[3][..Math.Min(5, parts[3].Length)];
                if (creationTime.HasValue)
                    return $"{creationTime.Value:MMM dd HH:mm} ({shortId})";
                if (parts.Length >= 3 && parts[1].Length >= 8 && parts[2].Length >= 6)
                {
                    try
                    {
                        var parsedTime = new DateTime(
                            int.Parse(parts[1][..4]), int.Parse(parts[1][4..6]), int.Parse(parts[1][6..8]),
                            int.Parse(parts[2][..2]), int.Parse(parts[2][2..4]), 0);
                        return $"{parsedTime:MMM dd HH:mm} ({shortId})";
                    }
                    catch { }
                }
                return $"{parts[2][..Math.Min(6, parts[2].Length)]}_{shortId}";
            }
        }
        if (creationTime.HasValue)
            return $"{creationTime.Value:MMM dd HH:mm} ({dirOrFileName[..Math.Min(8, dirOrFileName.Length)]})";
        return dirOrFileName[..Math.Min(20, dirOrFileName.Length)];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FEED PARSERS
    // ═══════════════════════════════════════════════════════════════════════

    private static List<(DateTime Time, string Icon, string Text, string Session)> ParseEventsFile(string path, string sessionName)
    {
        var entries = new List<(DateTime, string, string, string)>();
        try
        {
            var fileInfo = new FileInfo(path);
            var tailSize = Math.Min(200000, fileInfo.Length);
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fileInfo.Length > tailSize)
                    fs.Seek(-tailSize, SeekOrigin.End);
                using var reader = new StreamReader(fs);
                tail = reader.ReadToEnd();
            }

            var lines = tail.Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                    var ts = root.TryGetProperty("timestamp", out var tsv) ? tsv.GetString() : null;
                    if (ts == null) continue;
                    if (!DateTime.TryParse(ts, null, DateTimeStyles.RoundtripKind, out var dt)) continue;
                    dt = dt.ToLocalTime();

                    string icon = "", text = "";

                    if (type == "tool.execution_start")
                    {
                        if (!root.TryGetProperty("data", out var data)) continue;
                        var toolName = data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "";
                        if (toolName == "report_intent" || toolName == "stop_powershell" || toolName.Length > 60) continue;

                        var detail = "";
                        if (data.TryGetProperty("arguments", out var args))
                        {
                            if (args.TryGetProperty("description", out var dp)) detail = dp.GetString() ?? "";
                            else if (args.TryGetProperty("intent", out var ip)) detail = ip.GetString() ?? "";
                            else if (args.TryGetProperty("command", out var cp))
                            { detail = (cp.GetString() ?? "").Replace("\n", " "); if (detail.Length > 50) detail = detail[..47] + "..."; }
                            else if (args.TryGetProperty("path", out var pp)) detail = pp.GetString() ?? "";
                            else if (args.TryGetProperty("pattern", out var patp)) detail = patp.GetString() ?? "";
                            else if (args.TryGetProperty("prompt", out var prp))
                            { detail = prp.GetString() ?? ""; if (detail.Length > 50) detail = detail[..47] + "..."; }
                            else if (args.TryGetProperty("query", out var qp)) detail = qp.GetString() ?? "";
                        }

                        icon = GetToolIcon(toolName);
                        text = string.IsNullOrEmpty(detail) ? toolName : $"{toolName} -> {detail}";
                        if (text.Length > 70) text = text[..67] + "...";
                    }
                    else if (type.Contains("tool_call"))
                    {
                        var toolName = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
                        if (toolName == "report_intent" || toolName == "stop_powershell") continue;
                        icon = GetToolIcon(toolName);
                        text = $"Tool: {toolName}";
                    }
                    else if (type == "subagent.started")
                    {
                        if (!root.TryGetProperty("data", out var data)) continue;
                        var agentName = data.TryGetProperty("agentDisplayName", out var dn) ? dn.GetString() ?? "" :
                                        data.TryGetProperty("agentName", out var an) ? an.GetString() ?? "" : "sub-agent";
                        icon = ">";
                        text = $"Spawned {agentName}";
                    }
                    else if (type == "subagent.completed")
                    {
                        icon = "+";
                        text = "Sub-agent completed";
                    }
                    else if (type == "assistant.turn_start")
                    {
                        if (root.TryGetProperty("data", out var data))
                        {
                            var turnId = data.TryGetProperty("turnId", out var tid) ? tid.GetString() ?? "?" : "?";
                            icon = "*";
                            text = $"Turn {turnId} started";
                        }
                        else continue;
                    }
                    else if (type.Contains("turn"))
                    {
                        icon = "*";
                        var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        text = string.IsNullOrEmpty(msg) ? "Turn" : $"Turn: {msg}";
                        if (text.Length > 80) text = text[..77] + "...";
                    }
                    else if (type.Contains("session.start"))
                    {
                        icon = ">";
                        text = "Session started";
                    }
                    else if (type == "session.task_complete")
                    {
                        icon = "=";
                        text = "Task completed";
                    }
                    else if (type.Contains("session.end") || type.Contains("complete"))
                    {
                        icon = "=";
                        text = "Session completed";
                    }
                    else if (type.Contains("agent"))
                    {
                        icon = "@";
                        text = $"Agent: {type}";
                    }
                    else
                    {
                        continue;
                    }

                    entries.Add((dt, icon, text, sessionName));
                }
                catch { }
            }
        }
        catch { }
        return entries;
    }

    private static List<(DateTime Time, string Icon, string Text, string Session)> ParseProcessLog(string path, string sessionName)
    {
        var entries = new List<(DateTime, string, string, string)>();
        try
        {
            var fileInfo = new FileInfo(path);
            var tailSize = Math.Min(100000, fileInfo.Length);
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fileInfo.Length > tailSize)
                    fs.Seek(-tailSize, SeekOrigin.End);
                using var reader = new StreamReader(fs);
                tail = reader.ReadToEnd();
            }

            var lines = tail.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                if (trimmed.Contains("Tool invocation result:"))
                {
                    var resultMatch = Regex.Match(trimmed, @"Tool invocation result:\s*(?:###\s*)?(.+)$");
                    if (!resultMatch.Success) continue;
                    var resultText = resultMatch.Groups[1].Value.Trim();
                    if (resultText.Length > 60) resultText = resultText[..57] + "...";

                    if (TryParseLogTimestamp(trimmed, out var dt))
                        entries.Add((dt.ToLocalTime(), "=", $"Result: {resultText}", sessionName));
                    continue;
                }

                if (trimmed.Contains("\"tool_name\"") && trimmed.Contains("tool_call_executed"))
                {
                    var toolNameMatch = Regex.Match(trimmed, "\"tool_name\":\\s*\"([^\"]+)\"");
                    if (!toolNameMatch.Success) continue;
                    var toolName = toolNameMatch.Groups[1].Value;
                    if (toolName == "report_intent" || toolName == "stop_powershell") continue;

                    if (TryFindTimestampNearLine(lines, i, out var dt2))
                        entries.Add((dt2.ToLocalTime(), GetToolIcon(toolName), toolName, sessionName));
                    continue;
                }

                var tsMatch = Regex.Match(trimmed, @"(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2})");
                if (!tsMatch.Success) continue;
                if (!DateTime.TryParse(tsMatch.Groups[1].Value, null, DateTimeStyles.AssumeLocal, out var entryDt)) continue;

                string icon = "=", text = trimmed;

                if (trimmed.Contains("tool_call") || trimmed.Contains("Tool:"))
                {
                    var toolMatch = Regex.Match(trimmed, @"tool[_\s]*(?:call|name)[:\s]+(\S+)", RegexOptions.IgnoreCase);
                    var toolName = toolMatch.Success ? toolMatch.Groups[1].Value : "unknown";
                    icon = GetToolIcon(toolName);
                    text = $"Tool: {toolName}";
                }
                else if (trimmed.Contains("edit", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("file", StringComparison.OrdinalIgnoreCase))
                {
                    icon = "~";
                }
                else if (trimmed.Contains("agent", StringComparison.OrdinalIgnoreCase))
                {
                    icon = "@";
                }
                else if (trimmed.Contains("complete", StringComparison.OrdinalIgnoreCase))
                {
                    icon = "=";
                }
                else
                {
                    continue;
                }

                if (text.Length > 80) text = text[..77] + "...";
                entries.Add((entryDt, icon, text, sessionName));
            }
        }
        catch { }
        return entries;
    }

    private static bool TryParseLogTimestamp(string line, out DateTime result)
    {
        result = DateTime.MinValue;
        var match = Regex.Match(line, @"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z)");
        return match.Success && DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result);
    }

    private static bool TryFindTimestampNearLine(string[] lines, int lineIndex, out DateTime result)
    {
        result = DateTime.MinValue;
        for (int k = lineIndex; k >= Math.Max(0, lineIndex - 30); k--)
        {
            if (TryParseLogTimestamp(lines[k].Trim(), out result))
                return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static string? RunCmd(string fileName, string arguments, string? workingDirectory = null, int timeoutMs = 10_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    // ─── GitHub Clickable Hyperlinks (OSC 8) ─────────────────────────────────

    private static string? _cachedRepoSlug;
    private static bool _repoSlugFetched;

    private static string? GetGitHubRepoSlug(string? teamRoot)
    {
        if (_repoSlugFetched) return _cachedRepoSlug;
        _repoSlugFetched = true;
        _cachedRepoSlug = RunCmd("gh", "repo view --json nameWithOwner -q .nameWithOwner", teamRoot)?.Trim();
        return _cachedRepoSlug;
    }

    private static string Hyperlink(string url, string text) =>
        $"\x1b]8;;{url}\x1b\\{text}\x1b]8;;\x1b\\";

    private static string FormatLinkedIssueNumber(string number, string color, string? repoSlug)
    {
        var display = $"#{Esc(number)}";
        if (!string.IsNullOrEmpty(repoSlug))
        {
            var url = $"https://github.com/{repoSlug}/issues/{number}";
            return $"[{color}]{Hyperlink(url, display)}[/]";
        }
        return $"[{color}]{display}[/]";
    }

    private static string FormatLinkedPrNumber(string number, string color, string? repoSlug)
    {
        var display = $"#{Esc(number)}";
        if (!string.IsNullOrEmpty(repoSlug))
        {
            var url = $"https://github.com/{repoSlug}/pull/{number}";
            return $"[{color}]{Hyperlink(url, display)}[/]";
        }
        return $"[{color}]{display}[/]";
    }

    private static bool IsGhCliAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays}d ago";
        return $"{(int)(age.TotalDays / 7)}w ago";
    }

    private static string FmtTok(long count) =>
        count >= 1_000_000 ? $"{count / 1_000_000.0:F1}M" :
        count >= 1_000 ? $"{count / 1_000.0:F1}K" :
        count.ToString();

    private static string Esc(string s) => Markup.Escape(s);

    private static string GetToolIcon(string toolName) => toolName.ToLowerInvariant() switch
    {
        "powershell" or "bash" or "shell" => ">",
        "edit" => "~",
        "view" or "read" => "?",
        "create" => "+",
        "grep" or "glob" or "search" or "find" => "/",
        "task" => "@",
        "sql" => "#",
        "web_fetch" => "W",
        "git" => "G",
        _ => "."
    };

    private static string ReadBlock(StreamReader reader, string firstLine)
    {
        var sb = new StringBuilder(firstLine);
        for (int i = 0; i < 80; i++)
        {
            var next = reader.ReadLine();
            if (next == null) break;
            sb.AppendLine(next);
            if (next.Length > 0 && next[0] == '}') break;
        }
        return sb.ToString();
    }

    private static long ExtLong(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static double ExtDbl(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string? ExtStr(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
