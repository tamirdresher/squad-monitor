using Spectre.Console;
using Spectre.Console.Rendering;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

var interval = 5;
var runOnce = false;
var orchestrationOnlyMode = false;
var teamRoot = FindTeamRoot();
var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--interval" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
    {
        interval = n;
        i++;
    }
    else if (args[i] == "--once")
    {
        runOnce = true;
    }
}

if (teamRoot == null)
{
    AnsiConsole.MarkupLine("[red]Error: Could not find .squad directory. Run from team root.[/]");
    return 1;
}

AnsiConsole.MarkupLine($"[dim]Squad Monitor v2 - Refresh interval: {interval}s[/]");
AnsiConsole.MarkupLine($"[dim]Team root: {teamRoot}[/]");
AnsiConsole.MarkupLine($"[dim]Press 'o' or 'O' to toggle orchestration-only view[/]");
AnsiConsole.WriteLine();

if (runOnce)
{
    // Run once mode: render directly without Live display
    var now = DateTime.Now;
    var header = new Rule($"[yellow bold]Squad Monitor v2[/] [dim]— {now:yyyy-MM-dd HH:mm:ss}[/]")
    {
        Justification = Justify.Left
    };
    AnsiConsole.Write(header);
    AnsiConsole.WriteLine();

    DisplayRalphHeartbeat(userProfile);
    DisplayRalphLog(userProfile);
    DisplayGitHubIssues(teamRoot);
    DisplayGitHubPRs(teamRoot);
    DisplayRecentlyMergedPRs(teamRoot);
    var activities = LoadActivities(teamRoot);
    DisplayOrchestrationLog(activities);
}
else
{
    // Live mode: use AnsiConsole.Live() for flicker-free updates
    var layout = new Layout("Root");
    
    await AnsiConsole.Live(layout)
        .AutoClear(false)
        .StartAsync(async ctx =>
        {
            do
            {
                // Check for keyboard input to toggle view mode
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.O)
                    {
                        orchestrationOnlyMode = !orchestrationOnlyMode;
                    }
                }

                var now = DateTime.Now;
                var content = orchestrationOnlyMode 
                    ? BuildOrchestrationOnlyContent(now, userProfile, teamRoot)
                    : BuildDashboardContent(now, userProfile, teamRoot);
                layout.Update(content);
                ctx.Refresh();

                var viewMode = orchestrationOnlyMode ? "[yellow]Orchestration View[/]" : "[cyan]Full Dashboard[/]";
                AnsiConsole.MarkupLine($"\n[dim]Mode: {viewMode} | Press 'o' to toggle | Refreshing in {interval}s... (Ctrl+C to exit)[/]");
                await Task.Delay(TimeSpan.FromSeconds(interval));

            } while (true);
        });
}

return 0;

// ─── Dashboard Content Builder ─────────────────────────────────────────────

static IRenderable BuildDashboardContent(DateTime now, string userProfile, string teamRoot)
{
    var sections = new List<IRenderable>();

    // Determine how many issue rows we can show based on terminal height.
    // Reserve ~30 lines for: header(2) + ralph heartbeat(3) + ralph log(8) +
    // PRs section(~12) + merged PRs(~6) + orchestration(~12) + padding.
    // Each issue row takes ~3 lines in the table (content + borders).
    int termHeight = 50; // sensible default
    try { termHeight = Console.WindowHeight; } catch { }
    int reservedLines = 32;
    int maxIssueRows = Math.Max(3, (termHeight - reservedLines) / 3);

    // Header
    var header = new Rule($"[yellow bold]Squad Monitor v2[/] [dim]— {now:yyyy-MM-dd HH:mm:ss}[/]")
    {
        Justification = Justify.Left
    };
    sections.Add(header);
    sections.Add(Text.Empty);

    // Ralph Watch Heartbeat
    sections.Add(BuildRalphHeartbeatSection(userProfile));
    
    // Ralph Watch Log
    sections.Add(BuildRalphLogSection(userProfile));
    
    // GitHub Issues (limited by terminal height)
    sections.Add(BuildGitHubIssuesSection(teamRoot, maxIssueRows));
    
    // GitHub PRs
    sections.Add(BuildGitHubPRsSection(teamRoot));
    
    // Recently Merged PRs
    sections.Add(BuildRecentlyMergedPRsSection(teamRoot));
    
    // Orchestration Log
    var activities = LoadActivities(teamRoot);
    sections.Add(BuildOrchestrationLogSection(activities));

    // Combine all sections into a group
    var rows = new Rows(sections);
    return rows;
}

// ─── Orchestration-Only Dashboard Builder ──────────────────────────────────

static IRenderable BuildOrchestrationOnlyContent(DateTime now, string userProfile, string teamRoot)
{
    var sections = new List<IRenderable>();

    // Header
    var header = new Rule($"[yellow bold]Squad Monitor v2 — Orchestration View[/] [dim]— {now:yyyy-MM-dd HH:mm:ss}[/]")
    {
        Justification = Justify.Left
    };
    sections.Add(header);
    sections.Add(Text.Empty);

    // Load and display orchestration activities in detail
    var activities = LoadActivities(teamRoot);
    sections.Add(BuildDetailedOrchestrationSection(activities, now));

    // Combine all sections into a group
    var rows = new Rows(sections);
    return rows;
}

// ─── Helpers ────────────────────────────────────────────────────────────────

static string? FindTeamRoot()
{
    var current = Directory.GetCurrentDirectory();
    while (current != null)
    {
        if (Directory.Exists(Path.Combine(current, ".squad")))
            return current;
        current = Directory.GetParent(current)?.FullName;
    }
    return null;
}

static string FormatAge(TimeSpan age)
{
    if (age.TotalMinutes < 1) return "just now";
    if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
    if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
    if (age.TotalDays < 7) return $"{(int)age.TotalDays}d ago";
    return $"{(int)(age.TotalDays / 7)}w ago";
}

static string CapitalizeAgent(string agent)
{
    if (string.IsNullOrEmpty(agent)) return agent;
    return char.ToUpper(agent[0]) + agent.Substring(1).ToLower();
}

static string? RunProcess(string fileName, string arguments, string? workingDirectory = null, int timeoutMs = 10_000)
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
        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        return proc.ExitCode == 0 ? output : null;
    }
    catch
    {
        return null;
    }
}

