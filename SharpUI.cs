using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Rendering;
using Spectre.Console;

// Aliases to resolve ambiguity between Spectre.Console and SharpConsoleUI
using SColor = SharpConsoleUI.Color;
using SHAlign = SharpConsoleUI.Layout.HorizontalAlignment;
using SVAlign = SharpConsoleUI.Layout.VerticalAlignment;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquadMonitor;

// ─── Structured Data Records ──────────────────────────────────────────────────

/// <summary>GitHub issue with structured data for TableControl.</summary>
record GitHubIssue(int Number, string Title, string Author, string Assignees, DateTime CreatedAt, string? RepoSlug);

/// <summary>GitHub PR with structured data for TableControl.</summary>
record GitHubPR(int Number, string Title, string Author, string Branch, string ReviewDecision, bool IsDraft, DateTime CreatedAt, string? RepoSlug);

/// <summary>Token usage stats per model.</summary>
record TokenModelStats(string Model, int Calls, long PromptTokens, long CompletionTokens, long CachedTokens, double Cost, List<long> Durations);

// ─── SharpConsoleUI Dashboard ─────────────────────────────────────────────────

/// <summary>
/// Polished SharpConsoleUI-based multi-panel TUI dashboard for Squad Monitor.
///
/// Layout (--sharp-ui / --beta flag):
///   ┌──────────────────────────────────────────────────────────────┐
///   │  Squad Monitor v2 — TUI Dashboard           ⟳ HH:MM:SS     │
///   ├─────────────────────────────────┬────────────────────────────┤
///   │  GitHub Issues  (TableControl)  │  ┌1 Ralph┬2 Tokens┬3 Sessions┐
///   │  /=filter  ↑↓=sort             │  │  (TabControl)            │
///   │  GitHub PRs     (TableControl)  │  │  Ralph heartbeat / token │
///   │                                 │  │  stats / sessions feed   │
///   ├──── HorizontalSplitter ─────────┴──┴──────────────────────────┤
///   │  ▂▄▆█▂ Agent Activity (SparklineControl)                     │
///   │  Live agent feed entries                                      │
///   ├──────────────────────────────────────────────────────────────┤
///   │  q Quit  / Filter  r Refresh  Tab Panel   1 Ralph  2 Tokens │
///   └──────────────────────────────────────────────────────────────┘
/// </summary>
public static class SharpUI
{
    private static ConsoleWindowSystem? _ws;

    // ── Caching: structured GitHub data ───────────────────────────────────────
    private static List<GitHubIssue>? _cachedIssues;
    private static List<GitHubPR>? _cachedPRs;
    private static DateTime _cachedGitHubTime = DateTime.MinValue;
    private static readonly TimeSpan GitHubCacheTtl = TimeSpan.FromSeconds(60);

    // ── Caching: feed / Ralph / tokens ────────────────────────────────────────
    private static List<string>? _cachedFeedLines;
    private static DateTime _cachedFeedTime = DateTime.MinValue;
    private static readonly TimeSpan FeedCacheTtl = TimeSpan.FromSeconds(30);

    private static List<string>? _cachedTokenLines;
    private static DateTime _cachedTokenTime = DateTime.MinValue;
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromSeconds(60);

    // ── Sparkline activity history (60 samples × 10 s = 10 minutes) ──────────
    private static readonly Queue<double> _activityBuckets = new(60);
    private static int _lastFeedLineCount = 0;

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
            // ── Window system ─────────────────────────────────────────────────
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

            // ── Header (sticky top) ──────────────────────────────────────────
            var headerCtrl = Controls.Markup($"[yellow bold] Squad Monitor v2 — TUI Dashboard [/]  [dim]— {DateTime.Now:yyyy-MM-dd HH:mm:ss}[/]")
                .StickyTop()
                .WithName("header")
                .Build();

            // ── Issues TableControl ─────────────────────────────────────────
            var issuesTable = Controls.Table()
                .WithTitle(" GitHub Issues (squad) ", TextJustification.Left)
                .AddColumn("#", TextJustification.Right, 6)
                .AddColumn("Title", TextJustification.Left, 40)
                .AddColumn("Author", TextJustification.Left, 14)
                .AddColumn("Assignees", TextJustification.Left, 14)
                .AddColumn("Age", TextJustification.Right, 8)
                .Interactive()
                .WithSorting()
                .WithFiltering()
                .WithFuzzyFilter()
                .WithHeaderColors(SColor.White, SColor.Navy)
                .Rounded()
                .WithHorizontalAlignment(SHAlign.Stretch)
                .WithName("issues-table")
                .Build();

