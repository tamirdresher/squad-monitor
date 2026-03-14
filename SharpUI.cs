using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Spectre.Console;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquadMonitor;

/// <summary>
/// SharpConsoleUI-based multi-panel TUI dashboard for Squad Monitor.
///
/// Layout:
///   ┌─────────────────────────────────────────────────────────────┐
///   │  Squad Monitor v2 — TUI Dashboard          ⟳ Refreshing    │
///   ├──────────────────────────────┬────────────────────────────── │
///   │  GitHub Issues & PRs         │  Ralph Watch                 │
///   │  (scrollable)                │  ────────────────────        │
///   │                              │  Token Usage & Models        │
///   ├──────────────────────────────┴──────────────────────────────┤
///   │  Live Agent Feed (scrollable, auto-scroll)                 │
///   ├────────────────────────────────────────────────────────────-┤
///   │  q=Quit  ↑↓=Scroll  Tab=Switch Panel  r=Refresh           │
///   └────────────────────────────────────────────────────────────-┘
/// </summary>
public static class SharpUI
{
    private static ConsoleWindowSystem? _ws;

    // ── Caching infrastructure to reduce CPU/IO/network pressure ──────
    private static List<string>? _cachedGitHubLines;
    private static DateTime _cachedGitHubTime = DateTime.MinValue;
    private static readonly TimeSpan GitHubCacheTtl = TimeSpan.FromSeconds(60);

    private static List<string>? _cachedFeedLines;
    private static DateTime _cachedFeedTime = DateTime.MinValue;
    private static readonly TimeSpan FeedCacheTtl = TimeSpan.FromSeconds(30);

    private static List<string>? _cachedTokenLines;
    private static DateTime _cachedTokenTime = DateTime.MinValue;
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromSeconds(60);

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
            // ── Create window system first (needed by WindowBuilder) ─────

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

            // ── Build controls with initial data ───────────────────────────

            var headerCtrl = Controls.Markup($"[yellow bold] Squad Monitor v2 — TUI Dashboard [/] [dim]— {DateTime.Now:yyyy-MM-dd HH:mm:ss}[/]")
                .StickyTop()
                .WithName("header")
                .Build();

            var issuesCtrl = Controls.Markup("[dim]  Loading GitHub data...[/]")
                .WithName("issues")
                .Build();

            var ralphCtrl = Controls.Markup("[dim]  Loading Ralph status...[/]")
                .WithName("ralph")
                .Build();

            var tokenCtrl = Controls.Markup("[dim]  Loading token stats...[/]")
                .WithName("tokens")
                .Build();

            var feedCtrl = Controls.Markup("[dim]  Loading live agent feed...[/]")
                .WithName("feed")
                .Build();

            var statusCtrl = Controls.Markup(" [grey]q[/] Quit   [grey]Tab[/] Switch Panel   [grey]r[/] Force Refresh   [grey]↑↓[/] Scroll   [yellow bold]▶ Issues/PRs[/]")
                .StickyBottom()
                .WithName("statusbar")
                .Build();

            // ── Layout: Scrollable panels with explicit refs for keyboard nav ──

            var leftScrollPanel = Controls.ScrollablePanel()
                .AddControl(issuesCtrl)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("leftscroll")
                .Build();

            var rightScrollPanel = Controls.ScrollablePanel()
                .AddControl(ralphCtrl)
                .AddControl(tokenCtrl)
                .WithAutoScroll(false)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("rightscroll")
                .Build();

            var feedPanel = Controls.ScrollablePanel()
                .AddControl(feedCtrl)
                .WithAutoScroll(true)
                .WithMouseWheel(true)
                .WithScrollbar(true)
                .WithName("feedpanel")
                .Build();

            // Panel focus tracking for Tab key navigation
            var scrollPanels = new[] { leftScrollPanel, rightScrollPanel, feedPanel };
            var panelNames = new[] { "Issues/PRs", "Ralph/Tokens", "Agent Feed" };
            int focusedIdx = 0;

            var grid = Controls.HorizontalGrid()
                .Column(leftCol =>
                {
                    leftCol.Flex(6);
                    leftCol.Add(leftScrollPanel);
                })
                .Column(rightCol =>
                {
                    rightCol.Flex(4);
                    rightCol.Add(rightScrollPanel);
                })
                .WithSplitterAfter(0)
                .Build();