// ─── Section Builders (return IRenderable) ──────────────────────────────────

static IRenderable BuildRalphHeartbeatSection(string userProfile)
{
    var items = new List<IRenderable>();
    
    var section = new Rule("[cyan]Ralph Watch Loop[/]") { Justification = Justify.Left };
    items.Add(section);

    var heartbeatPath = Path.Combine(userProfile, ".squad", "ralph-heartbeat.json");
    if (!File.Exists(heartbeatPath))
    {
        items.Add(new Markup("[dim]  No heartbeat file found — ralph-watch may not be running[/]"));
        items.Add(Text.Empty);
        return new Rows(items);
    }

    try
    {
        var json = File.ReadAllText(heartbeatPath);
        var heartbeatFileAge = DateTime.Now - File.GetLastWriteTime(heartbeatPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var lastRun = root.TryGetProperty("lastRun", out var lr) ? lr.GetString() : null;
        var round = root.TryGetProperty("round", out var rn) ? rn.ToString() : "?";
        var status = root.TryGetProperty("status", out var st) ? st.GetString() : "unknown";
        var consecutiveFailures = root.TryGetProperty("consecutiveFailures", out var cf) ? cf.GetInt32() : 0;
        var pid = root.TryGetProperty("pid", out var p) ? p.ToString() : "?";
        
        // Extract metrics if available
        var metricsText = "";
        if (root.TryGetProperty("metrics", out var metrics))
        {
            var issuesClosed = metrics.TryGetProperty("issuesClosed", out var ic) ? ic.GetInt32() : 0;
            var prsMerged = metrics.TryGetProperty("prsMerged", out var pm) ? pm.GetInt32() : 0;
            var agentActions = metrics.TryGetProperty("agentActions", out var aa) ? aa.GetInt32() : 0;
            
            if (issuesClosed > 0 || prsMerged > 0 || agentActions > 0)
            {
                metricsText = $"  |  Metrics: [cyan]{issuesClosed}[/] issues, [cyan]{prsMerged}[/] PRs, [cyan]{agentActions}[/] actions";
            }
        }

        var staleness = "unknown";
        var stalenessColor = "dim";
        DateTime lastRunDt = DateTime.MinValue;
        if (lastRun != null && DateTime.TryParse(lastRun, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out lastRunDt))
        {
            var age = DateTime.Now - lastRunDt;
            staleness = FormatAge(age);
            stalenessColor = age.TotalMinutes < 10 ? "green" : age.TotalMinutes < 30 ? "yellow" : "red";
        }

        var statusColor = status == "running" ? "green" : status == "idle" ? "yellow" : "red";
        var failColor = consecutiveFailures == 0 ? "green" : consecutiveFailures < 3 ? "yellow" : "red";

        items.Add(new Markup($"  Status: [{statusColor}]{Markup.Escape(status ?? "unknown")}[/]  |  " +
                            $"Round: [white]{Markup.Escape(round)}[/]  |  " +
                            $"Last run: [{stalenessColor}]{Markup.Escape(staleness)}[/]  |  " +
                            $"Failures: [{failColor}]{consecutiveFailures}[/]  |  " +
                            $"PID: [dim]{Markup.Escape(pid)}[/]" +
                            metricsText));

        // Calculate next round time (lastRun + 5 minutes)
        if (lastRunDt != DateTime.MinValue && status == "idle")
        {
            var nextRoundTime = lastRunDt.AddMinutes(5);
            var timeUntilNext = nextRoundTime - DateTime.Now;
            
            if (timeUntilNext.TotalSeconds > 0)
            {
                var nextRoundStr = nextRoundTime.ToString("HH:mm:ss");
                var countdown = timeUntilNext.TotalMinutes >= 1 
                    ? $"{(int)timeUntilNext.TotalMinutes}m {timeUntilNext.Seconds}s"
                    : $"{(int)timeUntilNext.TotalSeconds}s";
                items.Add(new Markup($"  Next round: [cyan]~{nextRoundStr}[/] [dim](in {countdown})[/]"));
            }
            else
            {
                items.Add(new Markup($"  Next round: [yellow]overdue[/] [dim](expected {FormatAge(-timeUntilNext)} ago)[/]"));
            }
        }

        // Show time since last heartbeat update
        var heartbeatAge = FormatAge(heartbeatFileAge);
        var heartbeatColor = heartbeatFileAge.TotalMinutes < 1 ? "green" : heartbeatFileAge.TotalMinutes < 6 ? "yellow" : "red";
        items.Add(new Markup($"  Heartbeat updated: [{heartbeatColor}]{heartbeatAge}[/]"));
    }
    catch
    {
        items.Add(new Markup("[red]  Error reading heartbeat file[/]"));
    }

    items.Add(Text.Empty);
    return new Rows(items);
}

static IRenderable BuildRalphLogSection(string userProfile)
{
    var items = new List<IRenderable>();
    
    var logPath = Path.Combine(userProfile, ".squad", "ralph-watch.log");
    if (!File.Exists(logPath))
    {
        return Text.Empty; // No log file — skip silently
    }

    var section = new Rule("[cyan]Ralph Recent Rounds[/]") { Justification = Justify.Left };
    items.Add(section);

    try
    {
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        
        var fileLength = fs.Length;
        if (fileLength == 0)
        {
            items.Add(new Markup("[dim]  Log file exists but is empty — waiting for first round to complete[/]"));
            items.Add(Text.Empty);
            return new Rows(items);
        }

        var startPos = Math.Max(0, fileLength - 500);
        fs.Seek(startPos, SeekOrigin.Begin);
        var tail = reader.ReadToEnd();

        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last5 = lines.TakeLast(5);
        
        if (last5.Count() == 0)
        {
            items.Add(new Markup("[dim]  Waiting for round activity...[/]"));
            items.Add(Text.Empty);
            return new Rows(items);
        }

        // Parse log entries: 2026-03-08T16:37:47 | Round=3 | ExitCode=0 | Duration=277.9241812s | ...
        var logEntryRegex = new Regex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\s*\|\s*Round=(\d+)\s*\|\s*ExitCode=(\d+)\s*\|\s*Duration=([\d.]+)s");

        foreach (var line in last5)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var match = logEntryRegex.Match(trimmed);
            if (match.Success)
            {
                var startTimeStr = match.Groups[1].Value;
                var round = match.Groups[2].Value;
                var exitCode = match.Groups[3].Value;
                var durationStr = match.Groups[4].Value;

                if (DateTime.TryParse(startTimeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var startTime) &&
                    double.TryParse(durationStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSecs))
                {
                    var endTime = startTime.AddSeconds(durationSecs);
                    var startLocal = startTime.ToString("HH:mm:ss");
                    var endLocal = endTime.ToString("HH:mm:ss");
                    
                    // Format duration as minutes:seconds
                    var durationMinutes = (int)(durationSecs / 60);
                    var durationSeconds = (int)(durationSecs % 60);
                    var durationFormatted = $"{durationMinutes}m {durationSeconds}s";
                    
                    var statusIcon = exitCode == "0" ? "✅" : "❌";
                    var color = exitCode == "0" ? "green" : "red";
                    
                    items.Add(new Markup($"  [{color}]Round {round} | Started {startLocal} | Finished {endLocal} | Duration {durationFormatted} | {statusIcon}[/]"));
                }
                else
                {
                    // Fallback to original line display
                    var color = trimmed.Contains("✓") ? "green" :
                               trimmed.Contains("→") ? "cyan" :
                               trimmed.Contains("⚠") || trimmed.Contains("WARN") ? "yellow" :
                               trimmed.Contains("✗") || trimmed.Contains("ERROR") ? "red" :
                               "dim";
                    items.Add(new Markup($"  [{color}]{Markup.Escape(trimmed)}[/]"));
                }
            }
            else
            {
                // Non-structured log line (e.g., header)
                var color = trimmed.Contains("✓") ? "green" :
                           trimmed.Contains("→") ? "cyan" :
                           trimmed.Contains("⚠") || trimmed.Contains("WARN") ? "yellow" :
                           trimmed.Contains("✗") || trimmed.Contains("ERROR") ? "red" :
                           "dim";
                items.Add(new Markup($"  [{color}]{Markup.Escape(trimmed)}[/]"));
            }
        }
    }
    catch
    {
        items.Add(new Markup("[red]  Error reading ralph-watch.log[/]"));
    }

    items.Add(Text.Empty);
    return new Rows(items);
}