            // ── PRs TableControl ────────────────────────────────────────────
            var prsTable = Controls.Table()
                .WithTitle(" Pull Requests (Open) ", TextJustification.Left)
                .AddColumn("#", TextJustification.Right, 6)
                .AddColumn("Title", TextJustification.Left, 36)
                .AddColumn("Author", TextJustification.Left, 14)
                .AddColumn("Branch", TextJustification.Left, 20)
                .AddColumn("Review", TextJustification.Left, 10)
                .AddColumn("Age", TextJustification.Right, 8)
                .Interactive()
                .WithSorting()
                .WithFiltering()
                .WithFuzzyFilter()
                .WithHeaderColors(SColor.White, SColor.Navy)
                .Rounded()
                .WithHorizontalAlignment(SHAlign.Stretch)
                .WithName("prs-table")
                .Build();

            // ── Left panel: Issues + PRs in a ScrollablePanel ───────────────
            var leftPanel = Controls.ScrollablePanel()
                .AddControl(issuesTable)
                .AddControl(prsTable)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("left-panel")
                .Build();

            // ── Tab content: Ralph heartbeat ─────────────────────────────────
            var ralphCtrl = Controls.Markup("[dim]  Loading Ralph status...[/]")
                .WithName("ralph")
                .Build();
            var ralphPanel = Controls.ScrollablePanel()
                .AddControl(ralphCtrl)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .Build();

            // ── Tab content: Token usage stats ──────────────────────────────
            var tokenCtrl = Controls.Markup("[dim]  Loading token stats...[/]")
                .WithName("tokens")
                .Build();
            var tokenPanel = Controls.ScrollablePanel()
                .AddControl(tokenCtrl)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .Build();

            // ── Tab content: Sessions feed ───────────────────────────────────
            var sessionsCtrl = Controls.Markup("[dim]  Loading sessions...[/]")
                .WithName("sessions")
                .Build();
            var sessionsPanel = Controls.ScrollablePanel()
                .AddControl(sessionsCtrl)
                .WithAutoScroll(true)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .Build();

            // ── TabControl (right panel) ─────────────────────────────────────
            var tabControl = Controls.TabControl()
                .AddTab("1 Ralph", ralphPanel)
                .AddTab("2 Tokens", tokenPanel)
                .AddTab("3 Sessions", sessionsPanel)
                .WithVerticalAlignment(SVAlign.Fill)
                .WithName("tabs")
                .Build();

            // ── Main HorizontalGrid (left=issues+PRs | right=tabs) ──────────
            var mainGrid = Controls.HorizontalGrid()
                .Column(leftCol =>
                {
                    leftCol.Flex(6);
                    leftCol.Add(leftPanel);
                })
                .Column(rightCol =>
                {
                    rightCol.Flex(4);
                    rightCol.Add(tabControl);
                })
                .WithSplitterAfter(0)
                .Build();

            // ── SparklineControl: agent activity (green→cyan gradient) ───────
            var sparkline = Controls.Sparkline()
                .WithTitle(" Agent Activity ")
                .WithBorder(BorderStyle.Single, SColor.Cyan1)
                .WithBarColor(SColor.Green)
                .WithGradient(ColorGradient.FromColors(new[] { SColor.Green, SColor.Cyan1 }))
                .WithHeight(5)
                .WithAutoFitDataPoints(true)
                .WithName("sparkline")
                .Build();

            // ── Feed markup (live agent events) ─────────────────────────────
            var feedCtrl = Controls.Markup("[dim]  Loading live agent feed...[/]")
                .WithName("feed")
                .Build();
            var feedScrollPanel = Controls.ScrollablePanel()
                .AddControl(feedCtrl)
                .WithAutoScroll(true)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("feed-panel")
                .Build();