            // ── Build the main window ─────────────────────────────────────

            var window = new WindowBuilder(_ws)
                .WithTitle(" Squad Monitor v2 ")
                .Maximized()
                .Resizable(false)
                .Movable(false)
                .HideTitleButtons()
                .WithActiveBorderColor(Color.Cyan1)
                .WithInactiveBorderColor(Color.Grey)
                .AddControl(headerCtrl)
                .AddControl(grid)
                .AddControl(feedPanel)
                .AddControl(statusCtrl)
                .WithAsyncWindowThread(async (win, ct) =>
                {
                    // Initial data load
                    RefreshAllPanels(headerCtrl, issuesCtrl, ralphCtrl, tokenCtrl, feedCtrl,
                        teamRoot, userProfile, disableGitHub);

                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(interval * 1000, ct);
                        }
                        catch (OperationCanceledException) { break; }

                        RefreshAllPanels(headerCtrl, issuesCtrl, ralphCtrl, tokenCtrl, feedCtrl,
                            teamRoot, userProfile, disableGitHub);
                    }
                })
                .OnKeyPressed((sender, e) =>
                {
                    if (e.KeyInfo.Key == ConsoleKey.Q)
                        _ws?.Shutdown(0);
                    else if (e.KeyInfo.Key == ConsoleKey.R)
                    {
                        // Force-refresh: invalidate all caches
                        _cachedGitHubLines = null;
                        _cachedFeedLines = null;
                        _cachedTokenLines = null;
                        RefreshAllPanels(headerCtrl, issuesCtrl, ralphCtrl, tokenCtrl, feedCtrl,
                            teamRoot, userProfile, disableGitHub);
                    }
                    else if (e.KeyInfo.Key == ConsoleKey.Tab)
                    {
                        focusedIdx = (focusedIdx + 1) % scrollPanels.Length;
                        statusCtrl.SetContent(new List<string>
                        {
                            $" [grey]q[/] Quit   [grey]Tab[/] Switch Panel   [grey]r[/] Force Refresh   [grey]↑↓[/] Scroll   [yellow bold]▶ {panelNames[focusedIdx]}[/]"
                        });
                    }
                    else if (e.KeyInfo.Key == ConsoleKey.UpArrow)
                    {
                        scrollPanels[focusedIdx].ScrollVerticalBy(-3);
                    }
                    else if (e.KeyInfo.Key == ConsoleKey.DownArrow)
                    {
                        scrollPanels[focusedIdx].ScrollVerticalBy(3);
                    }
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
        MarkupControl header, MarkupControl issues,
        MarkupControl ralph, MarkupControl tokens, MarkupControl feed,
        string teamRoot, string userProfile, bool disableGitHub)
    {
        try
        {
            var now = DateTime.Now;

            header.SetContent(new List<string>
            {
                $"[yellow bold] Squad Monitor v2 — TUI Dashboard [/]  [dim]— {now:yyyy-MM-dd HH:mm:ss} — ⟳ {now:HH:mm:ss}[/]"
            });

            // GitHub: cache for 60s (spawns gh CLI processes + network)
            if (_cachedGitHubLines == null || (now - _cachedGitHubTime) >= GitHubCacheTtl)
            {
                _cachedGitHubLines = GetGitHubLines(teamRoot, disableGitHub);
                _cachedGitHubTime = now;
            }
            issues.SetContent(_cachedGitHubLines);

            // Ralph heartbeat: always refresh (cheap file read)
            ralph.SetContent(GetRalphLines(userProfile));

            // Token stats: cache for 60s (parses multiple log files)
            if (_cachedTokenLines == null || (now - _cachedTokenTime) >= TokenCacheTtl)
            {
                _cachedTokenLines = GetTokenLines(userProfile);
                _cachedTokenTime = now;
            }
            tokens.SetContent(_cachedTokenLines);

            // Feed: cache for 30s (recursive directory scan + log parsing)
            if (_cachedFeedLines == null || (now - _cachedFeedTime) >= FeedCacheTtl)
            {
                _cachedFeedLines = GetFeedLines(userProfile, 30);
                _cachedFeedTime = now;
            }
            feed.SetContent(_cachedFeedLines);
        }
        catch
        {
            // Silently handle refresh errors to keep the dashboard running
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA: GITHUB ISSUES & PRS
    // ═══════════════════════════════════════════════════════════════════════

    private static List<string> GetGitHubLines(string teamRoot, bool disableGitHub)
    {
        var lines = new List<string>();

        if (disableGitHub)
        {
            lines.Add("[dim] ── GitHub ── (disabled — gh CLI not available)[/]");
            return lines;
        }

        // Issues
        lines.Add("[magenta bold] ── GitHub Issues (squad) ──[/]");
        lines.Add("");

        var repoSlug = GetGitHubRepoSlug(teamRoot);

        var issueOutput = RunCmd("gh", "issue list --label squad --json number,title,author,createdAt,assignees --limit 12", teamRoot);
        if (issueOutput == null)
        {
            lines.Add("[dim]  Could not fetch issues[/]");
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(issueOutput);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0)
                {
                    lines.Add("[dim]  No open issues with 'squad' label[/]");
                }
                else
                {
                    lines.Add($" [dim]{"#",-6} {"Title",-42} {"Author",-14} {"Assignees",-14} {"Age",-8}[/]");
                    lines.Add(" [dim]" + new string('─', 88) + "[/]");

                    foreach (var issue in arr.EnumerateArray())
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

                        var numDisplay = FormatLinkedIssueNumber(num, "cyan", repoSlug);
                        lines.Add($" {numDisplay,-6} {Esc(title),-42} [yellow]{Esc(author),-14}[/] {asgnStr,-14} [dim]{Esc(age),-8}[/]");
                    }
                }
            }
            catch { lines.Add("[red]  Error parsing issues[/]"); }
        }

        lines.Add("");

        // PRs
        lines.Add("[magenta bold] ── Pull Requests (Open) ──[/]");
        lines.Add("");

        var prOutput = RunCmd("gh", "pr list --json number,title,author,createdAt,headRefName,reviewDecision,isDraft --limit 15", teamRoot);
        if (prOutput == null)
        {
            lines.Add("[dim]  Could not fetch PRs[/]");
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(prOutput);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0)
                {
                    lines.Add("[dim]  No open pull requests[/]");
                }
                else
                {
                    lines.Add($" [dim]{"#",-6} {"Title",-38} {"Author",-14} {"Branch",-22} {"Review",-10} {"Age",-8}[/]");
                    lines.Add(" [dim]" + new string('─', 100) + "[/]");

                    foreach (var pr in arr.EnumerateArray())
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

                        var draftTag = isDraft ? "[dim]DRAFT[/]" : "";
                        var reviewColor = review switch
                        {
                            "APPROVED" => "green",
                            "CHANGES_REQUESTED" => "red",
                            "REVIEW_REQUIRED" => "yellow",
                            _ => "dim"
                        };
                        var reviewDisplay = review switch
                        {
                            "APPROVED" => "✅ Approved",
                            "CHANGES_REQUESTED" => "❌ Changes",
                            "REVIEW_REQUIRED" => "⏳ Pending",
                            _ => isDraft ? "📝 Draft" : "—"
                        };

                        var numDisplay = FormatLinkedPrNumber(num, "cyan", repoSlug);
                        lines.Add($" {numDisplay,-6} {Esc(title),-38} [yellow]{Esc(author),-14}[/] [dim]{Esc(branch),-22}[/] [{reviewColor}]{reviewDisplay,-10}[/] [dim]{Esc(age),-8}[/]");
                    }
                }
            }
            catch { lines.Add("[red]  Error parsing PRs[/]"); }
        }

        return lines;
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
            return $"[link=https://github.com/{repoSlug}/issues/{number}][{color}]{display}[/][/]";
        return $"[{color}]{display}[/]";
    }

    private static string FormatLinkedPrNumber(string number, string color, string? repoSlug)
    {
        var display = $"#{Esc(number)}";
        if (!string.IsNullOrEmpty(repoSlug))
            return $"[link=https://github.com/{repoSlug}/pull/{number}][{color}]{display}[/][/]";
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