static IRenderable BuildGitHubIssuesSection(string teamRoot, int maxRows = 8)
{
    var items = new List<IRenderable>();
    
    var section = new Rule("[magenta]GitHub Issues (squad)[/]") { Justification = Justify.Left };
    items.Add(section);

    var output = RunProcess("gh", $"issue list --label squad --json number,title,author,createdAt,labels,assignees --limit {maxRows}", teamRoot);
    if (output == null)
    {
        items.Add(new Markup("[dim]  Could not fetch issues (gh CLI unavailable or not authenticated)[/]"));
        items.Add(Text.Empty);
        return new Rows(items);
    }

    try
    {
        using var doc = JsonDocument.Parse(output);
        var issues = doc.RootElement;

        if (issues.GetArrayLength() == 0)
        {
            items.Add(new Markup("[dim]  No open issues with 'squad' label[/]"));
            items.Add(Text.Empty);
            return new Rows(items);
        }

        var table = new Table()
            .BorderColor(Color.Grey)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").Width(6))
            .AddColumn(new TableColumn("Title").Width(40))
            .AddColumn(new TableColumn("Author").Width(15))
            .AddColumn(new TableColumn("Labels").Width(20))
            .AddColumn(new TableColumn("Assignees").Width(15))
            .AddColumn(new TableColumn("Age").Width(8));

        foreach (var issue in issues.EnumerateArray())
        {
            var number = issue.TryGetProperty("number", out var n) ? n.GetInt32().ToString() : "?";
            var title = issue.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var author = issue.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
            var createdAt = issue.TryGetProperty("createdAt", out var c) && DateTime.TryParse(c.GetString(), out var created) ? FormatAge(DateTime.Now - created.ToLocalTime()) : "?";

            var labelsList = new List<string>();
            if (issue.TryGetProperty("labels", out var labels))
            {
                foreach (var label in labels.EnumerateArray())
                {
                    if (label.TryGetProperty("name", out var name))
                    {
                        var labelName = name.GetString() ?? "";
                        if (!string.IsNullOrEmpty(labelName))
                            labelsList.Add(labelName);
                    }
                }
            }
            var labelsStr = string.Join(", ", labelsList);

            var assigneesList = new List<string>();
            if (issue.TryGetProperty("assignees", out var assignees))
            {
                foreach (var assignee in assignees.EnumerateArray())
                {
                    if (assignee.TryGetProperty("login", out var aLogin))
                    {
                        var assigneeName = aLogin.GetString() ?? "";
                        if (!string.IsNullOrEmpty(assigneeName))
                            assigneesList.Add(assigneeName);
                    }
                }
            }
            var assigneesStr = assigneesList.Count > 0 ? string.Join(", ", assigneesList) : "[dim]none[/]";

            if (title.Length > 40)
                title = title.Substring(0, 37) + "...";

            table.AddRow(
                $"[cyan]{Markup.Escape(number)}[/]",
                Markup.Escape(title),
                $"[yellow]{Markup.Escape(author)}[/]",
                $"[dim]{Markup.Escape(labelsStr)}[/]",
                assigneesStr,
                $"[dim]{Markup.Escape(createdAt)}[/]"
            );
        }

        items.Add(table);
    }
    catch
    {
        items.Add(new Markup("[red]  Error parsing issue data[/]"));
    }

    items.Add(Text.Empty);
    return new Rows(items);
}