            // ── Feed container: sparkline on top, feed entries below ─────────
            var feedContainer = Controls.ScrollablePanel()
                .AddControl(sparkline)
                .AddControl(feedCtrl)
                .WithAutoScroll(true)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("feed-container")
                .Build();

            // ── HorizontalSplitter: main grid ↕ feed area ───────────────────
            var splitter = Controls.HorizontalSplitter()
                .WithControls(mainGrid, feedContainer)
                .WithMinHeights(15, 7)
                .WithFocusedColors(SColor.SteelBlue, SColor.Grey23)
                .WithDraggingColors(SColor.Yellow, SColor.Grey23)
                .WithName("main-splitter")
                .Build();

            // ── StatusBarControl ─────────────────────────────────────────────
            var statusBar = Controls.StatusBar()
                .AddLeft("q", "Quit", () => _ws?.Shutdown(0))
                .AddLeft("/", "Filter")
                .AddLeft("r", "Refresh")
                .AddLeft("Tab", "Next panel")
                .AddLeftSeparator()
                .AddRight("3", "Sessions", () => tabControl.SwitchToTab("3 Sessions"))
                .AddRight("2", "Tokens", () => tabControl.SwitchToTab("2 Tokens"))
                .AddRight("1", "Ralph", () => tabControl.SwitchToTab("1 Ralph"))
                .WithAboveLine()
                .WithBackgroundColor(SColor.Grey11)
                .WithShortcutForegroundColor(SColor.Cyan1)
                .StickyBottom()
                .WithName("statusbar")
                .Build();

            // ── Window with gradient background and steel-blue border ─────────
            var bgGradient = ColorGradient.FromColors(new[] { SColor.Navy, SColor.Black });