static IRenderable BuildGitHubPRsSection(string teamRoot)
{
    var items = new List<IRenderable>();
    
    var section = new Rule("[magenta]GitHub Pull Requests (Open)[/]") { Justification = Justify.Left };
    items.Add(section);

    var output = RunProcess("gh", "pr list --json number,title,author,createdAt,headRefName,reviewDecision,statusCheckRollup,isDraft --limit 20", teamRoot);
    if (output == null)
    {
        items.Add(new Markup("[dim]  Could not fetch PRs (gh CLI unavailable or not authenticated)[/]"));
        items.Add(Text.Empty);
        return new Rows(items);
    }

    try
    {
        using var doc = JsonDocument.Parse(output);
        var prs = doc.RootElement;

        if (prs.GetArrayLength() == 0)
        {
            items.Add(new Markup("[dim]  No open pull requests[/]"));
            items.Add(Text.Empty);
            return new Rows(items);
        }

        var table = new Table()
            .BorderColor(Color.Grey)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").Width(6))
            .AddColumn(new TableColumn("Title").Width(35))
            .AddColumn(new TableColumn("Author").Width(12))
            .AddColumn(new TableColumn("Branch").Width(20))
            .AddColumn(new TableColumn("Review").Width(10))
            .AddColumn(new TableColumn("CI").Width(8))
            .AddColumn(new TableColumn("Age").Width(8));

        foreach (var pr in prs.EnumerateArray())
        {
            var number = pr.TryGetProperty("number", out var n) ? n.GetInt32().ToString() : "?";
            var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var author = pr.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
            var branch = pr.TryGetProperty("headRefName", out var b) ? b.GetString() ?? "" : "";
            var createdAt = pr.TryGetProperty("createdAt", out var c) && DateTime.TryParse(c.GetString(), out var created) ? FormatAge(DateTime.Now - created.ToLocalTime()) : "?";
            var isDraft = pr.TryGetProperty("isDraft", out var d) && d.GetBoolean();

            var reviewDecision = pr.TryGetProperty("reviewDecision", out var rd) ? rd.GetString() ?? "" : "";
            var reviewStatus = reviewDecision switch
            {
                "APPROVED" => "[green]✓[/]",
                "CHANGES_REQUESTED" => "[red]✗[/]",
                "REVIEW_REQUIRED" => "[yellow]?[/]",
                _ => "[dim]—[/]"
            };

            var ciStatus = "[dim]—[/]";
            if (pr.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
            {
                var statuses = rollup.EnumerateArray().ToList();
                if (statuses.Count > 0)
                {
                    var allSuccess = statuses.All(s =>
                    {
                        if (s.TryGetProperty("__typename", out var tn))
                        {
                            var typename = tn.GetString();
                            if (typename == "CheckRun" && s.TryGetProperty("conclusion", out var conclusion))
                                return conclusion.GetString() == "SUCCESS";
                            if (typename == "StatusContext" && s.TryGetProperty("state", out var state))
                                return state.GetString() == "SUCCESS";
                        }
                        return false;
                    });

                    var anyPending = statuses.Any(s =>
                    {
                        if (s.TryGetProperty("__typename", out var tn))
                        {
                            var typename = tn.GetString();
                            if (typename == "CheckRun" && s.TryGetProperty("status", out var status))
                                return status.GetString() == "IN_PROGRESS" || status.GetString() == "QUEUED";
                            if (typename == "StatusContext" && s.TryGetProperty("state", out var state))
                                return state.GetString() == "PENDING";
                        }
                        return false;
                    });

                    ciStatus = allSuccess ? "[green]✓[/]" : anyPending ? "[yellow]…[/]" : "[red]✗[/]";
                }
            }

            if (title.Length > 35)
                title = title.Substring(0, 32) + "...";
            if (branch.Length > 20)
                branch = branch.Substring(0, 17) + "...";

            var titleMarkup = isDraft ? $"[dim]{Markup.Escape(title)} (draft)[/]" : Markup.Escape(title);

            table.AddRow(
                $"[cyan]{Markup.Escape(number)}[/]",
                titleMarkup,
                $"[yellow]{Markup.Escape(author)}[/]",
                $"[dim]{Markup.Escape(branch)}[/]",
                reviewStatus,
                ciStatus,
                $"[dim]{Markup.Escape(createdAt)}[/]"
            );
        }

        items.Add(table);
    }
    catch
    {
        items.Add(new Markup("[red]  Error parsing PR data[/]"));
    }

    items.Add(Text.Empty);
    return new Rows(items);
}

static IRenderable BuildRecentlyMergedPRsSection(string teamRoot)
{
    var items = new List<IRenderable>();
    
    var section = new Rule("[magenta]GitHub Pull Requests (Recently Merged)[/]") { Justification = Justify.Left };
    items.Add(section);

    // Get closed PRs from the last 7 days
    var output = RunProcess("gh", "pr list --state merged --limit 10 --json number,title,author,mergedAt,headRefName", teamRoot);
    if (output == null)
    {
        items.Add(new Markup("[dim]  Could not fetch merged PRs[/]"));
        items.Add(Text.Empty);
        return new Rows(items);
    }

    try
    {
        using var doc = JsonDocument.Parse(output);
        var prs = doc.RootElement;

        if (prs.GetArrayLength() == 0)
        {
            items.Add(new Markup("[dim]  No recently merged pull requests[/]"));
            items.Add(Text.Empty);
            return new Rows(items);
        }

        var table = new Table()
            .BorderColor(Color.Grey)
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").Width(6))
            .AddColumn(new TableColumn("Title").Width(40))
            .AddColumn(new TableColumn("Author").Width(12))
            .AddColumn(new TableColumn("Branch").Width(20))
            .AddColumn(new TableColumn("Merged").Width(10));

        foreach (var pr in prs.EnumerateArray())
        {
            var number = pr.TryGetProperty("number", out var n) ? n.GetInt32().ToString() : "?";
            var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var author = pr.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
            var branch = pr.TryGetProperty("headRefName", out var b) ? b.GetString() ?? "" : "";
            var mergedAt = pr.TryGetProperty("mergedAt", out var m) && DateTime.TryParse(m.GetString(), out var merged) ? FormatAge(DateTime.Now - merged.ToLocalTime()) : "?";

            if (title.Length > 40)
                title = title.Substring(0, 37) + "...";
            if (branch.Length > 20)
                branch = branch.Substring(0, 17) + "...";

            table.AddRow(
                $"[green]{Markup.Escape(number)}[/]",
                Markup.Escape(title),
                $"[yellow]{Markup.Escape(author)}[/]",
                $"[dim]{Markup.Escape(branch)}[/]",
                $"[green]{Markup.Escape(mergedAt)}[/]"
            );
        }

        items.Add(table);
    }
    catch
    {
        items.Add(new Markup("[red]  Error parsing merged PR data[/]"));
    }

    items.Add(Text.Empty);
    return new Rows(items);
}

static IRenderable BuildOrchestrationLogSection(List<AgentActivity> activities)
{
    var items = new List<IRenderable>();
    
    var section = new Rule("[yellow]Orchestration Activity (24h)[/]") { Justification = Justify.Left };
    items.Add(section);

    if (activities.Count == 0)
    {
        items.Add(new Markup("[dim]  No activities found in orchestration logs.[/]"));
        items.Add(Text.Empty);
        return new Rows(items);
    }

    var now = DateTime.Now;
    var recentActivities = activities.Count(a => (now - a.Timestamp).TotalHours <= 24);
    var uniqueAgents = activities.Select(a => a.Agent).Distinct().ToList();
    var totalAgents = uniqueAgents.Count;

    var top10 = activities.Take(10).ToList();

    var table = new Table()
        .BorderColor(Color.Grey)
        .Border(TableBorder.Rounded)
        .AddColumn(new TableColumn("Agent").Width(10))
        .AddColumn(new TableColumn("Activity").Width(50))
        .AddColumn(new TableColumn("Age").Width(10));

    foreach (var activity in top10)
    {
        var age = FormatAge(now - activity.Timestamp);
        var activityText = activity.Task;
        if (activityText.Length > 50)
            activityText = activityText.Substring(0, 47) + "...";

        var agentName = CapitalizeAgent(activity.Agent);

        table.AddRow(
            $"[cyan]{Markup.Escape(agentName)}[/]",
            $"[white]{Markup.Escape(activityText)}[/]",
            $"[dim]{Markup.Escape(age)}[/]"
        );
    }

    items.Add(table);
    items.Add(new Markup($"[dim]  Agents: {totalAgents} | Activities (24h): {recentActivities} | Showing top 10 of {activities.Count}[/]"));
    items.Add(Text.Empty);
    
    return new Rows(items);
}

static IRenderable BuildDetailedOrchestrationSection(List<AgentActivity> activities, DateTime now)
{
    var items = new List<IRenderable>();
    
    var section = new Rule("[yellow bold]Orchestration Activity — Detailed View[/]") { Justification = Justify.Left };
    items.Add(section);
    items.Add(Text.Empty);

    if (activities.Count == 0)
    {
        items.Add(new Markup("[dim]  No activities found in orchestration logs.[/]"));
        items.Add(Text.Empty);
        return new Rows(items);
    }

    // Show statistics
    var totalActivities = activities.Count;
    var recentActivities = activities.Count(a => (now - a.Timestamp).TotalHours <= 24);
    var uniqueAgents = activities.Select(a => a.Agent).Distinct().ToList();
    var totalAgents = uniqueAgents.Count;
    
    var activeCount = activities.Count(a => a.Status.Contains("Progress", StringComparison.OrdinalIgnoreCase) || a.Status.Contains("⏳"));
    var completedCount = activities.Count(a => a.Status.Contains("Completed", StringComparison.OrdinalIgnoreCase) || a.Status.Contains("✅"));
    var failedCount = activities.Count(a => a.Status.Contains("Failed", StringComparison.OrdinalIgnoreCase) || a.Status.Contains("❌"));

    var statsPanel = new Panel(new Markup(
        $"[cyan]Total Activities:[/] {totalActivities}  |  " +
        $"[yellow]Last 24h:[/] {recentActivities}  |  " +
        $"[cyan]Active Agents:[/] {totalAgents}\n" +
        $"[yellow]⏳ In Progress:[/] {activeCount}  |  " +
        $"[green]✅ Completed:[/] {completedCount}  |  " +
        $"[red]❌ Failed:[/] {failedCount}"))
    {
        Header = new PanelHeader("[bold]Statistics[/]"),
        Border = BoxBorder.Rounded,
        BorderStyle = new Style(Color.Grey)
    };
    items.Add(statsPanel);
    items.Add(Text.Empty);

    // Display detailed activity entries (up to 25)
    var displayCount = Math.Min(25, activities.Count);
    items.Add(new Markup($"[dim]Showing {displayCount} most recent activities:[/]"));
    items.Add(Text.Empty);

    foreach (var activity in activities.Take(displayCount))
    {
        var age = now - activity.Timestamp;
        var ageStr = FormatAge(age);
        
        var statusColor = activity.Status.Contains("✅") || activity.Status.Contains("Completed", StringComparison.OrdinalIgnoreCase) ? "green" :
                         activity.Status.Contains("⏳") || activity.Status.Contains("Progress", StringComparison.OrdinalIgnoreCase) ? "yellow" :
                         activity.Status.Contains("❌") || activity.Status.Contains("Failed", StringComparison.OrdinalIgnoreCase) ? "red" :
                         "blue";

        var ageColor = age.TotalHours < 1 ? "green" :
                       age.TotalDays < 1 ? "yellow" :
                       "dim";

        // Create detailed entry with grid layout
        var grid = new Grid()
            .AddColumn(new GridColumn().Width(15))
            .AddColumn(new GridColumn().NoWrap())
            .AddRow($"[cyan bold]{Markup.Escape(activity.Agent)}[/]", $"[{ageColor}]{Markup.Escape(ageStr)} — {Markup.Escape(activity.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))}[/]")
            .AddRow($"[dim]Status:[/]", $"[{statusColor} bold]{Markup.Escape(activity.Status)}[/]");

        if (!string.IsNullOrWhiteSpace(activity.Task))
        {
            grid.AddRow($"[dim]Task:[/]", $"[white]{Markup.Escape(activity.Task)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(activity.Outcome))
        {
            grid.AddRow($"[dim]Outcome:[/]", $"[dim]{Markup.Escape(activity.Outcome)}[/]");
        }

        var panel = new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0, 1, 0)
        };

        items.Add(panel);
        items.Add(Text.Empty);
    }

    if (activities.Count > displayCount)
    {
        items.Add(new Markup($"[dim]... and {activities.Count - displayCount} more activities[/]"));
        items.Add(Text.Empty);
    }

    return new Rows(items);
}

// ─── Ralph Heartbeat Panel ──────────────────────────────────────────────────

static void DisplayRalphHeartbeat(string userProfile)
{
    var section = new Rule("[cyan]Ralph Watch Loop[/]") { Justification = Justify.Left };
    AnsiConsole.Write(section);

    var heartbeatPath = Path.Combine(userProfile, ".squad", "ralph-heartbeat.json");
    if (!File.Exists(heartbeatPath))
    {
        AnsiConsole.MarkupLine("[dim]  No heartbeat file found — ralph-watch may not be running[/]");
        AnsiConsole.WriteLine();
        return;
    }

    try
    {
        var json = File.ReadAllText(heartbeatPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var lastRun = root.TryGetProperty("lastRun", out var lr) ? lr.GetString() : null;
        var round = root.TryGetProperty("round", out var rn) ? rn.ToString() : "?";
        var status = root.TryGetProperty("status", out var st) ? st.GetString() : "unknown";
        var consecutiveFailures = root.TryGetProperty("consecutiveFailures", out var cf) ? cf.GetInt32() : 0;
        var pid = root.TryGetProperty("pid", out var p) ? p.ToString() : "?";
        
        // Extract metrics if available
        var metricsText = "";
        if (root.TryGetProperty("metrics", out var metrics))
        {
            var issuesClosed = metrics.TryGetProperty("issuesClosed", out var ic) ? ic.GetInt32() : 0;
            var prsMerged = metrics.TryGetProperty("prsMerged", out var pm) ? pm.GetInt32() : 0;
            var agentActions = metrics.TryGetProperty("agentActions", out var aa) ? aa.GetInt32() : 0;
            
            if (issuesClosed > 0 || prsMerged > 0 || agentActions > 0)
            {
                metricsText = $"\n  Metrics: [cyan]{issuesClosed}[/] issues closed, [cyan]{prsMerged}[/] PRs merged, [cyan]{agentActions}[/] agent actions";
            }
        }

        var staleness = "unknown";
        var stalenessColor = "dim";
        if (lastRun != null && DateTime.TryParse(lastRun, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var lastRunDt))
        {
            var age = DateTime.Now - lastRunDt;
            staleness = FormatAge(age);
            stalenessColor = age.TotalMinutes < 10 ? "green" : age.TotalMinutes < 30 ? "yellow" : "red";
        }

        var statusColor = status == "running" ? "green" : status == "idle" ? "yellow" : "red";
        var failColor = consecutiveFailures == 0 ? "green" : consecutiveFailures < 3 ? "yellow" : "red";

        AnsiConsole.MarkupLine($"  Status: [{statusColor}]{Markup.Escape(status ?? "unknown")}[/]  |  " +
                               $"Round: [white]{Markup.Escape(round)}[/]  |  " +
                               $"Last run: [{stalenessColor}]{Markup.Escape(staleness)}[/]  |  " +
                               $"Failures: [{failColor}]{consecutiveFailures}[/]  |  " +
                               $"PID: [dim]{Markup.Escape(pid)}[/]" +
                               metricsText);
    }
    catch
    {
        AnsiConsole.MarkupLine("[red]  Error reading heartbeat file[/]");
    }

    AnsiConsole.WriteLine();
}

// ─── Ralph Watch Log Panel ──────────────────────────────────────────────────

static void DisplayRalphLog(string userProfile)
{
    var logPath = Path.Combine(userProfile, ".squad", "ralph-watch.log");
    if (!File.Exists(logPath))
    {
        return; // No log file — skip silently
    }

    var section = new Rule("[cyan]Ralph Recent Rounds[/]") { Justification = Justify.Left };
    AnsiConsole.Write(section);

    try
    {
        // Read last 500 chars to get recent entries
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var tailSize = Math.Min(2000, fs.Length);
        fs.Seek(-tailSize, SeekOrigin.End);
        using var reader = new StreamReader(fs);
        var tail = reader.ReadToEnd();

        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Show last 5 meaningful lines
        var recentLines = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(5)
            .ToList();

        if (recentLines.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]  Log file exists but is empty[/]");
        }
        else
        {
            foreach (var line in recentLines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 120)
                    trimmed = trimmed[..120] + "…";
                // Color errors/warnings
                var color = trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? "red" :
                           trimmed.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "yellow" :
                           trimmed.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) ? "green" :
                           "dim";
                AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape(trimmed)}[/]");
            }
        }
    }
    catch
    {
        AnsiConsole.MarkupLine("[red]  Error reading ralph-watch.log[/]");
    }

    AnsiConsole.WriteLine();
}

// ─── GitHub Issues Panel ────────────────────────────────────────────────────

static void DisplayGitHubIssues(string teamRoot)
{
    var section = new Rule("[cyan]GitHub Issues (Open)[/]") { Justification = Justify.Left };
    AnsiConsole.Write(section);

    var json = RunProcess("gh", "issue list --state open --label squad --limit 15 --json number,title,labels,assignees,updatedAt", teamRoot);
    if (json == null)
    {
        AnsiConsole.MarkupLine("[dim]  Could not fetch issues (gh CLI unavailable or not authenticated)[/]");
        AnsiConsole.WriteLine();
        return;
    }

    try
    {
        using var doc = JsonDocument.Parse(json);
        var issues = doc.RootElement;

        if (issues.GetArrayLength() == 0)
        {
            AnsiConsole.MarkupLine("[dim]  No open issues with 'squad' label[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("[bold]#[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Title[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Labels[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Assignee[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Updated[/]").RightAligned());

        foreach (var issue in issues.EnumerateArray())
        {
            var number = issue.GetProperty("number").GetInt32();
            var title = issue.GetProperty("title").GetString() ?? "";
            if (title.Length > 60) title = title[..60] + "…";

            var labels = string.Join(", ", issue.GetProperty("labels").EnumerateArray()
                .Select(l => l.GetProperty("name").GetString() ?? "")
                .Where(l => l != "squad")); // Don't repeat the filter label

            var assignees = string.Join(", ", issue.GetProperty("assignees").EnumerateArray()
                .Select(a => a.GetProperty("login").GetString() ?? ""));
            if (string.IsNullOrEmpty(assignees)) assignees = "unassigned";

            var updatedStr = "";
            if (issue.TryGetProperty("updatedAt", out var updatedAt) &&
                DateTime.TryParse(updatedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var updatedDt))
            {
                updatedStr = FormatAge(DateTime.Now - updatedDt.ToLocalTime());
            }

            // Status heuristic from labels
            var statusColor = labels.Contains("in-progress") ? "yellow" :
                             labels.Contains("assigned") || !string.IsNullOrEmpty(assignees) && assignees != "unassigned" ? "blue" :
                             "dim";

            table.AddRow(
                $"[white]#{number}[/]",
                $"[{statusColor}]{Markup.Escape(title)}[/]",
                $"[dim]{Markup.Escape(labels)}[/]",
                $"[cyan]{Markup.Escape(assignees)}[/]",
                $"[dim]{Markup.Escape(updatedStr)}[/]"
            );
        }

        AnsiConsole.Write(table);
    }
    catch
    {
        AnsiConsole.MarkupLine("[red]  Error parsing issue data[/]");
    }

    AnsiConsole.WriteLine();
}

// ─── GitHub PRs Panel ───────────────────────────────────────────────────────

static void DisplayGitHubPRs(string teamRoot)
{
    var section = new Rule("[cyan]GitHub Pull Requests (Open)[/]") { Justification = Justify.Left };
    AnsiConsole.Write(section);

    var json = RunProcess("gh", "pr list --state open --limit 10 --json number,title,author,reviewDecision,statusCheckRollup,updatedAt,isDraft,headRefName", teamRoot);
    if (json == null)
    {
        AnsiConsole.MarkupLine("[dim]  Could not fetch PRs (gh CLI unavailable or not authenticated)[/]");
        AnsiConsole.WriteLine();
        return;
    }

    try
    {
        using var doc = JsonDocument.Parse(json);
        var prs = doc.RootElement;

        if (prs.GetArrayLength() == 0)
        {
            AnsiConsole.MarkupLine("[dim]  No open pull requests[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("[bold]#[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Title[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Author[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Review[/]").Centered());
        table.AddColumn(new TableColumn("[bold]CI[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Updated[/]").RightAligned());

        foreach (var pr in prs.EnumerateArray())
        {
            var number = pr.GetProperty("number").GetInt32();
            var title = pr.GetProperty("title").GetString() ?? "";
            var isDraft = pr.TryGetProperty("isDraft", out var d) && d.GetBoolean();
            if (isDraft) title = "[draft] " + title;
            if (title.Length > 55) title = title[..55] + "…";

            var author = pr.TryGetProperty("author", out var auth) && auth.TryGetProperty("login", out var login)
                ? login.GetString() ?? "" : "";

            // Review decision
            var reviewDecision = pr.TryGetProperty("reviewDecision", out var rd) ? rd.GetString() ?? "" : "";
            var reviewDisplay = reviewDecision switch
            {
                "APPROVED" => "[green]✓ Approved[/]",
                "CHANGES_REQUESTED" => "[red]✗ Changes[/]",
                "REVIEW_REQUIRED" => "[yellow]⏳ Pending[/]",
                _ => "[dim]—[/]"
            };

            // CI status rollup
            var ciDisplay = "[dim]—[/]";
            if (pr.TryGetProperty("statusCheckRollup", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                var total = checks.GetArrayLength();
                var success = 0;
                var fail = 0;
                var pending = 0;
                foreach (var check in checks.EnumerateArray())
                {
                    var conclusion = check.TryGetProperty("conclusion", out var c) ? c.GetString() ?? "" : "";
                    var checkStatus = check.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    if (conclusion == "SUCCESS") success++;
                    else if (conclusion == "FAILURE" || conclusion == "ERROR") fail++;
                    else if (checkStatus == "IN_PROGRESS" || checkStatus == "QUEUED" || checkStatus == "PENDING") pending++;
                }

                if (fail > 0)
                    ciDisplay = $"[red]✗ {fail}/{total}[/]";
                else if (pending > 0)
                    ciDisplay = $"[yellow]⏳ {pending}/{total}[/]";
                else if (success == total && total > 0)
                    ciDisplay = $"[green]✓ {success}/{total}[/]";
            }

            var updatedStr = "";
            if (pr.TryGetProperty("updatedAt", out var updatedAt) &&
                DateTime.TryParse(updatedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var updatedDt))
            {
                updatedStr = FormatAge(DateTime.Now - updatedDt.ToLocalTime());
            }

            table.AddRow(
                $"[white]#{number}[/]",
                $"[white]{Markup.Escape(title)}[/]",
                $"[cyan]{Markup.Escape(author)}[/]",
                reviewDisplay,
                ciDisplay,
                $"[dim]{Markup.Escape(updatedStr)}[/]"
            );
        }

        AnsiConsole.Write(table);
    }
    catch
    {
        AnsiConsole.MarkupLine("[red]  Error parsing PR data[/]");
    }

    AnsiConsole.WriteLine();
}

static void DisplayRecentlyMergedPRs(string teamRoot)
{
    var section = new Rule("[cyan]GitHub Pull Requests (Recently Merged)[/]") { Justification = Justify.Left };
    AnsiConsole.Write(section);

    var json = RunProcess("gh", "pr list --state merged --limit 10 --json number,title,author,mergedAt,headRefName", teamRoot);
    if (json == null)
    {
        AnsiConsole.MarkupLine("[dim]  Could not fetch merged PRs[/]");
        AnsiConsole.WriteLine();
        return;
    }

    try
    {
        using var doc = JsonDocument.Parse(json);
        var prs = doc.RootElement;

        if (prs.GetArrayLength() == 0)
        {
            AnsiConsole.MarkupLine("[dim]  No recently merged pull requests[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("[bold]#[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Title[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Author[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Branch[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Merged[/]").RightAligned());

        foreach (var pr in prs.EnumerateArray())
        {
            var number = pr.GetProperty("number").GetInt32();
            var title = pr.GetProperty("title").GetString() ?? "";
            if (title.Length > 50) title = title[..50] + "…";

            var author = pr.TryGetProperty("author", out var auth) && auth.TryGetProperty("login", out var login)
                ? login.GetString() ?? "" : "";
            
            var branch = pr.TryGetProperty("headRefName", out var b) ? b.GetString() ?? "" : "";
            if (branch.Length > 25) branch = branch[..25] + "…";

            var mergedStr = "";
            if (pr.TryGetProperty("mergedAt", out var mergedAt) &&
                DateTime.TryParse(mergedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var mergedDt))
            {
                mergedStr = FormatAge(DateTime.Now - mergedDt.ToLocalTime());
            }

            table.AddRow(
                $"[green]#{number}[/]",
                $"[white]{Markup.Escape(title)}[/]",
                $"[cyan]{Markup.Escape(author)}[/]",
                $"[dim]{Markup.Escape(branch)}[/]",
                $"[green]{Markup.Escape(mergedStr)}[/]"
            );
        }

        AnsiConsole.Write(table);
    }
    catch
    {
        AnsiConsole.MarkupLine("[red]  Error parsing merged PR data[/]");
    }

    AnsiConsole.WriteLine();
}

// ─── Orchestration Log Panel ────────────────────────────────────────────────

static List<AgentActivity> LoadActivities(string teamRoot)
{
    var activities = new List<AgentActivity>();
    var orchestrationLogPath = Path.Combine(teamRoot, ".squad", "orchestration-log");

    if (!Directory.Exists(orchestrationLogPath))
        return activities;

    var logFiles = Directory.GetFiles(orchestrationLogPath, "*.md")
        .OrderByDescending(f => File.GetLastWriteTime(f))
        .Take(20);

    foreach (var file in logFiles)
    {
        try
        {
            var activity = ParseOrchestrationLog(file);
            if (activity != null)
                activities.Add(activity);
        }
        catch
        {
            // Skip malformed files
        }
    }

    return activities.OrderByDescending(a => a.Timestamp).ToList();
}

static AgentActivity? ParseOrchestrationLog(string filePath)
{
    var content = File.ReadAllText(filePath);
    var fileName = Path.GetFileNameWithoutExtension(filePath);

    var match = Regex.Match(fileName, @"^(\d{4})-(\d{2})-(\d{2})T(\d{2})-(\d{2})-(\d{2})Z-(.+)$");
    if (!match.Success) return null;

    var timestamp = new DateTime(
        int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
        int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value), int.Parse(match.Groups[6].Value),
        DateTimeKind.Local);
    var agentName = match.Groups[7].Value;

    var status = "Unknown";
    var statusMatch = Regex.Match(content, @"\*\*Status:\*\*\s*(.+?)(?:\r?\n|$)");
    if (statusMatch.Success)
    {
        status = statusMatch.Groups[1].Value.Trim();
    }
    else
    {
        // Try alternate status patterns
        var altStatusMatch = Regex.Match(content, @"^##\s*Status:\s*(.+?)(?:\r?\n|$)", RegexOptions.Multiline);
        if (altStatusMatch.Success)
            status = altStatusMatch.Groups[1].Value.Trim();
    }
    
    // If status is still Unknown, check for emojis or common patterns in the content
    if (status == "Unknown")
    {
        if (content.Contains("✅") || Regex.IsMatch(content, @"(?i)(completed|success|done)"))
            status = "✅ Completed";
        else if (content.Contains("⏳") || Regex.IsMatch(content, @"(?i)in.progress"))
            status = "⏳ In Progress";
        else if (content.Contains("❌") || Regex.IsMatch(content, @"(?i)failed"))
            status = "❌ Failed";
    }

    var task = "";
    var assignmentMatch = Regex.Match(content, @"## Assignment\s*(.+?)(?=##|$)", RegexOptions.Singleline);
    if (assignmentMatch.Success)
    {
        task = assignmentMatch.Groups[1].Value.Trim().Replace("\r", "").Replace("\n", " ");
        if (task.Length > 150) task = task[..150] + "...";
    }

    var outcome = "";
    var outcomeMatch = Regex.Match(content, @"\*\*Result:\*\*\s*(.+?)(?:\r?\n|$)");
    if (outcomeMatch.Success)
    {
        outcome = outcomeMatch.Groups[1].Value.Trim();
    }
    else
    {
        var outcomeSection = Regex.Match(content, @"## Outcome\s*(.+?)(?=##|$)", RegexOptions.Singleline);
        if (outcomeSection.Success)
        {
            var lines = outcomeSection.Groups[1].Value.Trim().Split('\n');
            outcome = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            if (outcome.Length > 100) outcome = outcome[..100] + "...";
        }
    }

    return new AgentActivity
    {
        Agent = CapitalizeAgent(agentName),
        Timestamp = timestamp,
        Status = status,
        Task = task,
        Outcome = outcome
    };
}

static void DisplayOrchestrationLog(List<AgentActivity> activities)
{
    var now = DateTime.Now;

    var section = new Rule("[cyan]Orchestration Log (Recent)[/]") { Justification = Justify.Left };
    AnsiConsole.Write(section);

    if (activities.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]  No activities found in orchestration logs.[/]");
        AnsiConsole.WriteLine();
        return;
    }

    var table = new Table();
    table.Border(TableBorder.Simple);
    table.AddColumn(new TableColumn("[bold]Agent[/]").Centered());
    table.AddColumn(new TableColumn("[bold]Status[/]").Centered());
    table.AddColumn(new TableColumn("[bold]Age[/]").Centered());
    table.AddColumn(new TableColumn("[bold]Task[/]").LeftAligned());
    table.AddColumn(new TableColumn("[bold]Outcome[/]").LeftAligned());

    foreach (var activity in activities.Take(10))
    {
        var age = now - activity.Timestamp;
        var ageStr = FormatAge(age);

        var statusColor = activity.Status.Contains("✅") || activity.Status.Contains("Completed") ? "green" :
                         activity.Status.Contains("⏳") || activity.Status.Contains("Progress") ? "yellow" :
                         activity.Status.Contains("❌") || activity.Status.Contains("Failed") ? "red" :
                         "blue";

        table.AddRow(
            $"[cyan]{Markup.Escape(activity.Agent)}[/]",
            $"[{statusColor}]{Markup.Escape(activity.Status)}[/]",
            age.TotalHours < 1 ? $"[green]{Markup.Escape(ageStr)}[/]" :
                age.TotalDays < 1 ? $"[yellow]{Markup.Escape(ageStr)}[/]" :
                $"[dim]{Markup.Escape(ageStr)}[/]",
            $"[white]{Markup.Escape(activity.Task)}[/]",
            !string.IsNullOrEmpty(activity.Outcome)
                ? $"[dim]{Markup.Escape(activity.Outcome)}[/]"
                : "[dim]-[/]"
        );
    }

    AnsiConsole.Write(table);

    var totalAgents = activities.Select(a => a.Agent).Distinct().Count();
    var recentActivities = activities.Count(a => (now - a.Timestamp).TotalHours < 24);
    AnsiConsole.MarkupLine($"[dim]  Agents: {totalAgents} | Activities (24h): {recentActivities} | Showing top 10 of {activities.Count}[/]");
    AnsiConsole.WriteLine();
}

record AgentActivity
{
    public required string Agent { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Status { get; init; }
    public required string Task { get; init; }
    public required string Outcome { get; init; }
}