            var window = new WindowBuilder(_ws)
                .WithTitle(" Squad Monitor v2 ")
                .Maximized()
                .Resizable(false)
                .Movable(false)
                .HideTitleButtons()
                .WithBackgroundGradient(bgGradient, GradientDirection.Vertical)
                .WithBorderColor(SColor.SteelBlue)
                .WithActiveBorderColor(SColor.SteelBlue)
                .WithInactiveBorderColor(SColor.Grey)
                .AddControl(headerCtrl)
                .AddControl(splitter)
                .AddControl(statusBar)
                .WithAsyncWindowThread(async (win, ct) =>
                {
                    // Initial data load
                    RefreshAllPanels(headerCtrl, issuesTable, prsTable, ralphCtrl, tokenCtrl,
                        sessionsCtrl, feedCtrl, sparkline, teamRoot, userProfile, disableGitHub);

                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(interval * 1000, ct); }
                        catch (OperationCanceledException) { break; }

                        RefreshAllPanels(headerCtrl, issuesTable, prsTable, ralphCtrl, tokenCtrl,
                            sessionsCtrl, feedCtrl, sparkline, teamRoot, userProfile, disableGitHub);
                    }
                })
                .OnKeyPressed((sender, e) =>
                {
                    var key = e.KeyInfo.Key;
                    var ch = e.KeyInfo.KeyChar;

                    if (key == ConsoleKey.Q)
                        _ws?.Shutdown(0);
                    else if (key == ConsoleKey.R)
                    {
                        // Force-refresh: invalidate all caches
                        _cachedIssues = null;
                        _cachedPRs = null;
                        _cachedFeedLines = null;
                        _cachedTokenLines = null;
                        RefreshAllPanels(headerCtrl, issuesTable, prsTable, ralphCtrl, tokenCtrl,
                            sessionsCtrl, feedCtrl, sparkline, teamRoot, userProfile, disableGitHub);
                    }
                    else if (ch == '1' || key == ConsoleKey.D1)
                        tabControl.SwitchToTab("1 Ralph");
                    else if (ch == '2' || key == ConsoleKey.D2)
                        tabControl.SwitchToTab("2 Tokens");
                    else if (ch == '3' || key == ConsoleKey.D3)
                        tabControl.SwitchToTab("3 Sessions");
                })
                .Build();

            _ws.AddWindow(window, true);

            await Task.Run(() => _ws.Run());
        }
        catch (Exception ex) when (ex.Message.Contains("console mode") || ex.Message.Contains("console handle"))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.Error.WriteLine("║  SharpConsoleUI requires a real interactive terminal.       ║");
            Console.Error.WriteLine("║  Run this from a standard terminal (cmd, PowerShell, etc.)  ║");
            Console.Error.WriteLine("║  — not from a piped/redirected environment.                 ║");
            Console.Error.WriteLine("╚══════════════════════════════════════════════════════════════╝");
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
        TableControl issuesTable,
        TableControl prsTable,
        MarkupControl ralphCtrl,
        MarkupControl tokenCtrl,
        MarkupControl sessionsCtrl,
        MarkupControl feedCtrl,
        SparklineControl sparkline,
        string teamRoot, string userProfile, bool disableGitHub)
    {
        try
        {
            var now = DateTime.Now;

            // ── Header timestamp ──────────────────────────────────────────────
            header.SetContent(new List<string>
            {
                $"[yellow bold] Squad Monitor v2 — TUI Dashboard [/]  [dim]— {now:yyyy-MM-dd HH:mm:ss} — ⟳ {now:HH:mm:ss}[/]"
            });

            // ── GitHub Issues & PRs (cache 60s) ───────────────────────────────
            if (_cachedIssues == null || _cachedPRs == null || (now - _cachedGitHubTime) >= GitHubCacheTtl)
            {
                var repoSlug = GetGitHubRepoSlug(teamRoot);
                _cachedIssues = disableGitHub ? new List<GitHubIssue>() : FetchIssues(teamRoot, repoSlug);
                _cachedPRs = disableGitHub ? new List<GitHubPR>() : FetchPRs(teamRoot, repoSlug);
                _cachedGitHubTime = now;
            }
            PopulateIssuesTable(issuesTable, _cachedIssues, disableGitHub);
            PopulatePRsTable(prsTable, _cachedPRs, disableGitHub);

            // ── Ralph heartbeat (always fresh — cheap file read) ──────────────
            ralphCtrl.SetContent(GetRalphLines(userProfile));

            // ── Token stats (cache 60s) ────────────────────────────────────────
            if (_cachedTokenLines == null || (now - _cachedTokenTime) >= TokenCacheTtl)
            {
                _cachedTokenLines = GetTokenLines(userProfile);
                _cachedTokenTime = now;
            }
            tokenCtrl.SetContent(_cachedTokenLines);

            // ── Sessions feed (cache 30s) ─────────────────────────────────────
            if (_cachedFeedLines == null || (now - _cachedFeedTime) >= FeedCacheTtl)
            {
                _cachedFeedLines = GetFeedLines(userProfile, 30);
                _cachedFeedTime = now;
            }
            sessionsCtrl.SetContent(_cachedFeedLines);
            feedCtrl.SetContent(_cachedFeedLines);

            // ── Sparkline activity update ─────────────────────────────────────
            var currentFeedCount = _cachedFeedLines?.Count ?? 0;
            var delta = Math.Max(0, currentFeedCount - _lastFeedLineCount);
            _lastFeedLineCount = currentFeedCount;
            _activityBuckets.Enqueue(delta);
            if (_activityBuckets.Count > 60) _activityBuckets.Dequeue();
            sparkline.SetDataPoints(_activityBuckets);
        }
        catch
        {
            // Silently handle refresh errors to keep the dashboard running
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: GITHUB ISSUES & PRS (structured records for TableControl)
    // ═══════════════════════════════════════════════════════════════════════

    private static List<GitHubIssue> FetchIssues(string teamRoot, string? repoSlug)
    {
        var result = new List<GitHubIssue>();
        var output = RunCmd("gh", "issue list --label squad --json number,title,author,createdAt,assignees --limit 20", teamRoot);
        if (output == null) return result;
        try
        {
            using var doc = JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var num = el.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
                var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var author = el.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var al) ? al.GetString() ?? "" : "";
                DateTime? createdAt = el.TryGetProperty("createdAt", out var c) && DateTime.TryParse(c.GetString(), out var dt) ? dt.ToLocalTime() : null;

                var assignees = new List<string>();
                if (el.TryGetProperty("assignees", out var asgn))
                    foreach (var a2 in asgn.EnumerateArray())
                        if (a2.TryGetProperty("login", out var login)) assignees.Add(login.GetString() ?? "");

                result.Add(new GitHubIssue(num, title, author, string.Join(", ", assignees), createdAt ?? DateTime.UtcNow, repoSlug));
            }
        }
        catch { }
        return result;
    }

    private static List<GitHubPR> FetchPRs(string teamRoot, string? repoSlug)
    {
        var result = new List<GitHubPR>();
        var output = RunCmd("gh", "pr list --json number,title,author,createdAt,headRefName,reviewDecision,isDraft --limit 20", teamRoot);
        if (output == null) return result;
        try
        {
            using var doc = JsonDocument.Parse(output);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var num = el.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
                var title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var author = el.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var al) ? al.GetString() ?? "" : "";
                var branch = el.TryGetProperty("headRefName", out var b) ? b.GetString() ?? "" : "";
                var review = el.TryGetProperty("reviewDecision", out var r) ? r.GetString() ?? "" : "";
                var isDraft = el.TryGetProperty("isDraft", out var d) && d.GetBoolean();
                DateTime? createdAt = el.TryGetProperty("createdAt", out var c) && DateTime.TryParse(c.GetString(), out var dt) ? dt.ToLocalTime() : null;

                result.Add(new GitHubPR(num, title, author, branch, review, isDraft, createdAt ?? DateTime.UtcNow, repoSlug));
            }
        }
        catch { }
        return result;
    }

    private static void PopulateIssuesTable(TableControl table, List<GitHubIssue> issues, bool disableGitHub)
    {
        table.ClearRows();
        if (disableGitHub)
        {
            table.AddRow(new[] { "—", "(gh CLI not available)", "", "", "" });
            return;
        }
        if (issues.Count == 0)
        {
            table.AddRow(new[] { "—", "No open issues with 'squad' label", "", "", "" });
            return;
        }
        foreach (var issue in issues)
        {
            var title = issue.Title.Length > 40 ? issue.Title[..37] + "..." : issue.Title;
            var author = issue.Author.Length > 12 ? issue.Author[..12] : issue.Author;
            var assignees = issue.Assignees.Length > 14 ? issue.Assignees[..11] + "..." : issue.Assignees;
            var age = FormatAge(DateTime.Now - issue.CreatedAt);
            table.AddRow(new[] { $"#{issue.Number}", title, author, assignees.Length > 0 ? assignees : "—", age });
        }
    }

    private static void PopulatePRsTable(TableControl table, List<GitHubPR> prs, bool disableGitHub)
    {
        table.ClearRows();
        if (disableGitHub)
        {
            table.AddRow(new[] { "—", "(gh CLI not available)", "", "", "", "" });
            return;
        }
        if (prs.Count == 0)
        {
            table.AddRow(new[] { "—", "No open pull requests", "", "", "", "" });
            return;
        }
        foreach (var pr in prs)
        {
            var title = pr.Title.Length > 36 ? pr.Title[..33] + "..." : pr.Title;
            var author = pr.Author.Length > 12 ? pr.Author[..12] : pr.Author;
            var branch = pr.Branch.Length > 18 ? pr.Branch[..15] + "..." : pr.Branch;
            var reviewLabel = pr.ReviewDecision switch
            {
                "APPROVED" => "✅ Approved",
                "CHANGES_REQUESTED" => "❌ Changes",
                "REVIEW_REQUIRED" => "⏳ Pending",
                _ => pr.IsDraft ? "📝 Draft" : "—"
            };
            var age = FormatAge(DateTime.Now - pr.CreatedAt);
            table.AddRow(new[] { $"#{pr.Number}", title, author, branch, reviewLabel, age });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: RALPH WATCH STATUS
    // ═══════════════════════════════════════════════════════════════════════

    private static List<string> GetRalphLines(string userProfile)
    {
        var lines = new List<string>();
        lines.Add("[cyan bold] ── Ralph Watch Loop ──[/]");
        lines.Add("");

        var heartbeatPath = Path.Combine(userProfile, ".squad", "ralph-heartbeat.json");
        if (!File.Exists(heartbeatPath))
        {
            lines.Add("[dim]  No heartbeat — ralph-watch may not be running[/]");
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
            lines.Add("[cyan bold] ── Recent Rounds ──[/]");
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
                            var icon = ec == "0" ? "✅" : "❌";
                            var color = ec == "0" ? "green" : "red";
                            var dm = (int)(dur / 60);
                            var ds = (int)(dur % 60);
                            lines.Add($"  [{color}]Round {rd} | {dm}m {ds}s | {icon}[/]");
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
    //  DATA: TOKEN USAGE & MODEL STATS
    // ═══════════════════════════════════════════════════════════════════════

    private static List<string> GetTokenLines(string userProfile)
    {
        var lines = new List<string>();
        lines.Add("");
        lines.Add("[magenta bold] ── Token Usage & Model Stats ──[/]");
        lines.Add("");

        try
        {
            var logDir = Path.Combine(userProfile, ".copilot", "logs");
            if (!Directory.Exists(logDir)) { lines.Add("[dim]  No copilot logs directory[/]"); return lines; }

            var logFiles = new DirectoryInfo(logDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime).Take(5).ToList();
            if (logFiles.Count == 0) { lines.Add("[dim]  No log files found[/]"); return lines; }

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

            if (modelStats.Count == 0) { lines.Add("[dim]  No usage data in recent logs[/]"); return lines; }

            lines.Add($" [dim]{"Model",-24} {"Calls",6} {"Prompt",10} {"Completion",11} {"Cached",10} {"Cache%",7} {"Latency",8} {"Cost",8}[/]");
            lines.Add(" [dim]" + new string('─', 90) + "[/]");

            foreach (var kvp in modelStats.OrderByDescending(x => x.Value.Calls))
            {
                var model = kvp.Key.Replace("claude-", "").Replace("gpt-", "");
                if (model.Length > 22) model = model[..19] + "...";
                var st = kvp.Value;
                var cacheTotal = st.Prompt + st.Cached;
                var cachePct = cacheTotal > 0 ? (double)st.Cached / cacheTotal * 100 : 0;
                var cacheClr = cachePct > 50 ? "green" : cachePct > 20 ? "yellow" : "dim";
                var costClr = st.Cost < 5 ? "green" : st.Cost < 20 ? "yellow" : "red";
                var avgLat = st.Durations.Count > 0 ? $"{st.Durations.Average() / 1000.0:F1}s" : "—";
                var latClr = st.Durations.Count > 0 && st.Durations.Average() > 15000 ? "red"
                           : st.Durations.Count > 0 && st.Durations.Average() > 5000 ? "yellow" : "green";

                lines.Add($" [cyan]{Esc(model),-24}[/] [white]{st.Calls,6}[/] [blue]{FmtTok(st.Prompt),10}[/] [green]{FmtTok(st.Completion),11}[/] [yellow]{FmtTok(st.Cached),10}[/] [{cacheClr}]{cachePct,6:F0}%[/] [{latClr}]{avgLat,8}[/] [{costClr}]{"$" + st.Cost.ToString("F2"),8}[/]");
            }

            lines.Add("");
            var totalCachePct = (totalPrompt + totalCached) > 0 ? (double)totalCached / (totalPrompt + totalCached) * 100 : 0;
            var totalCalls = modelStats.Values.Sum(s => s.Calls);
            lines.Add($" [dim]Total:[/] {totalCalls} calls  |  Prompt: [blue]{FmtTok(totalPrompt)}[/]  |  Completion: [green]{FmtTok(totalCompletion)}[/]  |  Cached: [yellow]{FmtTok(totalCached)}[/] ([cyan]{totalCachePct:F0}%[/])  |  Premium: [magenta]{premiumRequests}[/]  |  Cost: [white]${totalCost:F2}[/]");
        }
        catch { lines.Add("[red]  Error reading token stats[/]"); }

        return lines;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: LIVE AGENT FEED
    // ═══════════════════════════════════════════════════════════════════════

    private static List<string> GetFeedLines(string userProfile, int sessionWindowMinutes)
    {
        var lines = new List<string>();
        lines.Add("[green bold] ── Live Agent Feed — Multi-Session View ──[/]");
        lines.Add("");

        try
        {
            var now = DateTime.Now;
            var activeSessions = new List<LiveSessionInfo>();
            var allEntries = new List<(DateTime Time, string Icon, string Text, string Session)>();

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

                    // Parse feed entries
                    if (File.Exists(eventsFile) && new FileInfo(eventsFile).Length > 0)
                    {
                        var entries = ParseEventsFile(eventsFile, sessionName);
                        allEntries.AddRange(entries);
                        if (entries.Count == 0)
                        {
                            foreach (var logFile in logFiles.Where(f => f.Name.StartsWith("process-")))
                                allEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                        }
                    }
                    else
                    {
                        foreach (var logFile in logFiles.Where(f => f.Name.StartsWith("process-")))
                            allEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                    }
                }
            }

            // ── Scan Copilot CLI sessions (~/.copilot/logs — flat process-*.log files) ──
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

                    allEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                }

                // ── Scan Copilot session subdirectories (dirs with events.jsonl) ──
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
                        allEntries.AddRange(ParseEventsFile(eventsFile, sessionName));
                    }
                    else
                    {
                        foreach (var logFile in logFiles.Where(f => f.Name.StartsWith("process-")))
                            allEntries.AddRange(ParseProcessLog(logFile.FullName, sessionName));
                    }
                }
            }

            // ── Session Overview ──
            if (activeSessions.Count > 0)
            {
                var activeCount = activeSessions.Count(s => s.IsActive);
                var totalProcesses = activeSessions.Sum(s => s.ProcessCount);
                var totalMcps = activeSessions.Sum(s => s.McpCount);
                lines.Add($" [bold]Sessions:[/] {activeSessions.Count} ({activeCount} active)  " +
                          $"[bold]Agents:[/] {totalProcesses}  [bold]MCPs:[/] {totalMcps}");
                lines.Add("");

                // Session roster with active indicators
                foreach (var session in activeSessions.OrderByDescending(s => s.LastWrite))
                {
                    var indicator = session.IsActive ? "[green]🟢[/]" : "[dim]⬜[/]";
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
                    lines.Add($" {indicator} [{typeColor}]{Esc(session.Name)}[/]{cwdPart}  [dim]age:{ageStr}  last:{lastStr}  procs:{session.ProcessCount}[/]");
                }
                lines.Add("");
            }

            if (allEntries.Count == 0)
            {
                lines.Add($"[dim]  No active agent sessions in the last {sessionWindowMinutes} minutes[/]");
                return lines;
            }

            // ── Activity Feed ──
            var sorted = allEntries.OrderByDescending(e => e.Time).Take(50).Reverse().ToList();

            // Assign colors to sessions
            var sessionNames = sorted.Select(e => e.Session).Distinct().ToList();
            var palette = new[] { "cyan", "green", "yellow", "magenta", "blue", "red" };
            var sessionColors = new Dictionary<string, string>();
            for (int i = 0; i < sessionNames.Count; i++)
                sessionColors[sessionNames[i]] = palette[i % palette.Length];

            // Legend
            var legendParts = sessionNames.Select(s => $"[{sessionColors[s]}]■[/] {Esc(s)}");
            lines.Add($" Sessions: {string.Join("  ", legendParts)}");
            lines.Add(" [dim]" + new string('─', 100) + "[/]");

            foreach (var entry in sorted)
            {
                var clr = sessionColors.GetValueOrDefault(entry.Session, "white");
                var timeStr = entry.Time.ToString("HH:mm:ss");
                var icon = string.IsNullOrEmpty(entry.Icon) ? "🔧" : entry.Icon;
                var text = entry.Text;
                if (text.Length > 85) text = text[..82] + "...";
                lines.Add($" [dim]{timeStr}[/] [{clr}][{Esc(entry.Session)}][/] {icon} {Esc(text)}");
            }

            lines.Add("");
            lines.Add($" [dim]Showing {sorted.Count} of {allEntries.Count} entries from {sessionNames.Count} session(s)  |  Window: {sessionWindowMinutes}m[/]");
        }
        catch (Exception ex)
        {
            lines.Add($"[red]  Error: {Esc(ex.Message)}[/]");
        }

        return lines;
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
                // Fallback: parse from dir name
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
                        text = string.IsNullOrEmpty(detail) ? toolName : $"{toolName} → {detail}";
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
                        icon = "🤖";
                        text = $"Spawned {agentName}";
                    }
                    else if (type == "subagent.completed")
                    {
                        icon = "✅";
                        text = "Sub-agent completed";
                    }
                    else if (type == "assistant.turn_start")
                    {
                        if (root.TryGetProperty("data", out var data))
                        {
                            var turnId = data.TryGetProperty("turnId", out var tid) ? tid.GetString() ?? "?" : "?";
                            icon = "💭";
                            text = $"Turn {turnId} started";
                        }
                        else continue;
                    }
                    else if (type.Contains("turn"))
                    {
                        icon = "💭";
                        var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        text = string.IsNullOrEmpty(msg) ? "Turn" : $"Turn: {msg}";
                        if (text.Length > 80) text = text[..77] + "...";
                    }
                    else if (type.Contains("session.start"))
                    {
                        icon = "🚀";
                        text = "Session started";
                    }
                    else if (type == "session.task_complete")
                    {
                        icon = "🏁";
                        text = "Task completed";
                    }
                    else if (type.Contains("session.end") || type.Contains("complete"))
                    {
                        icon = "🏁";
                        text = "Session completed";
                    }
                    else if (type.Contains("agent"))
                    {
                        icon = "🤖";
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

                // Parse tool invocation results
                if (trimmed.Contains("Tool invocation result:"))
                {
                    var resultMatch = Regex.Match(trimmed, @"Tool invocation result:\s*(?:###\s*)?(.+)$");
                    if (!resultMatch.Success) continue;
                    var resultText = resultMatch.Groups[1].Value.Trim();
                    if (resultText.Length > 60) resultText = resultText[..57] + "...";

                    if (TryParseLogTimestamp(trimmed, out var dt))
                    {
                        entries.Add((dt.ToLocalTime(), "📋", $"Result: {resultText}", sessionName));
                    }
                    continue;
                }

                // Parse telemetry tool_call_executed lines
                if (trimmed.Contains("\"tool_name\"") && trimmed.Contains("tool_call_executed"))
                {
                    var toolNameMatch = Regex.Match(trimmed, "\"tool_name\":\\s*\"([^\"]+)\"");
                    if (!toolNameMatch.Success) continue;
                    var toolName = toolNameMatch.Groups[1].Value;
                    if (toolName == "report_intent" || toolName == "stop_powershell") continue;

                    if (TryFindTimestampNearLine(lines, i, out var dt2))
                    {
                        entries.Add((dt2.ToLocalTime(), GetToolIcon(toolName), toolName, sessionName));
                    }
                    continue;
                }

                // Match timestamp patterns
                var tsMatch = Regex.Match(trimmed, @"(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2})");
                if (!tsMatch.Success) continue;
                if (!DateTime.TryParse(tsMatch.Groups[1].Value, null, DateTimeStyles.AssumeLocal, out var entryDt)) continue;

                string icon = "📋", text = trimmed;

                if (trimmed.Contains("tool_call") || trimmed.Contains("Tool:"))
                {
                    var toolMatch = Regex.Match(trimmed, @"tool[_\s]*(?:call|name)[:\s]+(\S+)", RegexOptions.IgnoreCase);
                    var toolName = toolMatch.Success ? toolMatch.Groups[1].Value : "unknown";
                    icon = GetToolIcon(toolName);
                    text = $"Tool: {toolName}";
                }
                else if (trimmed.Contains("edit", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("file", StringComparison.OrdinalIgnoreCase))
                {
                    icon = "✏️";
                }
                else if (trimmed.Contains("agent", StringComparison.OrdinalIgnoreCase))
                {
                    icon = "🤖";
                }
                else if (trimmed.Contains("complete", StringComparison.OrdinalIgnoreCase))
                {
                    icon = "🏁";
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

    /// <summary>
    /// Wraps display text in an OSC 8 terminal hyperlink escape sequence.
    /// Supported by Windows Terminal, iTerm2, and other modern terminals.
    /// </summary>
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
        "powershell" or "bash" or "shell" => "⚡",
        "edit" => "✏️",
        "view" or "read" => "👁️",
        "create" => "📄",
        "grep" or "glob" or "search" or "find" => "🔍",
        "task" => "🤖",
        "sql" => "🗄️",
        "web_fetch" => "🌐",
        "git" => "📦",
        _ => "🔧"
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
